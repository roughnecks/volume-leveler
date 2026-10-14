// Settings.cs (file completo)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VolumeLeveler
{
    // A single saved reference preset, user-labeled.
    public sealed class Preset
    {
        public string Label { get; set; } = "";
        public int ReferencePercent { get; set; } = 50;
    }

    // Plain-data settings persisted across runs. Autostart-with-Windows
    // status is NOT stored here: the Windows registry Run key (see
    // AutoStartHelper) is the single source of truth for that, so the UI
    // always reflects reality even if the registry was changed outside
    // the app.
    public sealed class Settings
    {
        public int ReferencePercent { get; set; } = 50;

        // Last known Windows volume for each tracked app, keyed by
        // process name (case-insensitive) - restored on the next launch
        // instead of always forcing InitialSeedVolume, so a restart
        // doesn't silently undo whatever level a previous session had
        // converged on. See MainForm.CreateAndStartAppState.
        public Dictionary<string, float> LastKnownVolumes { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // User-added process names excluded from tracking and from the
        // "Detected apps" list, on top of the built-in hardcoded
        // exclusions (see MainForm.HardcodedExcludedProcessNames).
        // Case-insensitive matching is done by the consumer (MainForm) -
        // this is plain persisted data.
        public List<string> BlacklistedProcessNames { get; set; } = new();

        // Exactly three reference presets, user-labeled. Always has 3
        // entries after Load() - see EnsureThreePresets.
        public List<Preset> Presets { get; set; } = new()
        {
            new Preset { Label = "Preset 1", ReferencePercent = 50 },
            new Preset { Label = "Preset 2", ReferencePercent = 50 },
            new Preset { Label = "Preset 3", ReferencePercent = 50 }
        };

        // Whether tracking/leveling itself should start automatically as
        // soon as the app launches, instead of requiring an explicit
        // Start press every time - most useful once a reference/presets
        // are already configured from a previous session. See
        // MainForm.LoadPersistedSettings / BuildCurrentSettings /
        // StartButtonClicked.
        public bool AutoStartLevelingOnLaunch { get; set; } = false;

        // Whether the exit confirmation dialog still appears when the
        // user closes the main window - false once the user has ticked
        // "Don't ask me again" on that dialog. See
        // MainForm.ExitConfirmationDialog / OnFormClosing.
        public bool ShowExitConfirmation { get; set; } = true;

        // Main window position/size, restored on the next launch - see
        // MainForm.LoadPersistedSettings / BuildCurrentSettings.
        // int.MinValue for X/Y and 0 for Width/Height both mean "never
        // saved yet" (a real saved position could legitimately be
        // negative on a multi-monitor setup, so 0/negative isn't usable
        // as the X/Y sentinel - int.MinValue is used there instead). The
        // consumer falls back to its normal design-time default
        // size/position whenever these sentinels are seen.
        public int WindowX { get; set; } = int.MinValue;
        public int WindowY { get; set; } = int.MinValue;
        public int WindowWidth { get; set; } = 0;
        public int WindowHeight { get; set; } = 0;

        private static string SettingsDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VolumeLeveler");

        private static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

        // Loads settings from disk, falling back to defaults on any
        // failure (missing file, corrupted JSON, permission issue) - a
        // broken settings file should never prevent the app from
        // starting.
        public static Settings Load()
        {
            try
            {
                string json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                settings.EnsureThreePresets();
                return settings;
            }
            catch
            {
                return new Settings();
            }
        }

        // Guards against a hand-edited or older settings.json having a
        // different number of presets than the UI expects (exactly 3
        // slots) - pads or truncates rather than failing.
        private void EnsureThreePresets()
        {
            Presets ??= new List<Preset>();
            while (Presets.Count < 3)
                Presets.Add(new Preset { Label = $"Preset {Presets.Count + 1}", ReferencePercent = 50 });
            if (Presets.Count > 3)
                Presets = Presets.Take(3).ToList();
        }

        // Best-effort save - a failure here (e.g. no write permission) is
        // not worth surfacing to the user, since it only affects what's
        // remembered for the next launch.
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // Ignored by design - see class comment.
            }
        }
    }
}