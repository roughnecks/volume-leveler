// MainForm.cs (file completo)
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using static VolumeLeveler.ProcessLoopbackInterop;

namespace VolumeLeveler
{
    // MainForm owns the whole leveling pipeline for one running instance
    // of Volume Leveler:
    //
    // - A discovery thread periodically scans active Windows audio
    //   sessions, starts a capture pipeline for each newly-seen app, and
    //   removes one only when its process has genuinely exited or its
    //   capture has silently stalled (see RunDiscoveryLoop).
    //
    // - Each tracked app gets its own capture thread using per-process
    //   WASAPI loopback (see RunCaptureLoop), which measures RMS
    //   continuously and feeds both an instantaneous/smoothed reading
    //   (for display) and a running calibration average.
    //
    // - A single shared AGC thread (see RunAgcLoop) periodically checks
    //   every tracked app and applies at most one volume correction per
    //   cycle: a one-shot calibration when an app is new, when the
    //   shared reference level changes, or when a new "streak" of audio
    //   begins (track/video change), plus an occasional drift-based
    //   recalibration otherwise. A correction is a single proportional
    //   volume change (see ComputeLeveledVolume) applied as a short ramp
    //   (see RampSessionsVolume), never a continuous nudging loop - see
    //   the AGC section further down for the reasoning behind the
    //   calibration-window/warmup/level-shift design.
    //
    // - The reference level is a single 0-100% slider, decoupled from
    //   any specific app, with a convenience button to seed it from a
    //   chosen app's current measured level.
    //
    // UI-wise: the app runs mainly from the system tray (its own icon,
    // a website link, help, and checkboxes to auto-start leveling and/or
    // Windows itself), plus a window with the reference slider, presets,
    // detected/tracked-app lists, and a blacklist. A global pause hotkey
    // (Ctrl+Alt+L) is available at all times. Settings are persisted to
    // %AppData%\VolumeLeveler\settings.json - see Settings.cs.
    public partial class MainForm : Form
    {
        private readonly MMDeviceEnumerator _deviceEnumerator = new();

        private const string AppVersion = "1.2.1";

        private Label _statusLabel = null!;
        private Button _startButton = null!;
        private Button _stopButton = null!;

        private Label _referenceLabel = null!;
        private TrackBar _referenceTrackBar = null!;
        private ComboBox _seedFromAppComboBox = null!;
        private Button _seedReferenceButton = null!;

        private Label _detectedAppsLabel = null!;
        private ListBox _detectedAppsListBox = null!;
        private Button _refreshAppsButton = null!;

        private Label _activeAppsLabel = null!;
        private ListBox _activeAppsListBox = null!;
        private System.Windows.Forms.Timer _uiRefreshTimer = null!;

        // System tray icon and its context menu.
        private NotifyIcon _trayIcon = null!;
        private ContextMenuStrip _trayMenu = null!;

        // Tray menu checkboxes: whether tracking/leveling itself starts
        // automatically on launch, and whether Volume Leveler starts
        // with Windows. The latter's checked state is always read fresh
        // from AutoStartHelper.IsEnabledAsync() every time the tray menu
        // opens (see RefreshAutoStartCheckboxAsync), rather than from a
        // settings-file flag, so it can never drift out of sync with
        // what's actually registered - including changes made from Task
        // Manager's Startup tab.
        private ToolStripMenuItem _autoStartLevelingMenuItem = null!;
        private ToolStripMenuItem _autoStartWithWindowsMenuItem = null!;

        private Panel _headerPanel = null!;
        private Button _helpButton = null!;
        private Panel _startStopPanel = null!;
        private Button _resetVolumesButton = null!;

        // Last known Windows volume for each tracked app, keyed by
        // process name (case-insensitive) - restored on the next launch
        // instead of always forcing InitialSeedVolume, so a restart
        // doesn't silently undo whatever level a previous session had
        // converged on. See LoadPersistedSettings, CreateAndStartAppState,
        // BuildCurrentSettings.
        private readonly Dictionary<string, float> _persistedVolumes =
            new(StringComparer.OrdinalIgnoreCase);

        // Blacklist: hardcoded names are always excluded; _userBlacklist
        // is editable at runtime and persisted. See IsProcessExcluded,
        // AddToBlacklist, RemoveFromBlacklistButtonClicked.
        private static readonly HashSet<string> HardcodedExcludedProcessNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Known Voicemeeter executable names across its editions
                // (Standard/Banana/Potato). If a different Voicemeeter
                // build uses another name, check Task Manager's "Details"
                // tab for the exact process name and add it to the
                // user-editable blacklist below instead of relying on
                // this hardcoded set.
                "voicemeeter", "voicemeeter8x64", "voicemeeterpro", "voicemeeter8"
            };
        private readonly HashSet<string> _userBlacklist = new(StringComparer.OrdinalIgnoreCase);
        private List<DetectedAppInfo> _lastDetectedGroups = new();

        private ListBox _blacklistListBox = null!;
        private Button _removeFromBlacklistButton = null!;
        private ContextMenuStrip _detectedAppsContextMenu = null!;

        // Parallel list of process names, in the exact same order as the
        // rows just written into _activeAppsListBox - lets
        // ActiveAppsListBox_MouseUp map a right-clicked row back to a
        // real process name, the same way _lastDetectedGroups does for
        // "Detected apps" (see DetectedAppsListBox_MouseUp).
        private List<string> _lastActiveAppsOrder = new();
        private ContextMenuStrip _activeAppsContextMenu = null!;

        // Three fixed reference preset slots.
        private ComboBox _presetComboBox = null!;
        private TextBox _presetLabelTextBox = null!;
        private Button _savePresetButton = null!;
        private Button _loadPresetButton = null!;
        private readonly Preset[] _presets =
        {
            new() { Label = "Preset 1", ReferencePercent = 50 },
            new() { Label = "Preset 2", ReferencePercent = 50 },
            new() { Label = "Preset 3", ReferencePercent = 50 }
        };

        // True until the very first time WinForms tries to make this
        // form visible - see SetVisibleCore, which swallows that first
        // attempt so the app starts minimized to the tray.
        private bool _startingUp = true;

        // Whether the exit confirmation dialog still appears when the
        // user closes the window - flips to false once the user ticks
        // "Don't ask me again". See OnFormClosing / ExitConfirmationDialog.
        private bool _showExitConfirmation = true;

        // Discovery: started once on the first Start press, runs until the
        // form closes. Owns creating/destroying per-app capture pipelines.
        private Thread? _discoveryThread;
        private volatile bool _discoveryStopRequested;
        private bool _discoveryStarted;

        // AGC: a single shared thread, started/stopped every time the user
        // presses Start/Stop, that evaluates every tracked app each cycle
        // and performs a one-shot (re)leveling when one is due.
        private Thread? _agcThread;
        private volatile bool _agcStopRequested;
        private int _agcThreadPending;

        // Set/cleared by the global pause hotkey (Ctrl+Alt+L). While
        // true, RunAgcLoop still refreshes each app's displayed
        // volume/RMS but performs no calibration, recalibration, or
        // volume ramp.
        private volatile bool _levelingPaused;
        private bool _hotkeyRegistered;

        // Per-app tracked state, keyed by process name (case-insensitive).
        private readonly object _appStatesLock = new();
        private readonly Dictionary<string, AppState> _appStates =
            new(StringComparer.OrdinalIgnoreCase);

        // The single shared reference level, expressed in raw RMS units
        // (see ReferenceRmsScale for how the slider maps onto this).
        // Sentinel -1 means "not set". _referenceVersion increments every
        // time the reference actually changes, so tracked apps can tell
        // "I was leveled for an old reference" from "I'm leveled for the
        // current one" - see AppState.LeveledForReferenceVersion.
        private readonly object _referenceLock = new();
        private double _referenceLevel = -1;
        private int _referenceVersion = 0;

        // How often the discovery thread rescans for new/vanished/stalled
        // apps.
        private const int DiscoveryIntervalMilliseconds = 3000;

        // Real audio content rarely sustains RMS anywhere near full-scale
        // (1.0) even at maximum session volume - typical mastered music
        // tops out around 0.25-0.40 RMS at 100% volume. The 0-100% slider
        // is scaled onto this realistic ceiling instead of raw 0.0-1.0
        // RMS, so e.g. "60%" on the slider asks for a target that's
        // actually reachable by normal content, rather than requiring the
        // session volume to exceed 100% (impossible) for anything but
        // unusually hot masters. Tune this if your content runs
        // consistently louder or quieter than average.
        private const double ReferenceRmsScale = 0.35;

        // Fast EMA time constant for the display/instant RMS reading
        // (used for the "RMS" figure shown per app, and to gate when
        // audio is "real" vs. silence). Unrelated to leveling cadence -
        // see LongTermRmsTimeConstantSeconds for that. This is a
        // deliberate stability choice, not a precision limit - the raw
        // per-buffer RMS feeding it is already effectively real-time
        // (every WASAPI buffer, negotiated at the device's default
        // period, typically ~10ms in shared mode, feeds both the instant
        // RMS and the calibration accumulator directly, with no
        // downsampling).
        private const double SmoothingTimeConstantSeconds = 0.8;

        // How often the AGC thread wakes up to check whether any tracked
        // app needs a calibration or a drift-triggered recalibration.
        private const int AgcIntervalMilliseconds = 5000;

        // How much of the very start of a new calibration window to
        // exclude from the RMS average entirely. Many tracks open with a
        // quieter "attack" (a solo intro line, a fade-in, a soft pickup)
        // before settling into their real sustained level; folding that
        // attack into the average on equal footing with everything after
        // it makes the computed target volume come out too high once the
        // track actually settles. Samples captured during this slice are
        // still used to detect "is there real audio yet"
        // (LastAboveSilenceUtc, ContinuousAudioSinceUtc), just not folded
        // into CalibrationRmsSum / CalibrationSampleCount.
        private const double CalibrationWarmupSeconds = 6;

        // How long, AFTER CalibrationWarmupSeconds has elapsed, an app's
        // audio must keep producing samples before Leveler trusts the
        // calibration average enough to act on it. The total wait before
        // a fresh calibration can act is CalibrationWarmupSeconds +
        // CalibrationWindowSeconds - see continuousLongEnough in
        // RunAgcLoop.
        private const double CalibrationWindowSeconds = 6;

        // Time constant for the slow, separate long-term RMS average used
        // only to decide whether automatic drift-based recalibration is
        // warranted within a single, unbroken streak (e.g. a long film
        // with no real silence). Deliberately much slower than
        // SmoothingTimeConstantSeconds so normal within-track dynamics
        // don't look like drift - this is a slower, looser safety net
        // alongside the faster LevelShiftDeviationRatio /
        // LevelShiftConfirmSeconds check below, not the only mechanism
        // for catching a level change. Increased from 45s to 120s so it
        // only catches sustained, genuine drift rather than normal musical
        // phrasing within a track.
        private const double LongTermRmsTimeConstantSeconds = 120;

        // Minimum time between automatic drift-triggered recalibrations
        // for the same app within the same streak - keeps this
        // "occasional", not continuous. Does NOT gate streak-based or
        // reference-change-based recalibration, nor the level-shift
        // trigger below - those always happen on their own schedule once
        // due.
        private const double RecalibrationIntervalSeconds = 300;

        // How far the long-term average RMS has to drift from the
        // reference (as a ratio, either direction) before Leveler
        // decides it's worth an automatic drift-based recalibration -
        // i.e. before it actually changes the volume. Gated by
        // RecalibrationIntervalSeconds, so this is deliberately a slow,
        // occasional safety net - see LevelShiftDeviationRatio for the
        // faster mechanism aimed specifically at sudden level changes
        // (e.g. gapless track changes). Lowered from 1.5 to 1.4 so the
        // safety net catches smaller sustained deviations sooner.
        private const double RecalibrationDriftFactor = 1.4;

        // Purely cosmetic threshold for the status text of an
        // already-leveled app - how far the CURRENT instantaneous RMS can
        // sit from the reference before the status stops saying "On
        // target." and starts saying so honestly. Tighter than
        // RecalibrationDriftFactor on purpose: this never changes the
        // volume, it only changes what's displayed, so there's no harm in
        // being more sensitive here. Lowered from 1.3 to 1.15 so the
        // status is stricter about what counts as "on target".
        private const double DisplayAlignmentRatio = 1.15;

        // How far the fast smoothed RMS has to sit from the reference (as
        // a ratio, either direction) before a deviation is even
        // considered a *candidate* level shift - i.e. possibly a new
        // track/video rather than normal within-track dynamics. Evaluated
        // every AGC cycle - see LevelShiftConfirmSeconds for what actually
        // turns a candidate into a confirmed trigger. Lowered from 1.5 to
        // 1.3 so smaller sustained deviations are treated as level shifts
        // instead of being tolerated as normal variation.
        private const double LevelShiftDeviationRatio = 1.3;

        // How long a level-shift candidate must persist, continuously,
        // before Leveler treats it as a real content change (new
        // track/video, most importantly one with no silence gap before
        // it) rather than a passing loud/quiet passage within the same
        // track. Deliberately set above one AgcIntervalMilliseconds cycle
        // so a genuine confirmation requires agreement across at least
        // two separate AGC checks, not a single sample - a loud chorus
        // that fades back within a few seconds never accumulates enough
        // continuous time to confirm. Increased from 9s to 12s to require
        // longer sustained deviation before confirming.
        private const double LevelShiftConfirmSeconds = 9;

        // Total duration of the short ramp used whenever Leveler actually
        // changes a session's volume (fresh calibration or drift-based
        // recalibration), instead of writing the target volume in one
        // instant step - see RampSessionsVolume. Long enough that a large
        // before/after difference reads as "the sound got quieter/
        // louder" rather than an audible "stacco" cut. Increased from
        // 1.5s to 3.0s for gentler, less intrusive transitions.
        private const double VolumeRampDurationSeconds = 3.0;

        // Time between individual steps of the volume ramp. Small enough
        // (25 steps over VolumeRampDurationSeconds) that the change reads
        // as smooth rather than as discrete audible jumps, large enough
        // not to spam SimpleAudioVolume.Volume writes for no audible
        // benefit.
        private const int VolumeRampStepMilliseconds = 40;

        private const double AgcSilenceThreshold = 0.004;

        // Volume any tracked app's sessions are set to immediately on
        // detection (unless a persisted volume exists for it - see
        // CreateAndStartAppState), before any calibration has run. Also
        // used as ComputeLeveledVolume's fallback base if a session's
        // volume is later found at/near zero, and as the target for the
        // "Reset volumes" button (ResetVolumesButtonClicked). This
        // initial seed is still an instant assignment (see
        // CreateAndStartAppState) - there's no "before" audio playing yet
        // at that point for a ramp to meaningfully protect against.
        private const float InitialSeedVolume = 0.5f;

        private const double NoDataWarningThresholdSeconds = 5;
        private const int MaxActivationAttempts = 4;
        private const int UiRefreshIntervalMilliseconds = 500;

        // If a tracked app's session reports active audio but its capture
        // hasn't received a single packet in this long, its pipeline is
        // rebuilt. This is the only condition that automatically tears
        // down and recreates a pipeline - see RunDiscoveryLoop.
        private const double CaptureStallRestartThresholdSeconds = 10;

        // Purely informational - past this many seconds of continuous
        // measured silence, the status message adds a hint suggesting the
        // user manually restart the app if it seems genuinely stuck.
        // Never triggers an automatic rebuild.
        private const double SilenceStallWarningThresholdSeconds = 120;

        // Shared accent color for buttons, the tray icon, and (on
        // Windows 11+) the title bar caption. A single constant so the
        // whole UI's accent stays consistent if it's ever changed.
        private static readonly System.Drawing.Color AccentColor = System.Drawing.Color.FromArgb(0xFB, 0x6D, 0x06);

        // Dark theme background colors. FormBackgroundColor is the
        // general window background; FieldBackgroundColor is used for
        // controls that show live data (lists, dropdown) - a shade
        // darker so they read as distinct "readout" areas against the
        // form.
        private static readonly System.Drawing.Color FormBackgroundColor = System.Drawing.Color.FromArgb(0x24, 0x24, 0x24);
        private static readonly System.Drawing.Color FieldBackgroundColor = System.Drawing.Color.FromArgb(0x1F, 0x1F, 0x1F);
        private static readonly System.Drawing.Color LightTextColor = System.Drawing.Color.White;

        // URL opened by the tray context menu's website entry - see
        // OpenWebsite.
        private const string WebsiteUrl = "https://github.com/roughnecks/volume-leveler";

        // Global pause hotkey (Ctrl+Alt+L, fixed for now).
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint ModControl = 0x0002;
        private const uint ModAlt = 0x0001;
        private const uint VkL = 0x4C;
        private const int PauseHotkeyId = 0xB00C;
        private const int WmHotkey = 0x0312;

        // DIAGNOSTIC / DEFENSIVE FIX: Explorer broadcasts a registered
        // "TaskbarCreated" message whenever it (re)creates the taskbar and
        // notification area - normally only when explorer.exe restarts,
        // but the same underlying Shell_NotifyIcon call our NotifyIcon
        // makes during SetupTrayIcon can also silently fail with no
        // managed exception if that area isn't fully initialized yet this
        // early at logon (a known real quirk: the Win32 call can report
        // success while the icon never actually appears). Listening for
        // this message and re-adding the icon if that happens is the
        // standard, documented mitigation for tray icons that must
        // survive - or recover from - this kind of early-boot timing gap.
        private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        // Windows 11 (build 22000+) title bar caption color.
        // DwmSetWindowAttribute simply returns a non-zero HRESULT on
        // older Windows versions rather than throwing, but the call is
        // still wrapped in try/catch defensively - see SetTitleBarAccent.
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
        private const int DwmwaCaptionColor = 35;

        private sealed class AppState
        {
            public readonly string ProcessName;
            public readonly object Lock = new();

            public Thread? CaptureThread;
            public volatile bool CaptureStopRequested;

            public double InstantRms;
            public double SmoothedRms;
            public float LastKnownVolume;
            public string CaptureState = "Starting...";
            public string AgcState = "Waiting for reference level.";
            public DateTime LastPacketUtc = DateTime.UtcNow;

            // Last time this app's smoothed RMS was at or above
            // AgcSilenceThreshold. Diagnostic-only.
            public DateTime LastAboveSilenceUtc = DateTime.UtcNow;

            // Set the moment smoothed RMS first crosses above the silence
            // threshold, and cleared back to null the moment it drops
            // below it again - marks the start of the current unbroken
            // audio streak (used for silence/"waiting for audio"
            // purposes). This is NOT the same thing as "when did the
            // current calibration window start" - see
            // CalibrationWindowStartUtc - because gapless playback never
            // clears this field even when the underlying track changes.
            public DateTime? ContinuousAudioSinceUtc;

            // When the current calibration averaging window began. Set
            // by RunCaptureLoop whenever a real streak starts (silence
            // gap followed by audio again); also reset independently by
            // RunAgcLoop when it confirms a level shift, or the moment a
            // reference change is first noticed, so a stale window from
            // before the change is never used to compute the new volume.
            // The first CalibrationWarmupSeconds after this timestamp are
            // excluded from CalibrationRmsSum / CalibrationSampleCount.
            public DateTime? CalibrationWindowStartUtc;

            // Running sum/count of raw RMS samples collected since
            // CalibrationWindowStartUtc was last set, EXCLUDING the
            // leading CalibrationWarmupSeconds - the calibration average
            // for the current window. Reset whenever a new window begins
            // (see CalibrationWindowStartUtc).
            public double CalibrationRmsSum;
            public int CalibrationSampleCount;

            // Candidate start time of an in-progress level-shift
            // deviation (smoothed RMS sitting past LevelShiftDeviationRatio
            // away from the reference). Null whenever the app isn't
            // currently deviating that far. Used only to require the
            // deviation to persist for LevelShiftConfirmSeconds before
            // acting on it - never touches the volume by itself.
            public DateTime? LevelShiftSinceUtc;

            // Which CalibrationWindowStartUtc value was used for the most
            // recent (re)calibration - lets the AGC loop tell "already
            // handled this window" from "this is a window I haven't
            // reacted to yet".
            public DateTime? LastEvaluatedCalibrationWindowStartUtc;

            // Which _referenceVersion the calibration window has already
            // been restarted for, following a reference change. Distinct
            // from LeveledForReferenceVersion (which only updates once
            // the app actually finishes being releveled) - this updates
            // the moment the window is restarted, so that reset only
            // happens once per reference change instead of on every AGC
            // cycle spent waiting for the fresh window to fill.
            public int CalibrationWindowRestartedForReferenceVersion;

            // Which _referenceVersion this app was last leveled for. 0
            // means "never leveled". Comparing this to the current
            // _referenceVersion is how the AGC loop knows whether an
            // app is up to date or due for a (re)leveling.
            public int LeveledForReferenceVersion;

            // When the last (initial or recalibration) leveling action
            // happened - gates the RecalibrationIntervalSeconds cooldown
            // for drift-based recalibration only.
            public DateTime LastLevelUtc = DateTime.MinValue;

            // Slow-moving average RMS, used only to detect drift for
            // automatic recalibration within a single streak. 0 means
            // "not yet established".
            public double LongTermRms;

            public AppState(string processName) => ProcessName = processName;
        }

        // Small custom dialog used by OnFormClosing instead of a plain
        // MessageBox, so the exit confirmation can come with a "Don't ask
        // me again" checkbox that switches it off entirely for future
        // closes. Uses the same dark theme / accent styling as the rest
        // of the app for visual consistency.
        private sealed class ExitConfirmationDialog : Form
        {
            public bool DontShowAgain { get; private set; }

            public ExitConfirmationDialog()
            {
                Text = "Confirm exit";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                AutoScaleMode = AutoScaleMode.Font;
                BackColor = FormBackgroundColor;
                ClientSize = new System.Drawing.Size(420, 196);

                var messageLabel = new Label
                {
                    AutoSize = false,
                    Location = new System.Drawing.Point(16, 16),
                    Size = new System.Drawing.Size(388, 36),
                    ForeColor = LightTextColor,
                    Text = "Are you sure you want to exit Volume Leveler?"
                };
                Controls.Add(messageLabel);

                var hintLabel = new Label
                {
                    AutoSize = false,
                    Location = new System.Drawing.Point(16, 56),
                    Size = new System.Drawing.Size(388, 56),
                    ForeColor = LightTextColor,
                    Text = "Tip: if you only want to disable leveling temporarily, the app can stay open - press Ctrl+Alt+L instead."
                };
                Controls.Add(hintLabel);

                var dontAskAgainCheckBox = new CheckBox
                {
                    Location = new System.Drawing.Point(16, 114),
                    Size = new System.Drawing.Size(388, 24),
                    ForeColor = LightTextColor,
                    Text = "Don't ask me again"
                };
                Controls.Add(dontAskAgainCheckBox);

                var noButton = new Button
                {
                    Text = "No",
                    DialogResult = DialogResult.No,
                    Size = new System.Drawing.Size(90, 30),
                    Location = new System.Drawing.Point(ClientSize.Width - 90 - 16, ClientSize.Height - 30 - 16)
                };
                ApplyAccentStyle(noButton);

                var yesButton = new Button
                {
                    Text = "Yes",
                    DialogResult = DialogResult.Yes,
                    Size = new System.Drawing.Size(90, 30),
                    Location = new System.Drawing.Point(noButton.Left - 90 - 8, noButton.Top)
                };
                ApplyAccentStyle(yesButton);

                Controls.Add(yesButton);
                Controls.Add(noButton);

                // Defaults to "No" on Enter/close - the safer choice if
                // the user just hits Enter without reading.
                AcceptButton = noButton;
                CancelButton = noButton;

                FormClosing += (_, _) => DontShowAgain = dontAskAgainCheckBox.Checked;
            }
        }

        public MainForm()
        {
            InitializeComponent();
            BuildUi();
            Icon = IconFactory.CreateAppIcon(); // Window/taskbar/Alt-Tab icon
            SetupTrayIcon();
            LoadPersistedSettings();
        }

        private void BuildUi()
        {
            Text = $"Volume Leveler v{AppVersion} - Calibrated Leveling";
            Width = 700;
            Height = 784;
            // Keeps the three list boxes usable - below this size the
            // proportional layout of the list area would squash them.
            MinimumSize = new System.Drawing.Size(520, 640);
            BackColor = FormBackgroundColor;

            // --- Header: status label + help button ---
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new System.Drawing.Font(Font.FontFamily, 10.5f),
                Text = "Press Start to begin tracking and leveling active apps.",
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                ForeColor = LightTextColor
            };
            _helpButton = new Button { Dock = DockStyle.Right, Width = 36, Text = "?" };
            _helpButton.Click += (_, _) => ShowHelp();
            ApplyAccentStyle(_helpButton);

            _headerPanel = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = FormBackgroundColor };
            _headerPanel.Controls.Add(_statusLabel);
            _headerPanel.Controls.Add(_helpButton);

            // --- Start/Stop/Reset: small, centered buttons under the
            // status row, instead of full-width rows. ---
            _startButton = new Button { Width = 90, Height = 32, Text = "Start" };
            _stopButton = new Button { Width = 90, Height = 32, Text = "Stop", Enabled = false };
            _resetVolumesButton = new Button { Width = 130, Height = 32, Text = "Reset volumes" };

            _startButton.Click += (_, _) => StartButtonClicked();
            _stopButton.Click += (_, _) => StopButtonClicked();
            _resetVolumesButton.Click += (_, _) => ResetVolumesButtonClicked();

            ApplyAccentStyle(_startButton);
            ApplyAccentStyle(_stopButton);
            ApplyAccentStyle(_resetVolumesButton);

            _startStopPanel = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = FormBackgroundColor };
            _startStopPanel.Controls.Add(_startButton);
            _startStopPanel.Controls.Add(_stopButton);
            _startStopPanel.Controls.Add(_resetVolumesButton);
            _startStopPanel.Resize += (_, _) => CenterStartStopButtons();

            // --- Reference: a slider, adjustable at any time ---
            _referenceLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = "Reference: not set",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = LightTextColor
            };
            _referenceTrackBar = new TrackBar
            {
                Dock = DockStyle.Top,
                Minimum = 0,
                Maximum = 100,
                TickFrequency = 5,
                Value = 50 // set before wiring the event, so this doesn't count as "the user set it"
            };
            // The native TrackBar keeps its default light system
            // rendering - Windows doesn't expose a supported way to
            // recolor it without replacing it with a custom-drawn
            // control, which is out of scope for now.
            _referenceTrackBar.Scroll += (_, _) => ReferenceTrackBarChanged();

            // A fixed-height TableLayoutPanel instead of a plain Panel,
            // so the dropdown and the seed button share the same row
            // height and align vertically.
            var seedPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 32,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = FormBackgroundColor
            };
            seedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            seedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _seedFromAppComboBox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = FieldBackgroundColor,
                ForeColor = LightTextColor
            };
            _seedReferenceButton = new Button
            {
                Dock = DockStyle.Fill,
                Text = "Seed reference from selected app"
            };
            _seedReferenceButton.Click += (_, _) => SeedReferenceFromApp();
            ApplyAccentStyle(_seedReferenceButton);
            seedPanel.Controls.Add(_seedFromAppComboBox, 0, 0);
            seedPanel.Controls.Add(_seedReferenceButton, 1, 0);

            // --- Presets row - dropdown of 3 slots, editable label,
            // Save/Load. Selecting a slot only updates the label
            // textbox; it never changes the live reference by itself. ---
            var presetPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 32,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = FormBackgroundColor
            };
            presetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            presetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            presetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            presetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));

            _presetComboBox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = FieldBackgroundColor,
                ForeColor = LightTextColor
            };
            _presetComboBox.SelectedIndexChanged += (_, _) => PresetComboBoxSelectionChanged();

            _presetLabelTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = FieldBackgroundColor,
                ForeColor = LightTextColor,
                BorderStyle = BorderStyle.FixedSingle
            };

            _savePresetButton = new Button { Dock = DockStyle.Fill, Text = "Save" };
            _savePresetButton.Click += (_, _) => SavePresetButtonClicked();
            ApplyAccentStyle(_savePresetButton);

            _loadPresetButton = new Button { Dock = DockStyle.Fill, Text = "Load" };
            _loadPresetButton.Click += (_, _) => LoadPresetButtonClicked();
            ApplyAccentStyle(_loadPresetButton);

            presetPanel.Controls.Add(_presetComboBox, 0, 0);
            presetPanel.Controls.Add(_presetLabelTextBox, 1, 0);
            presetPanel.Controls.Add(_savePresetButton, 2, 0);
            presetPanel.Controls.Add(_loadPresetButton, 3, 0);

            _detectedAppsLabel = new Label
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Text = "Detected apps (right-click to blacklist):",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = LightTextColor
            };
            _refreshAppsButton = new Button { Dock = DockStyle.Fill, Margin = Padding.Empty, Text = "Refresh detected apps" };
            ApplyAccentStyle(_refreshAppsButton);
            _detectedAppsListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false, // lets the row height follow the window exactly
                Margin = Padding.Empty,
                BackColor = FieldBackgroundColor,
                ForeColor = LightTextColor,
                BorderStyle = BorderStyle.FixedSingle
            };
            _refreshAppsButton.Click += (_, _) => RefreshDetectedApps();

            // Right-click on a detected app to blacklist it.
            _detectedAppsContextMenu = new ContextMenuStrip();
            _detectedAppsListBox.MouseUp += DetectedAppsListBox_MouseUp;

            // --- Blacklist review section ---
            var blacklistLabel = new Label
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Text = "Blacklisted apps (never tracked):",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = LightTextColor
            };
            _blacklistListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Margin = Padding.Empty,
                BackColor = FieldBackgroundColor,
                ForeColor = LightTextColor,
                BorderStyle = BorderStyle.FixedSingle
            };
            _removeFromBlacklistButton = new Button { Dock = DockStyle.Fill, Margin = Padding.Empty, Text = "Remove selected from blacklist" };
            _removeFromBlacklistButton.Click += (_, _) => RemoveFromBlacklistButtonClicked();
            ApplyAccentStyle(_removeFromBlacklistButton);

            _activeAppsLabel = new Label
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Text = "Currently leveled apps (right-click to blacklist):",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = LightTextColor
            };
            _activeAppsListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Margin = Padding.Empty,
                BackColor = FieldBackgroundColor,
                ForeColor = LightTextColor,
                BorderStyle = BorderStyle.FixedSingle
            };

            // Right-click on a currently-leveled app to blacklist it too
            // - not just from "Detected apps" (an app whose session is
            // momentarily reported "not found" drops out of that list
            // while still showing up here).
            _activeAppsContextMenu = new ContextMenuStrip();
            _activeAppsListBox.MouseUp += ActiveAppsListBox_MouseUp;

            // The three list sections live in one table that fills all
            // the space left under the fixed-height rows above. Labels
            // and buttons keep a fixed row height, while the three list
            // boxes share the remaining height by percentage, so they
            // all grow/shrink together when the window is resized.
            var listArea = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = FormBackgroundColor
            };
            listArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // detected label
            listArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // refresh button
            listArea.RowStyles.Add(new RowStyle(SizeType.Percent, 25));    // detected list
            listArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // blacklist label
            listArea.RowStyles.Add(new RowStyle(SizeType.Percent, 30));    // blacklist list
            listArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));   // remove button
            listArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // active label
            listArea.RowStyles.Add(new RowStyle(SizeType.Percent, 45));    // active list

            listArea.Controls.Add(_detectedAppsLabel, 0, 0);
            listArea.Controls.Add(_refreshAppsButton, 0, 1);
            listArea.Controls.Add(_detectedAppsListBox, 0, 2);
            listArea.Controls.Add(blacklistLabel, 0, 3);
            listArea.Controls.Add(_blacklistListBox, 0, 4);
            listArea.Controls.Add(_removeFromBlacklistButton, 0, 5);
            listArea.Controls.Add(_activeAppsLabel, 0, 6);
            listArea.Controls.Add(_activeAppsListBox, 0, 7);

            // Controls.Add stacks in reverse of visual top-to-bottom
            // order for the Dock = Top controls - the first one added
            // ends up lowest. The Dock = Fill table is added first so
            // it takes whatever space the Top-docked controls leave.
            Controls.Add(listArea);

            Controls.Add(presetPanel);
            Controls.Add(seedPanel);
            Controls.Add(_referenceTrackBar);
            Controls.Add(_referenceLabel);

            Controls.Add(_startStopPanel);
            Controls.Add(_headerPanel);

            _uiRefreshTimer = new System.Windows.Forms.Timer { Interval = UiRefreshIntervalMilliseconds };
            _uiRefreshTimer.Tick += (_, _) => RefreshActiveAppsDisplay();
            _uiRefreshTimer.Start();

            CenterStartStopButtons();
        }

        // WinForms has no built-in "center" docking, so the Start/Stop/
        // Reset buttons are positioned manually within their panel and
        // re-centered whenever the panel resizes (including the first
        // layout pass).
        private void CenterStartStopButtons()
        {
            const int gap = 12;
            int totalWidth = _startButton.Width + _stopButton.Width + _resetVolumesButton.Width + gap * 2;
            int x = Math.Max(0, (_startStopPanel.ClientSize.Width - totalWidth) / 2);
            int y = Math.Max(0, (_startStopPanel.ClientSize.Height - _startButton.Height) / 2);

            _startButton.Location = new System.Drawing.Point(x, y);
            _stopButton.Location = new System.Drawing.Point(x + _startButton.Width + gap, y);
            _resetVolumesButton.Location = new System.Drawing.Point(x + _startButton.Width + _stopButton.Width + gap * 2, y);
        }

        // Quick-reference setup instructions, shown on demand so they
        // don't need to occupy permanent screen space.
        private void ShowHelp()
        {
            MessageBox.Show(
                this,
                "Before you start:\n\n" +
                "1. Set your physical speaker/headphone volume once, at a comfortable level, and leave it there - Volume Leveler adjusts each app's Windows volume, not your hardware volume.\n\n" +
                "2. Make sure Windows' master volume and every app's volume are at or near 100% before pressing Start - Volume Leveler needs headroom to raise a quiet app; it can only lower a loud one.\n\n" +
                "3. Press Start, then either drag the reference slider to a target level, or play something in an app you consider 'right', select it in the dropdown, and press 'Seed reference from selected app'.\n\n" +
                "4. Every tracked app then gets its own one-time calibration and settles at the reference level. Use Ctrl+Alt+L to pause leveling temporarily without closing the app.\n\n" +
                "5. Save up to 3 reference presets (e.g. 'Night', 'Party', 'Studio') to switch quickly between listening situations - the Load button applies a preset's saved reference immediately.\n\n" +
                "6. To stop Volume Leveler from ever tracking a specific app (e.g. a notification-only app like MicMute or ShareX), right-click its entry in either 'Detected apps' or 'Currently leveled apps' and pick 'Add to blacklist'. It's removed from tracking immediately and won't be picked up again - review or undo it anytime from the 'Blacklisted apps' list further down.\n\n" +
                "7. Volume Leveler runs mainly from the system tray - right-click the tray icon for Show, this help, the project website, Exit, and checkboxes to start leveling and/or Windows itself automatically.",
                "Volume Leveler - How to use",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // Applies the shared orange accent to a button. Flat style is
        // used because BackColor has no visible effect on the default
        // (system-themed) button renderer.
        private static void ApplyAccentStyle(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = AccentColor;
            button.ForeColor = System.Drawing.Color.White;
        }

        // Opens the project website in the user's default browser.
        // UseShellExecute = true is required here - without it, .NET
        // tries to launch the URL as if it were an executable and
        // throws. Best-effort: if the user has no default browser
        // configured (or anything else goes wrong), there's nothing more
        // useful to do than silently ignore it.
        private static void OpenWebsite()
        {
            try
            {
                Process.Start(new ProcessStartInfo(WebsiteUrl) { UseShellExecute = true });
            }
            catch
            {
                // Ignored by design - see method comment.
            }
        }

        // System tray icon, context menu, and minimize-to-tray behavior.
        // The two checkboxes controlling auto-start behavior live here
        // rather than on the main window, since the app is meant to be
        // used mostly from the tray.
        private void SetupTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();

            var versionMenuItem = new ToolStripMenuItem($"Volume Leveler v{AppVersion}") { Enabled = false };
            _trayMenu.Items.Add(versionMenuItem);
            _trayMenu.Items.Add(new ToolStripSeparator());

            _trayMenu.Items.Add("Show", null, (_, _) => RestoreFromTray());
            _trayMenu.Items.Add(WebsiteUrl.Replace("https://", ""), null, (_, _) => OpenWebsite());
            _trayMenu.Items.Add("How to use", null, (_, _) => ShowHelp());
            _trayMenu.Items.Add(new ToolStripSeparator());

            _autoStartLevelingMenuItem = new ToolStripMenuItem("Start leveling automatically on launch")
            {
                CheckOnClick = true
            };
            _autoStartLevelingMenuItem.CheckedChanged += (_, _) => SaveSettingsNow();
            _trayMenu.Items.Add(_autoStartLevelingMenuItem);

            // Seeded false here; the real state is loaded asynchronously
            // every time the tray menu is opened (see the
            // ContextMenuStrip.Opening subscription below), never here at
            // startup. AutoStartHelper's Task Scheduler calls are quick,
            // but keeping this pattern costs nothing and means startup
            // never depends on it either way.
            _autoStartWithWindowsMenuItem = new ToolStripMenuItem("Start Volume Leveler with Windows")
            {
                CheckOnClick = true,
                Checked = false
            };
            _autoStartWithWindowsMenuItem.CheckedChanged += AutoStartWithWindowsMenuItem_CheckedChanged;
            _trayMenu.Items.Add(_autoStartWithWindowsMenuItem);

            _trayMenu.Items.Add(new ToolStripSeparator());
            // OnFormClosing shows a confirmation dialog first (unless the
            // user has dismissed it for good) and only hides the tray
            // icon once the user actually confirms exiting.
            _trayMenu.Items.Add("Exit", null, (_, _) => Close());

            _trayIcon = new NotifyIcon
            {
                Icon = IconFactory.CreateAppIcon(),
                Text = $"Volume Leveler v{AppVersion}",
                Visible = true,
                ContextMenuStrip = _trayMenu
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

            // Refresh the autostart checkbox every time the tray menu is
            // opened, instead of once at startup, so it always reflects
            // the actual Scheduled Task state (see AutoStartHelper) -
            // including changes made directly in Task Scheduler.
            _trayMenu.Opening += async (_, _) => await RefreshAutoStartCheckboxAsync();
        }

        // Named handler (not a lambda) so it can be detached during the
        // programmatic state refresh below - a plain lambda subscription
        // could not do that.
        private async void AutoStartWithWindowsMenuItem_CheckedChanged(object? sender, EventArgs e)
        {
            await AutoStartHelper.SetEnabledAsync(_autoStartWithWindowsMenuItem.Checked);
        }

        // Loads the autostart checkbox state asynchronously and applies it
        // without re-triggering CheckedChanged: setting Checked
        // programmatically would otherwise fire the same event as a user
        // click and needlessly re-register the Scheduled Task (see
        // AutoStartHelper.SetEnabledAsync) just from opening the menu to
        // look at the current state.
        private async Task RefreshAutoStartCheckboxAsync()
        {
            bool isEnabled = await AutoStartHelper.IsEnabledAsync();

            _autoStartWithWindowsMenuItem.CheckedChanged -= AutoStartWithWindowsMenuItem_CheckedChanged;
            _autoStartWithWindowsMenuItem.Checked = isEnabled;
            _autoStartWithWindowsMenuItem.CheckedChanged += AutoStartWithWindowsMenuItem_CheckedChanged;
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // The app starts minimized to the tray instead of showing its
        // window on launch. WinForms calls this (via Application.Run ->
        // Show()) exactly once during normal startup before any code
        // here gets a chance to Hide() the form - intercepting it here,
        // before it ever becomes visible, avoids the on-screen flash a
        // Show() + immediate Hide() approach would cause. The native
        // window handle is still forced to exist (via CreateHandle) so
        // the tray icon, the global pause hotkey, and the title bar
        // accent - all of which need a real handle - are set up exactly
        // as if the window had been shown normally. Any later, explicit
        // Show() call (e.g. RestoreFromTray) passes value = true through
        // unaffected, since _startingUp is cleared the first time this
        // runs.
        protected override void SetVisibleCore(bool value)
        {
            if (_startingUp)
            {
                _startingUp = false;
                if (!IsHandleCreated)
                {
                    CreateHandle();
                }
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
                Hide();
        }

        // Registers the global pause hotkey and, on supported Windows
        // versions, the accent title bar color. Both need a realized
        // window handle, so this runs from OnHandleCreated rather than
        // the constructor.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            _hotkeyRegistered = RegisterHotKey(Handle, PauseHotkeyId, ModControl | ModAlt, VkL);
            // Not fatal if this fails (e.g. another app already owns
            // Ctrl+Alt+L) - leveling simply can't be paused via hotkey
            // until that conflict is resolved; everything else still
            // works normally.

            SetTitleBarAccent();
        }

        private void SetTitleBarAccent()
        {
            try
            {
                int colorRef = System.Drawing.ColorTranslator.ToWin32(AccentColor); // 0x00BBGGRR
                DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref colorRef, sizeof(int));
            }
            catch
            {
                // Windows versions before build 22000 don't support this
                // attribute - failing here is harmless, the title bar
                // just keeps the default system color.
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == PauseHotkeyId)
                ToggleLevelingPaused();

            if (m.Msg == WmTaskbarCreated)
            {
                try
                {
                    // Re-asserting Visible (rather than only doing this
                    // once at startup) is what actually recovers from the
                    // silent-failure case: if Shell_NotifyIcon didn't take
                    // the first time, setting Visible=false then true
                    // forces .NET to call it again now that the
                    // notification area genuinely exists.
                    _trayIcon.Visible = false;
                    _trayIcon.Visible = true;
                }
                catch
                {
                    // Best-effort: if this fails there's nothing more we can do here.
                }
            }

            base.WndProc(ref m);
        }

        // Flips the pause flag read by RunAgcLoop. Leaves every app's
        // calibration bookkeeping untouched, so un-pausing resumes
        // exactly where leveling left off.
        private void ToggleLevelingPaused()
        {
            _levelingPaused = !_levelingPaused;
            string state = _levelingPaused ? "paused" : "resumed";
            _statusLabel.Text = $"Leveling {state} (Ctrl+Alt+L).";
            _trayIcon.Text = _levelingPaused ? $"Volume Leveler v{AppVersion} - Paused" : $"Volume Leveler v{AppVersion}";
        }

        // Loads the persisted reference level, per-app volumes,
        // blacklist, presets, window geometry, and the tray checkboxes
        // on startup, if any were saved. Settings.Load() itself never
        // throws - see its class comment - so no try/catch is needed
        // here.
        private void LoadPersistedSettings()
        {
            var settings = Settings.Load();
            _referenceTrackBar.Value = Math.Clamp(settings.ReferencePercent, _referenceTrackBar.Minimum, _referenceTrackBar.Maximum);
            ReferenceTrackBarChanged();

            _persistedVolumes.Clear();
            foreach (var pair in settings.LastKnownVolumes)
                _persistedVolumes[pair.Key] = pair.Value;

            _userBlacklist.Clear();
            foreach (var name in settings.BlacklistedProcessNames)
                _userBlacklist.Add(name);
            RefreshBlacklistListBox();

            for (int i = 0; i < 3 && i < settings.Presets.Count; i++)
            {
                _presets[i].Label = settings.Presets[i].Label;
                _presets[i].ReferencePercent = settings.Presets[i].ReferencePercent;
            }
            RefreshPresetComboBoxLabels();
            if (_presetComboBox.Items.Count > 0)
                _presetComboBox.SelectedIndex = 0;

            // Restore window position/size, discarding the saved
            // position if it no longer intersects any connected monitor
            // (e.g. a since-disconnected second monitor) so the window
            // can never end up unreachable off-screen.
            bool hasValidSize = settings.WindowWidth > 0 && settings.WindowHeight > 0;
            if (hasValidSize)
            {
                Width = settings.WindowWidth;
                Height = settings.WindowHeight;
            }

            bool hasValidLocation = settings.WindowX != int.MinValue && settings.WindowY != int.MinValue;
            if (hasValidLocation)
            {
                var candidateBounds = new System.Drawing.Rectangle(settings.WindowX, settings.WindowY, Width, Height);
                if (SystemInformation.VirtualScreen.IntersectsWith(candidateBounds))
                {
                    StartPosition = FormStartPosition.Manual;
                    Location = new System.Drawing.Point(settings.WindowX, settings.WindowY);
                }
                // else: leave StartPosition at its default so Windows
                // picks a sensible on-screen position.
            }

            _autoStartLevelingMenuItem.Checked = settings.AutoStartLevelingOnLaunch;
            _showExitConfirmation = settings.ShowExitConfirmation;

            // If enabled, start tracking/leveling right away - works
            // fine even though the window itself starts hidden (see
            // SetVisibleCore), since discovery/AGC are background
            // threads with no dependency on the window being visible.
            if (settings.AutoStartLevelingOnLaunch)
                StartButtonClicked();
        }

        // A process name is excluded from discovery/tracking/display if
        // it's in the hardcoded set (currently Voicemeeter's known
        // executable names) or in the user's own blacklist.
        private bool IsProcessExcluded(string processName) =>
            HardcodedExcludedProcessNames.Contains(processName) || _userBlacklist.Contains(processName);

        private void RefreshBlacklistListBox()
        {
            _blacklistListBox.Items.Clear();
            foreach (var name in _userBlacklist.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                _blacklistListBox.Items.Add(name);
        }

        private void RefreshPresetComboBoxLabels()
        {
            int selected = _presetComboBox.SelectedIndex;
            _presetComboBox.Items.Clear();
            foreach (var preset in _presets)
                _presetComboBox.Items.Add(preset.Label);
            if (selected >= 0 && selected < _presetComboBox.Items.Count)
                _presetComboBox.SelectedIndex = selected;
        }

        // Selecting a different preset slot only updates the editable
        // label shown - it deliberately does NOT load that preset's
        // reference, so browsing slots can never accidentally change
        // live leveling. Use the "Load" button for that.
        private void PresetComboBoxSelectionChanged()
        {
            int index = _presetComboBox.SelectedIndex;
            if (index < 0 || index >= _presets.Length) return;
            _presetLabelTextBox.Text = _presets[index].Label;
        }

        private void SavePresetButtonClicked()
        {
            int index = _presetComboBox.SelectedIndex;
            if (index < 0 || index >= _presets.Length)
            {
                _statusLabel.Text = "Pick a preset slot first.";
                return;
            }

            string label = string.IsNullOrWhiteSpace(_presetLabelTextBox.Text)
                ? $"Preset {index + 1}"
                : _presetLabelTextBox.Text.Trim();

            _presets[index].Label = label;
            _presets[index].ReferencePercent = _referenceTrackBar.Value;

            RefreshPresetComboBoxLabels();
            SaveSettingsNow();

            _statusLabel.Text = $"Saved current reference ({_referenceTrackBar.Value}%) as '{label}'.";
        }

        private void LoadPresetButtonClicked()
        {
            int index = _presetComboBox.SelectedIndex;
            if (index < 0 || index >= _presets.Length)
            {
                _statusLabel.Text = "Pick a preset slot first.";
                return;
            }

            var preset = _presets[index];
            _referenceTrackBar.Value = Math.Clamp(preset.ReferencePercent, _referenceTrackBar.Minimum, _referenceTrackBar.Maximum);
            ReferenceTrackBarChanged();

            _statusLabel.Text = $"Loaded preset '{preset.Label}' ({preset.ReferencePercent}%).";
        }

        // Right-click on a "Detected apps" entry to blacklist it. Uses
        // _lastDetectedGroups (populated alongside the list box in
        // RefreshDetectedApps) to map the clicked row back to a real
        // process name, rather than parsing the displayed text.
        private void DetectedAppsListBox_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            int index = _detectedAppsListBox.IndexFromPoint(e.Location);
            if (index < 0 || index >= _lastDetectedGroups.Count) return;

            _detectedAppsListBox.SelectedIndex = index;
            string processName = _lastDetectedGroups[index].ProcessName;

            _detectedAppsContextMenu.Items.Clear();
            _detectedAppsContextMenu.Items.Add($"Add '{processName}' to blacklist", null, (_, _) => AddToBlacklist(processName));
            _detectedAppsContextMenu.Show(_detectedAppsListBox, e.Location);
        }

        // Right-click on a "Currently leveled apps" entry to blacklist
        // it - covers apps whose session is momentarily reported "not
        // found" and so have already dropped out of "Detected apps".
        // Uses _lastActiveAppsOrder (populated alongside the list box in
        // RefreshActiveAppsDisplay) to map the clicked row back to a
        // real process name, the same way DetectedAppsListBox_MouseUp
        // uses _lastDetectedGroups.
        private void ActiveAppsListBox_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            int index = _activeAppsListBox.IndexFromPoint(e.Location);
            if (index < 0 || index >= _lastActiveAppsOrder.Count) return;

            _activeAppsListBox.SelectedIndex = index;
            string processName = _lastActiveAppsOrder[index];

            _activeAppsContextMenu.Items.Clear();
            _activeAppsContextMenu.Items.Add($"Add '{processName}' to blacklist", null, (_, _) => AddToBlacklist(processName));
            _activeAppsContextMenu.Show(_activeAppsListBox, e.Location);
        }

        // Adds a process name to the blacklist and, if it's currently
        // being tracked, stops and removes it immediately - this is an
        // explicit user action, so it doesn't wait for the discovery
        // loop's own (deliberately narrower) removal criteria.
        private void AddToBlacklist(string processName)
        {
            if (!_userBlacklist.Add(processName))
            {
                _statusLabel.Text = $"{processName} is already blacklisted.";
                return;
            }

            RefreshBlacklistListBox();
            SaveSettingsNow();

            lock (_appStatesLock)
            {
                if (_appStates.TryGetValue(processName, out var state))
                {
                    state.CaptureStopRequested = true;
                    _appStates.Remove(processName);
                }
            }

            _statusLabel.Text = $"{processName} added to blacklist - it will no longer be tracked.";
        }

        private void RemoveFromBlacklistButtonClicked()
        {
            if (_blacklistListBox.SelectedItem is not string name)
            {
                _statusLabel.Text = "Pick a blacklisted app to remove first.";
                return;
            }

            _userBlacklist.Remove(name);
            RefreshBlacklistListBox();
            SaveSettingsNow();

            _statusLabel.Text = $"{name} removed from blacklist.";
        }

        // Snapshots everything that needs to survive a restart into a
        // Settings instance - shared by the immediate saves after a
        // blacklist/preset/checkbox change and by OnFormClosing, so
        // there's one place that decides what's persisted.
        private Settings BuildCurrentSettings()
        {
            var settings = new Settings { ReferencePercent = _referenceTrackBar.Value };

            lock (_appStatesLock)
            {
                foreach (var pair in _appStates)
                {
                    float volume;
                    lock (pair.Value.Lock) { volume = pair.Value.LastKnownVolume; }
                    settings.LastKnownVolumes[pair.Key] = volume;
                }
            }

            settings.BlacklistedProcessNames = _userBlacklist.ToList();
            settings.Presets = _presets.Select(p => new Preset { Label = p.Label, ReferencePercent = p.ReferencePercent }).ToList();

            settings.AutoStartLevelingOnLaunch = _autoStartLevelingMenuItem.Checked;
            settings.ShowExitConfirmation = _showExitConfirmation;

            // Capture the window's "normal" bounds even if it's
            // currently minimized (hidden to the tray) or maximized -
            // RestoreBounds is exactly the size/location the form had
            // before entering either of those states, which is what's
            // actually useful to restore on the next launch.
            var bounds = WindowState == FormWindowState.Normal
                ? new System.Drawing.Rectangle(Location, Size)
                : RestoreBounds;
            settings.WindowX = bounds.X;
            settings.WindowY = bounds.Y;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;

            return settings;
        }

        private void SaveSettingsNow() => BuildCurrentSettings().Save();

        private List<AudioSessionControl> FindSessionsByProcessName(string processName)
        {
            var results = new List<AudioSessionControl>();
            var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive)
                        {
                            session.Dispose();
                            continue;
                        }

                        bool matches = false;
                        try
                        {
                            int pid = (int)session.GetProcessID;
                            if (pid != 0)
                            {
                                using var process = Process.GetProcessById(pid);
                                matches = string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        catch { /* process may have exited between enumeration and check */ }

                        if (matches)
                            results.Add(session); // caller is responsible for disposing these
                        else
                            session.Dispose();
                    }
                }
            }

            return results;
        }

        private readonly struct DetectedAppInfo
        {
            public string ProcessName { get; init; }
            public int SessionCount { get; init; }
        }

        // Also filters out anything IsProcessExcluded - hardcoded
        // (Voicemeeter) or user-blacklisted apps never appear here,
        // which means they also never reach the seed dropdown or the
        // discovery loop's tracking pass (both consume this list).
        private List<DetectedAppInfo> FindActiveSessionGroups()
        {
            var byProcess = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int currentProcessId = Environment.ProcessId;

            var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive)
                            continue;

                        string processName;
                        try
                        {
                            int pid = (int)session.GetProcessID;
                            if (pid == 0) continue;
                            if (pid == currentProcessId) continue;

                            using var process = Process.GetProcessById(pid);
                            processName = process.ProcessName;
                        }
                        catch
                        {
                            continue;
                        }

                        if (IsProcessExcluded(processName)) continue;

                        byProcess[processName] = byProcess.TryGetValue(processName, out var count) ? count + 1 : 1;
                    }
                }
            }

            return byProcess
                .Select(kvp => new DetectedAppInfo { ProcessName = kvp.Key, SessionCount = kvp.Value })
                .OrderBy(info => info.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void RefreshDetectedApps()
        {
            _refreshAppsButton.Enabled = false;
            _detectedAppsListBox.Items.Clear();
            _detectedAppsListBox.Items.Add("Scanning...");

            var thread = new Thread(() =>
            {
                List<DetectedAppInfo>? groups = null;
                Exception? failure = null;
                try { groups = FindActiveSessionGroups(); }
                catch (Exception ex) { failure = ex; }

                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;

                    _detectedAppsListBox.Items.Clear();
                    // Only kept in sync with the list box when groups
                    // were actually retrieved - see
                    // DetectedAppsListBox_MouseUp, which relies on index
                    // alignment between the two.
                    _lastDetectedGroups = groups ?? new List<DetectedAppInfo>();

                    if (failure != null)
                    {
                        _detectedAppsListBox.Items.Add($"Scan failed: {failure.Message}");
                        _lastDetectedGroups = new List<DetectedAppInfo>();
                    }
                    else if (groups!.Count == 0)
                    {
                        _detectedAppsListBox.Items.Add("No active audio sessions found.");
                        _lastDetectedGroups = new List<DetectedAppInfo>();
                    }
                    else
                    {
                        // Volume intentionally not shown here - this list
                        // refreshes only on demand, so its volume figure
                        // could disagree with the always-live one in
                        // "Currently leveled apps" below, which was
                        // confusing rather than useful.
                        foreach (var group in groups)
                        {
                            string sessionWord = group.SessionCount == 1 ? "session" : "sessions";
                            _detectedAppsListBox.Items.Add($"{group.ProcessName} ({group.SessionCount} {sessionWord})");
                        }
                    }

                    string? previousSelection = _seedFromAppComboBox.SelectedItem as string;
                    _seedFromAppComboBox.Items.Clear();
                    if (groups != null)
                        foreach (var group in groups)
                            _seedFromAppComboBox.Items.Add(group.ProcessName);
                    if (previousSelection != null && _seedFromAppComboBox.Items.Contains(previousSelection))
                        _seedFromAppComboBox.SelectedItem = previousSelection;

                    _refreshAppsButton.Enabled = true;
                }));
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        private void StartButtonClicked()
        {
            if (!_discoveryStarted)
            {
                _discoveryStarted = true;
                _discoveryStopRequested = false;

                _discoveryThread = new Thread(RunDiscoveryLoop) { IsBackground = true };
                _discoveryThread.Start();
            }

            _agcStopRequested = false;
            _agcThreadPending = 1;

            _startButton.Enabled = false;
            _stopButton.Enabled = true;
            _statusLabel.Text = "Tracking and leveling active apps.";

            _agcThread = new Thread(RunAgcLoop) { IsBackground = true };
            _agcThread.Start();
        }

        private void StopButtonClicked()
        {
            _agcStopRequested = true;
            _stopButton.Enabled = false;
            // Start re-enables itself once the AGC thread confirms it
            // exited - see AgcThreadFinished.
        }

        // Fired by dragging the slider - the reference can be set or
        // fine-tuned at any time, independent of any specific app. The
        // slider's 0-100% is mapped onto a realistic achievable RMS range
        // (ReferenceRmsScale), not raw 0.0-1.0 RMS - see its comment. The
        // label also shows the resulting target RMS, in the same "RMS X%"
        // format used by the tracked-apps list, so the two are directly
        // comparable. Bumping _referenceVersion is what tells the AGC
        // loop every tracked app is now due for a releveling and to
        // restart its calibration window fresh - see AppState.
        // CalibrationWindowRestartedForReferenceVersion.
        private void ReferenceTrackBarChanged()
        {
            double value = (_referenceTrackBar.Value / 100.0) * ReferenceRmsScale;
            lock (_referenceLock)
            {
                _referenceLevel = value;
                _referenceVersion++;
            }
            _referenceLabel.Text = $"Reference: {_referenceTrackBar.Value}% (target RMS {value:P1})";
        }

        // Convenience: moves the slider (and thus the reference) to match
        // a chosen app's current level, as a quick starting point. This
        // remains the most reliable way to calibrate the reference, since
        // it uses a real measured RMS instead of a guessed slider value.
        private void SeedReferenceFromApp()
        {
            if (_seedFromAppComboBox.SelectedItem is not string selectedName)
            {
                _statusLabel.Text = "Pick an app from the list first.";
                return;
            }

            AppState? state;
            lock (_appStatesLock) { _appStates.TryGetValue(selectedName, out state); }

            if (state == null)
            {
                _statusLabel.Text = $"{selectedName} isn't being tracked yet - wait a moment and try again.";
                return;
            }

            double smoothed;
            lock (state.Lock) { smoothed = state.SmoothedRms; }

            // Convert the measured RMS back into a slider percentage using
            // the same ReferenceRmsScale mapping used by
            // ReferenceTrackBarChanged, so seeding and manual dragging
            // agree on what a given slider position means.
            int percent = (int)Math.Round(Math.Clamp(smoothed / ReferenceRmsScale, 0, 1) * 100);
            _referenceTrackBar.Value = percent;
            ReferenceTrackBarChanged(); // Value setter above doesn't raise Scroll, so update explicitly
            _statusLabel.Text = $"Reference seeded from {selectedName}'s current level ({percent}%).";
        }

        // Creates and starts tracking one app: seeds its sessions from a
        // persisted volume if one exists, else InitialSeedVolume, then
        // launches its capture thread. Must be called with
        // _appStatesLock held. Callers are expected to have already
        // filtered out excluded process names (see IsProcessExcluded) -
        // this method doesn't re-check, since its only call site
        // (RunDiscoveryLoop) already sources names from
        // FindActiveSessionGroups, which filters them.
        private void CreateAndStartAppState(string name)
        {
            var state = new AppState(name);
            _appStates[name] = state;

            // If this app was already leveled in a previous run, reuse
            // the volume it was left at instead of always forcing
            // InitialSeedVolume - otherwise every restart silently undid
            // whatever level the previous session had converged on.
            float seedVolume = _persistedVolumes.TryGetValue(name, out float persisted)
                ? persisted
                : InitialSeedVolume;

            var initialSessions = FindSessionsByProcessName(name);
            try
            {
                foreach (var s in initialSessions)
                    s.SimpleAudioVolume.Volume = seedVolume;
                if (initialSessions.Count > 0)
                    state.LastKnownVolume = seedVolume;
            }
            finally
            {
                foreach (var s in initialSessions) s.Dispose();
            }

            state.CaptureThread = new Thread(() => RunCaptureLoop(state)) { IsBackground = true };
            state.CaptureThread.SetApartmentState(ApartmentState.MTA);
            state.CaptureThread.Start();
        }

        // Ramps every currently tracked app back to InitialSeedVolume and
        // forces a fresh calibration for each, for when the user wants
        // to start over from a known, neutral point rather than whatever
        // the running calibration had converged on.
        private void ResetVolumesButtonClicked()
        {
            List<AppState> snapshot;
            lock (_appStatesLock) { snapshot = _appStates.Values.ToList(); }

            foreach (var state in snapshot)
            {
                var sessions = FindSessionsByProcessName(state.ProcessName);
                try
                {
                    if (sessions.Count == 0) continue;

                    float previousVolume = sessions[0].SimpleAudioVolume.Volume;
                    RampSessionsVolume(sessions, previousVolume, InitialSeedVolume);

                    lock (state.Lock)
                    {
                        state.LastKnownVolume = InitialSeedVolume;
                        state.LeveledForReferenceVersion = 0; // forces a fresh calibration

                        // Start a brand-new calibration window right now,
                        // instead of clearing it to null and waiting for
                        // something else to re-arm it. Re-arming only
                        // happens on a real silence gap (a genuine new
                        // streak, detected by RunCaptureLoop) or on a
                        // reference change (detected by RunAgcLoop) -
                        // neither of which a Reset by itself causes. With
                        // continuous, uninterrupted audio playing (no
                        // silence gap) and no reference change, the old
                        // "clear to null" approach left the window
                        // permanently unset and the app stuck on
                        // "Calibrating..." forever. Setting it to "now"
                        // directly sidesteps both re-arm paths entirely.
                        state.CalibrationWindowStartUtc = DateTime.UtcNow;
                        state.CalibrationRmsSum = 0;
                        state.CalibrationSampleCount = 0;
                        state.LastEvaluatedCalibrationWindowStartUtc = null;
                        state.CalibrationWindowRestartedForReferenceVersion = 0;
                        state.LevelShiftSinceUtc = null;
                        state.LongTermRms = 0;
                        state.LastLevelUtc = DateTime.MinValue;
                        state.AgcState = "Reset - waiting for recalibration.";
                    }
                }
                finally
                {
                    foreach (var s in sessions) s.Dispose();
                }
            }

            _statusLabel.Text = "All tracked app volumes reset to 50%.";
        }

        // Continuously scans for active apps and keeps one capture
        // pipeline per detected process name running. Only one condition
        // automatically tears down and rebuilds a pipeline - the process
        // having genuinely exited, or its capture having stopped
        // receiving packets altogether (likely a recycled PID). This
        // narrower removal policy matters because process-loopback
        // capture cannot simply be torn down and recreated for the same
        // process on a whim: doing so reliably fails with E_UNEXPECTED
        // (0x8000FFFF) regardless of wait time between attempts - so
        // capture is only rebuilt when there's real evidence it needs to
        // be (genuine exit, or a stalled/recycled pipeline), not on
        // transient session states like a video/track change. Blacklisting
        // is handled separately and immediately by AddToBlacklist, not
        // here - see its comment. Runs until the form closes.
        private void RunDiscoveryLoop()
        {
            while (!_discoveryStopRequested)
            {
                List<DetectedAppInfo> groups;
                try
                {
                    groups = FindActiveSessionGroups();
                }
                catch
                {
                    Thread.Sleep(DiscoveryIntervalMilliseconds);
                    continue;
                }

                var currentNames = new HashSet<string>(
                    groups.Select(g => g.ProcessName), StringComparer.OrdinalIgnoreCase);

                lock (_appStatesLock)
                {
                    // Pass 1: remove apps whose process has genuinely
                    // exited, or whose capture has stopped receiving
                    // packets altogether while the app is still reporting
                    // active audio (likely a recycled process id - e.g.
                    // browser navigation).
                    foreach (var name in _appStates.Keys.ToList())
                    {
                        var state = _appStates[name];

                        bool stillRunning = Process.GetProcessesByName(name).Length > 0;
                        if (!stillRunning)
                        {
                            state.CaptureStopRequested = true;
                            _appStates.Remove(name);
                            continue;
                        }

                        if (currentNames.Contains(name))
                        {
                            DateTime lastPacket;
                            lock (state.Lock) { lastPacket = state.LastPacketUtc; }

                            if ((DateTime.UtcNow - lastPacket).TotalSeconds > CaptureStallRestartThresholdSeconds)
                            {
                                state.CaptureStopRequested = true;
                                _appStates.Remove(name);
                            }
                        }
                    }

                    // Pass 2: start tracking anything currently active
                    // that isn't already tracked - covers genuinely new
                    // apps and anything just removed above for stalling.
                    // currentNames already excludes blacklisted apps -
                    // see FindActiveSessionGroups.
                    foreach (var name in currentNames)
                    {
                        if (!_appStates.ContainsKey(name))
                            CreateAndStartAppState(name);
                    }
                }

                Thread.Sleep(DiscoveryIntervalMilliseconds);
            }
        }

        private void RefreshActiveAppsDisplay()
        {
            List<AppState> snapshot;
            lock (_appStatesLock) { snapshot = _appStates.Values.ToList(); }

            _activeAppsListBox.Items.Clear();
            _lastActiveAppsOrder.Clear();

            if (snapshot.Count == 0)
            {
                _activeAppsListBox.Items.Add("No apps tracked yet.");
            }
            else
            {
                foreach (var state in snapshot.OrderBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    double smoothed;
                    float volume;
                    string captureState, agcState;
                    lock (state.Lock)
                    {
                        smoothed = state.SmoothedRms;
                        volume = state.LastKnownVolume;
                        captureState = state.CaptureState;
                        agcState = state.AgcState;
                    }
                    // RMS shown to one decimal place - values near the
                    // silence threshold were indistinguishable when
                    // rounded to a whole percent.
                    _activeAppsListBox.Items.Add(
                        $"{state.ProcessName}: RMS {smoothed:P1} | Vol {volume:P0} | {captureState} | {agcState}");
                    // Keeps _lastActiveAppsOrder aligned index-for-index
                    // with what was just added above, so
                    // ActiveAppsListBox_MouseUp can map a right-clicked
                    // row back to a real process name.
                    _lastActiveAppsOrder.Add(state.ProcessName);
                }
            }

            // Leveling counts as running only between Start and Stop.
            // After Stop, discovery keeps tracking apps (so the list
            // stays live), but nothing is being leveled - say so
            // explicitly instead of the misleading "Tracking N app(s).".
            bool levelingRunning = Volatile.Read(ref _agcThreadPending) == 1 && !_agcStopRequested;

            if (!_discoveryStarted)
                _statusLabel.Text = "Press Start to begin tracking and leveling active apps.";
            else if (!levelingRunning)
                _statusLabel.Text = $"Leveling stopped. Tracking {snapshot.Count} app(s) - volumes left unchanged.";
            else if (_levelingPaused)
                _statusLabel.Text = $"Tracking {snapshot.Count} app(s). Leveling paused (Ctrl+Alt+L).";
            else
                _statusLabel.Text = $"Tracking {snapshot.Count} app(s).";
        }

        private void AgcThreadFinished()
        {
            Interlocked.Exchange(ref _agcThreadPending, 0);

            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => { if (!IsDisposed) _startButton.Enabled = true; }));
                return;
            }
            _startButton.Enabled = true;
        }

        // One capture pipeline per tracked app. Started by the discovery
        // thread when the app first appears (or is rebuilt after a
        // packet stall), runs until either it's told to stop or the form
        // closes.
        private void RunCaptureLoop(AppState state)
        {
            void SetCaptureState(string text)
            {
                lock (state.Lock) { state.CaptureState = text; }
            }

            var processes = Process.GetProcessesByName(state.ProcessName);
            if (processes.Length == 0)
            {
                SetCaptureState("Process not found.");
                return;
            }
            int processId = processes[0].Id;

            IntPtr unmanagedActivationBuffer = IntPtr.Zero;
            IActivateAudioInterfaceAsyncOperation? asyncOp = null;
            IAudioClient? audioClient = null;
            IAudioCaptureClient? captureClient = null;
            EventWaitHandle? bufferReadyEvent = null;

            try
            {
                int hr = 0;
                bool initialized = false;

                for (int attempt = 1; attempt <= MaxActivationAttempts && !state.CaptureStopRequested; attempt++)
                {
                    if (audioClient != null) { Marshal.ReleaseComObject(audioClient); audioClient = null; }
                    if (asyncOp != null) { Marshal.ReleaseComObject(asyncOp); asyncOp = null; }
                    if (unmanagedActivationBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(unmanagedActivationBuffer);
                        unmanagedActivationBuffer = IntPtr.Zero;
                    }

                    if (attempt > 1)
                    {
                        SetCaptureState($"Retrying activation ({attempt}/{MaxActivationAttempts})...");
                        Thread.Sleep(attempt * 500);
                    }
                    else
                    {
                        SetCaptureState("Activating loopback...");
                    }

                    using var completedEvent = new ManualResetEventSlim(false);
                    int activationResult = 0;
                    object? activatedInterface = null;

                    var handler = new ActivationCompletionHandler((resultHr, iface) =>
                    {
                        activationResult = resultHr;
                        activatedInterface = iface;
                        completedEvent.Set();
                    });

                    var loopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                    {
                        TargetProcessId = (uint)processId,
                        ProcessLoopbackMode = PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                    };
                    var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
                    {
                        ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
                        ProcessLoopbackParams = loopbackParams
                    };

                    var propvariant = WrapActivationParams(activationParams, out unmanagedActivationBuffer);
                    Guid iidAudioClient = IID_IAudioClient;

                    ActivateAudioInterfaceAsync(
                        VirtualAudioDeviceProcessLoopback, ref iidAudioClient, ref propvariant, handler, out asyncOp);

                    if (!completedEvent.Wait(TimeSpan.FromSeconds(5)))
                    {
                        hr = unchecked((int)0x80004005);
                        SetCaptureState("Timed out waiting for activation.");
                        continue;
                    }
                    if (activationResult < 0 || activatedInterface == null)
                    {
                        hr = activationResult;
                        SetCaptureState($"Activation failed: 0x{activationResult:X8}");
                        continue;
                    }

                    audioClient = (IAudioClient)activatedInterface;

                    var format = BuildHardcodedFormat();
                    var sessionGuid = Guid.Empty;
                    uint streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK
                                      | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                                      | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;

                    hr = audioClient.Initialize(
                        AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, streamFlags, 0, 0, ref format, ref sessionGuid);

                    if (hr < 0)
                    {
                        SetCaptureState($"IAudioClient.Initialize failed: 0x{hr:X8}");
                        continue;
                    }

                    initialized = true;
                    break;
                }

                if (state.CaptureStopRequested)
                    return;

                if (!initialized || audioClient is null)
                {
                    SetCaptureState($"Gave up after {MaxActivationAttempts} attempts (last error: 0x{hr:X8}).");
                    return;
                }

                bufferReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                hr = audioClient.SetEventHandle(bufferReadyEvent.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0)
                {
                    SetCaptureState($"SetEventHandle failed: 0x{hr:X8}");
                    return;
                }

                Guid iidCaptureClient = IID_IAudioCaptureClient;
                hr = audioClient.GetService(ref iidCaptureClient, out object captureObj);
                if (hr < 0)
                {
                    SetCaptureState($"GetService(IAudioCaptureClient) failed: 0x{hr:X8}");
                    return;
                }
                captureClient = (IAudioCaptureClient)captureObj;

                SetCaptureState("Starting capture...");
                hr = audioClient.Start();
                if (hr < 0)
                {
                    SetCaptureState($"Start failed: 0x{hr:X8}");
                    return;
                }

                SetCaptureState("Waiting for first audio packet...");

                double smoothedRms = 0;
                var stopwatch = Stopwatch.StartNew();
                double lastTimestampSeconds = stopwatch.Elapsed.TotalSeconds;
                double lastPacketSeconds = stopwatch.Elapsed.TotalSeconds;
                bool receivedFirstPacket = false;
                bool noDataWarningShown = false;

                while (!state.CaptureStopRequested)
                {
                    if (!bufferReadyEvent.WaitOne(500))
                    {
                        if (!noDataWarningShown &&
                            stopwatch.Elapsed.TotalSeconds - lastPacketSeconds > NoDataWarningThresholdSeconds)
                        {
                            noDataWarningShown = true;
                            SetCaptureState("No audio packets received - capture may be stalled.");
                        }
                        continue;
                    }

                    hr = captureClient.GetBuffer(out IntPtr data, out uint framesAvailable, out uint flags, out _, out _);
                    if (hr < 0) break;

                    lastPacketSeconds = stopwatch.Elapsed.TotalSeconds;
                    lock (state.Lock) { state.LastPacketUtc = DateTime.UtcNow; }

                    if (!receivedFirstPacket)
                    {
                        receivedFirstPacket = true;
                        SetCaptureState("Capturing.");
                    }
                    else if (noDataWarningShown)
                    {
                        noDataWarningShown = false;
                        SetCaptureState("Capturing.");
                    }

                    if (framesAvailable > 0)
                    {
                        double rawRms = 0;

                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero)
                        {
                            int sampleCount = (int)framesAvailable * 2; // stereo
                            short[] samples = new short[sampleCount];
                            Marshal.Copy(data, samples, 0, sampleCount);

                            double sumSquares = 0;
                            foreach (short s in samples)
                            {
                                double normalized = s / 32768.0;
                                sumSquares += normalized * normalized;
                            }
                            rawRms = Math.Sqrt(sumSquares / sampleCount);
                        }

                        double now = stopwatch.Elapsed.TotalSeconds;
                        double elapsed = now - lastTimestampSeconds;
                        lastTimestampSeconds = now;

                        double alpha = 1 - Math.Exp(-elapsed / SmoothingTimeConstantSeconds);
                        smoothedRms += (rawRms - smoothedRms) * alpha;

                        lock (state.Lock)
                        {
                            state.InstantRms = rawRms;
                            state.SmoothedRms = smoothedRms;

                            if (smoothedRms >= AgcSilenceThreshold)
                            {
                                DateTime nowUtc = DateTime.UtcNow;
                                state.LastAboveSilenceUtc = nowUtc;
                                if (state.ContinuousAudioSinceUtc == null)
                                {
                                    // A new streak just started - reset
                                    // both the streak marker and the
                                    // calibration window/average so they
                                    // only reflect this streak, not
                                    // history from before the gap.
                                    // (CalibrationWindowStartUtc can ALSO
                                    // be reset independently by RunAgcLoop
                                    // - on a confirmed gapless level shift
                                    // or the moment a reference change is
                                    // noticed - see their comments.)
                                    state.ContinuousAudioSinceUtc = nowUtc;
                                    state.CalibrationWindowStartUtc = nowUtc;
                                    state.CalibrationRmsSum = 0;
                                    state.CalibrationSampleCount = 0;
                                }

                                // Only samples captured after the leading
                                // CalibrationWarmupSeconds of the window
                                // have elapsed are folded into the
                                // average - see CalibrationWarmupSeconds.
                                // LastAboveSilenceUtc/ContinuousAudioSinceUtc
                                // above are still updated unconditionally,
                                // so "is there real audio" detection is
                                // unaffected by this.
                                bool pastWarmup = state.CalibrationWindowStartUtc.HasValue &&
                                    (nowUtc - state.CalibrationWindowStartUtc.Value).TotalSeconds >= CalibrationWarmupSeconds;
                                if (pastWarmup)
                                {
                                    state.CalibrationRmsSum += rawRms;
                                    state.CalibrationSampleCount++;
                                }
                            }
                            else
                            {
                                state.ContinuousAudioSinceUtc = null;
                            }
                        }
                    }

                    captureClient.ReleaseBuffer(framesAvailable);
                }

                audioClient.Stop();
                SetCaptureState(state.CaptureStopRequested ? "Capture stopped." : "Capture ended unexpectedly.");
            }
            catch (Exception ex)
            {
                SetCaptureState($"Capture error: {ex.Message}");
            }
            finally
            {
                bufferReadyEvent?.Dispose();
                if (captureClient != null) Marshal.ReleaseComObject(captureClient);
                if (audioClient != null) Marshal.ReleaseComObject(audioClient);
                if (asyncOp != null) Marshal.ReleaseComObject(asyncOp);
                if (unmanagedActivationBuffer != IntPtr.Zero) Marshal.FreeHGlobal(unmanagedActivationBuffer);
            }
        }

        // Fraction of the ideal volume correction applied per
        // recalibration (0-1). 1.0 = jump straight to the computed ideal
        // volume in one step; lower values close only part of the gap
        // each time, so a big mislevel is fixed over a few gentle
        // corrections instead of one large audible swing. 0.7 reaches
        // ~95% of the ideal level within 2 corrections (and ~99% within
        // 3), keeping the very first leveling responsive while still
        // softening the largest jumps.
        private const double CorrectionDampingFactor = 0.7;

        // Given the volume that produced a measured RMS, estimates the
        // volume that would produce the target RMS instead, in one step.
        // Assumes RMS scales proportionally with session volume (a linear
        // gain applied before capture). hitCeiling reports whether the
        // unclamped result exceeded 100% - a real physical limit (content
        // too quiet to reach the target without more than unity gain),
        // not a bug - so the caller can say so explicitly.
        private static float ComputeLeveledVolume(float currentVolume, double measuredRms, double targetRms, out bool hitCeiling)
        {
            // If the session is currently at/near zero, multiplying by
            // any ratio just stays at zero - fall back to
            // InitialSeedVolume as the base so leveling can still work.
            float baseVolume = currentVolume > 0.01f ? currentVolume : InitialSeedVolume;
            double ratio = targetRms / Math.Max(measuredRms, 0.0001);
            double raw = baseVolume * ratio;
            hitCeiling = raw > 1.0;
            double ideal = Math.Clamp(raw, 0.0, 1.0);

            // Damped correction: move only part of the way toward the
            // ideal volume (see CorrectionDampingFactor) so large
            // corrections are spread over multiple gentle adjustments.
            double damped = baseVolume + (ideal - baseVolume) * CorrectionDampingFactor;
            return (float)Math.Clamp(damped, 0.0, 1.0);
        }

        // Applies a computed volume change as a short linear ramp instead
        // of one instant write, so a correction is heard as "the sound
        // got quieter/louder" rather than a sudden cut. Blocks the
        // calling (AGC) thread for VolumeRampDurationSeconds, which is
        // fine here - recalibrations are occasional by design, and this
        // only delays processing of the NEXT tracked app in the same AGC
        // cycle by about a second, not the UI thread.
        private static void RampSessionsVolume(List<AudioSessionControl> sessions, float fromVolume, float toVolume)
        {
            if (Math.Abs(toVolume - fromVolume) < 0.001f)
            {
                // Nothing meaningful to ramp - set it directly and skip
                // the step loop entirely.
                foreach (var s in sessions) s.SimpleAudioVolume.Volume = toVolume;
                return;
            }

            int steps = Math.Max(1, (int)(VolumeRampDurationSeconds * 1000 / VolumeRampStepMilliseconds));
            for (int i = 1; i <= steps; i++)
            {
                float stepVolume = fromVolume + (toVolume - fromVolume) * ((float)i / steps);
                foreach (var s in sessions) s.SimpleAudioVolume.Volume = stepVolume;
                if (i < steps) Thread.Sleep(VolumeRampStepMilliseconds);
            }
        }

        // Evaluates every tracked app once per cycle. Always re-reads and
        // re-publishes the live session volume first, regardless of
        // whether anything is about to change, so the displayed "Vol"
        // never goes stale. Performs at most one volume change per app
        // per cycle: a fresh calibration (new app, new calibration
        // window, or reference change) or an occasional drift-triggered
        // recalibration - never a continuous correction. Any actual
        // volume change is applied as a short ramp, not an instant jump.
        // When leveling is paused via hotkey, every app's live
        // volume/RMS is still refreshed for display, but no calibration
        // decision runs and no calibration bookkeeping is touched, so
        // resuming picks up exactly where it left off.
        private void RunAgcLoop()
        {
            try
            {
                while (!_agcStopRequested)
                {
                    Thread.Sleep(AgcIntervalMilliseconds);
                    if (_agcStopRequested) break;

                    double reference;
                    int referenceVersion;
                    lock (_referenceLock) { reference = _referenceLevel; referenceVersion = _referenceVersion; }

                    List<AppState> snapshot;
                    lock (_appStatesLock) { snapshot = _appStates.Values.ToList(); }

                    DateTime now = DateTime.UtcNow;

                    foreach (var state in snapshot)
                    {
                        var sessions = FindSessionsByProcessName(state.ProcessName);
                        try
                        {
                            if (sessions.Count == 0)
                            {
                                lock (state.Lock) { state.AgcState = "Session not found."; }
                                continue;
                            }

                            // Always read the live session volume, even
                            // when we're not about to act on it - this is
                            // what keeps "Vol" in the tracked-apps list in
                            // sync with volume changes made outside the
                            // app (Windows volume mixer, the app's own
                            // UI, etc.).
                            float currentVolume = sessions[0].SimpleAudioVolume.Volume;

                            // Paused via the global hotkey - show it and
                            // skip straight past every calibration
                            // decision below, without touching any of the
                            // calibration-window bookkeeping owned by
                            // RunCaptureLoop/RunAgcLoop.
                            if (_levelingPaused)
                            {
                                lock (state.Lock)
                                {
                                    state.LastKnownVolume = currentVolume;
                                    state.AgcState = "Paused (Ctrl+Alt+L) - not leveling.";
                                }
                                continue;
                            }

                            double smoothed, longTermRms, calibrationSum;
                            int calibrationCount, leveledVersion, calibrationWindowRestartedForReferenceVersion;
                            DateTime? calibrationWindowStart, lastEvaluatedCalibrationWindowStart, levelShiftSinceUtc;
                            DateTime lastAboveSilence, lastLevelUtc;
                            lock (state.Lock)
                            {
                                smoothed = state.SmoothedRms;
                                calibrationWindowStart = state.CalibrationWindowStartUtc;
                                lastAboveSilence = state.LastAboveSilenceUtc;
                                lastLevelUtc = state.LastLevelUtc;
                                leveledVersion = state.LeveledForReferenceVersion;
                                longTermRms = state.LongTermRms;
                                calibrationSum = state.CalibrationRmsSum;
                                calibrationCount = state.CalibrationSampleCount;
                                lastEvaluatedCalibrationWindowStart = state.LastEvaluatedCalibrationWindowStartUtc;
                                levelShiftSinceUtc = state.LevelShiftSinceUtc;
                                calibrationWindowRestartedForReferenceVersion = state.CalibrationWindowRestartedForReferenceVersion;
                            }

                            bool audible = smoothed >= AgcSilenceThreshold;

                            // Keep the slow long-term average up to date
                            // whenever there's real audio to sample -
                            // used only for the drift-based recalibration
                            // check further below.
                            if (audible)
                            {
                                double elapsedSeconds = AgcIntervalMilliseconds / 1000.0;
                                double alpha = 1 - Math.Exp(-elapsedSeconds / LongTermRmsTimeConstantSeconds);
                                longTermRms = longTermRms <= 0 ? smoothed : longTermRms + (smoothed - longTermRms) * alpha;
                            }

                            string silenceHint = "";
                            if (!audible)
                            {
                                double silentForSeconds = (now - lastAboveSilence).TotalSeconds;
                                if (silentForSeconds > SilenceStallWarningThresholdSeconds)
                                    silenceHint = $" Silent for {silentForSeconds:F0}s - if this seems wrong, try closing and reopening {state.ProcessName}.";
                            }

                            bool firstEverLevel = leveledVersion == 0;
                            bool dueToReferenceChange = leveledVersion != referenceVersion;
                            bool dueToNewCalibrationWindow = calibrationWindowStart.HasValue && calibrationWindowStart != lastEvaluatedCalibrationWindowStart;
                            bool dueForFreshCalibration = dueToReferenceChange || dueToNewCalibrationWindow;

                            // Only the level-shift branch and the
                            // reference-change-restart branch below ever
                            // need to restart the calibration
                            // window/average from the AGC thread's side -
                            // every other case leaves
                            // CalibrationWindowStartUtc / CalibrationRmsSum
                            // / CalibrationSampleCount alone, since those
                            // three are otherwise owned by the capture
                            // thread (RunCaptureLoop) and writing them
                            // unconditionally here would race with, and
                            // could erase, samples it's actively
                            // accumulating.
                            bool resetCalibrationWindow = false;
                            DateTime? levelShiftSince = levelShiftSinceUtc;

                            string agcState;

                            if (reference < 0)
                            {
                                agcState = "Waiting for reference level.";
                            }
                            else if (dueForFreshCalibration)
                            {
                                if (dueToReferenceChange && calibrationWindowRestartedForReferenceVersion != referenceVersion)
                                {
                                    // A reference change alone (no real
                                    // streak break, no confirmed level
                                    // shift) would otherwise leave
                                    // CalibrationWindowStartUtc exactly
                                    // where it was - which, for an app
                                    // that had already been playing for a
                                    // while, could be several minutes
                                    // old. So a reference change always
                                    // restarts the calibration window
                                    // from this exact moment too, exactly
                                    // like a real streak break or a
                                    // confirmed level shift. Gated by
                                    // CalibrationWindowRestartedFor-
                                    // ReferenceVersion so this happens
                                    // once per reference change, not
                                    // every cycle spent waiting for the
                                    // fresh window to fill.
                                    calibrationWindowStart = now;
                                    calibrationSum = 0;
                                    calibrationCount = 0;
                                    resetCalibrationWindow = true;
                                    calibrationWindowRestartedForReferenceVersion = referenceVersion;
                                }

                                // Wait for the warmup slice PLUS a full
                                // calibration window of samples past it
                                // before trusting the average - see
                                // CalibrationWarmupSeconds.
                                bool continuousLongEnough = calibrationWindowStart.HasValue &&
                                    (now - calibrationWindowStart.Value).TotalSeconds >= CalibrationWarmupSeconds + CalibrationWindowSeconds;

                                if (!audible)
                                {
                                    agcState = "Waiting for audio to calibrate..." + silenceHint;
                                }
                                else if (!continuousLongEnough)
                                {
                                    agcState = "Calibrating...";
                                }
                                else
                                {
                                    double calibratedRms = calibrationCount > 0 ? calibrationSum / calibrationCount : smoothed;
                                    float previousVolume = currentVolume;
                                    currentVolume = ComputeLeveledVolume(currentVolume, calibratedRms, reference, out bool hitCeiling);
                                    RampSessionsVolume(sessions, previousVolume, currentVolume);

                                    leveledVersion = referenceVersion;
                                    lastEvaluatedCalibrationWindowStart = calibrationWindowStart;
                                    lastLevelUtc = now;
                                    // With a damped correction we have NOT
                                    // fully reached the target yet: the RMS
                                    // moves toward the reference by the same
                                    // fraction the volume did. Reflect that
                                    // here so the drift check below doesn't
                                    // immediately see a phantom deviation
                                    // (or, conversely, ignore a real one).
                                    longTermRms = calibratedRms + (reference - calibratedRms) * CorrectionDampingFactor;
                                    levelShiftSince = null; // a fresh calibration just happened - don't carry over a stale candidate

                                    string baseMsg = firstEverLevel ? "Leveled." : "Re-leveled (new audio or reference change).";
                                    agcState = hitCeiling ? baseMsg + " At maximum volume - can't fully reach target." : baseMsg;
                                }
                            }
                            else
                            {
                                // Already leveled for the current
                                // reference and calibration window. Two
                                // independent checks decide whether a
                                // correction is due, evaluated in this
                                // order:
                                //
                                //  1) Level-shift detection (fast, ~
                                //     LevelShiftConfirmSeconds): catches a
                                //     genuine step change in content
                                //     loudness - most importantly a track
                                //     change with NO silence gap (gapless
                                //     local file playback), which never
                                //     breaks ContinuousAudioSinceUtc and
                                //     so is invisible to
                                //     dueToNewCalibrationWindow above.
                                //     Confirmed only after the deviation
                                //     has held continuously for
                                //     LevelShiftConfirmSeconds (spanning
                                //     multiple AGC cycles), so a single
                                //     loud chorus or dialogue peak - which
                                //     fades back within a couple of
                                //     seconds - never triggers it.
                                //
                                //  2) The original drift-based
                                //     recalibration, gated by
                                //     RecalibrationIntervalSeconds: a slow
                                //     safety net for smaller, sustained
                                //     deviations that never cross
                                //     LevelShiftDeviationRatio, or for
                                //     genuinely gradual drift rather than
                                //     a step change.
                                //
                                // instantRatio is computed once and reused
                                // both to drive (1) and for the cosmetic
                                // "Louder/Quieter than target" status text
                                // below - a single source of truth for
                                // "how far off is this app right now".
                                double instantRatio = audible
                                    ? Math.Max(smoothed / reference, reference / Math.Max(smoothed, 0.0001))
                                    : 0;

                                bool levelShiftCandidate = audible && instantRatio >= LevelShiftDeviationRatio;
                                if (levelShiftCandidate)
                                    levelShiftSince ??= now;
                                else
                                    levelShiftSince = null;

                                bool levelShiftConfirmed = levelShiftCandidate && levelShiftSince.HasValue &&
                                    (now - levelShiftSince.Value).TotalSeconds >= LevelShiftConfirmSeconds;

                                double secondsSinceLevel = (now - lastLevelUtc).TotalSeconds;
                                bool driftCheckDue = secondsSinceLevel >= RecalibrationIntervalSeconds && audible && longTermRms > 0;
                                double driftRatio = driftCheckDue
                                    ? (longTermRms > reference ? longTermRms / reference : reference / longTermRms)
                                    : 0;

                                if (levelShiftConfirmed)
                                {
                                    // Don't level on this single
                                    // instantaneous reading: restart the
                                    // calibration window from now
                                    // (discarding the old, now-stale
                                    // accumulated average) and let the
                                    // dueForFreshCalibration branch above
                                    // pick it up and actually level once
                                    // CalibrationWarmupSeconds +
                                    // CalibrationWindowSeconds of fresh
                                    // samples have been collected - same
                                    // averaging safeguard a real streak
                                    // break already gets.
                                    resetCalibrationWindow = true;
                                    calibrationWindowStart = now;
                                    levelShiftSince = null;
                                    agcState = "Detected a sustained level change - recalibrating...";
                                }
                                else if (driftCheckDue && driftRatio >= RecalibrationDriftFactor)
                                {
                                    float previousVolume = currentVolume;
                                    double preDriftRms = longTermRms;
                                    currentVolume = ComputeLeveledVolume(currentVolume, longTermRms, reference, out bool hitCeiling);
                                    RampSessionsVolume(sessions, previousVolume, currentVolume);

                                    lastLevelUtc = now;
                                    // Damped correction only moved the RMS
                                    // part-way toward the reference - see
                                    // the matching note in the fresh-
                                    // calibration branch above.
                                    longTermRms = preDriftRms + (reference - preDriftRms) * CorrectionDampingFactor;

                                    agcState = hitCeiling
                                        ? "Re-leveled (level had drifted). At maximum volume - can't fully reach target."
                                        : "Re-leveled (level had drifted).";
                                }
                                else if (!audible)
                                {
                                    agcState = "Leveled. (currently quiet)" + silenceHint;
                                }
                                else
                                {
                                    agcState = instantRatio <= DisplayAlignmentRatio
                                        ? "Leveled. On target."
                                        : smoothed > reference
                                            ? "Leveled. Louder than target right now (normal within-track variation)."
                                            : "Leveled. Quieter than target right now (normal within-track variation).";
                                }
                            }

                            lock (state.Lock)
                            {
                                state.LastKnownVolume = currentVolume;
                                state.AgcState = agcState;
                                state.LeveledForReferenceVersion = leveledVersion;
                                state.LastEvaluatedCalibrationWindowStartUtc = lastEvaluatedCalibrationWindowStart;
                                state.LastLevelUtc = lastLevelUtc;
                                state.LongTermRms = longTermRms;
                                state.LevelShiftSinceUtc = levelShiftSince;
                                state.CalibrationWindowRestartedForReferenceVersion = calibrationWindowRestartedForReferenceVersion;

                                if (resetCalibrationWindow)
                                {
                                    state.CalibrationWindowStartUtc = calibrationWindowStart;
                                    state.CalibrationRmsSum = 0;
                                    state.CalibrationSampleCount = 0;
                                }
                            }
                        }
                        finally
                        {
                            foreach (var s in sessions) s.Dispose();
                        }
                    }
                }
            }
            finally
            {
                AgcThreadFinished();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Confirm before actually closing, but only for a closure the
            // user explicitly initiated (the window's X button, or the
            // tray "Exit" item, which shares this same close path) - not
            // for a Windows shutdown/logoff, where blocking on a dialog
            // that might never get answered could hang the whole system
            // shutdown. Skipped entirely once the user has dismissed it
            // via "Don't ask me again" - see ExitConfirmationDialog.
            if (e.CloseReason == CloseReason.UserClosing && _showExitConfirmation)
            {
                using var dialog = new ExitConfirmationDialog();
                var result = dialog.ShowDialog(this);

                if (result == DialogResult.Yes && dialog.DontShowAgain)
                    _showExitConfirmation = false;

                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _uiRefreshTimer.Stop();
            _agcStopRequested = true;
            _discoveryStopRequested = true;

            // Persist everything (reference, per-app volumes, blacklist,
            // presets, checkboxes, window geometry - see
            // BuildCurrentSettings), hide the tray icon immediately
            // (rather than leaving it lingering until the process fully
            // exits), and release the pause hotkey.
            SaveSettingsNow();

            _trayIcon.Visible = false;
            if (_hotkeyRegistered)
                UnregisterHotKey(Handle, PauseHotkeyId);

            List<AppState> snapshot;
            lock (_appStatesLock)
            {
                snapshot = _appStates.Values.ToList();
                foreach (var state in snapshot)
                    state.CaptureStopRequested = true;
            }

            _agcThread?.Join(TimeSpan.FromSeconds(2));
            _discoveryThread?.Join(TimeSpan.FromSeconds(2));
            foreach (var state in snapshot)
                state.CaptureThread?.Join(TimeSpan.FromSeconds(1));

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _uiRefreshTimer?.Dispose();
                _trayIcon?.Dispose();
                _trayMenu?.Dispose();
                _detectedAppsContextMenu?.Dispose();
                _activeAppsContextMenu?.Dispose();
                _deviceEnumerator?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [ComVisible(true)]
    internal class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly Action<int, object?> _onCompleted;

        public ActivationCompletionHandler(Action<int, object?> onCompleted)
        {
            _onCompleted = onCompleted;
        }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object iface);
                _onCompleted(hr, hr >= 0 ? iface : null);
            }
            catch
            {
                _onCompleted(unchecked((int)0x80004005), null); // E_FAIL
            }
        }
    }

    public partial class MainForm
    {
        private void InitializeComponent()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(700, 784);
            Name = "MainForm";
            ResumeLayout(false);
        }
    }
}

