// IconFactory.cs (file completo)
using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VolumeLeveler
{
    // Step 15: provides the app's real icon (assets/vl.ico, embedded as
    // a resource - see the .csproj) for both the window/taskbar icon
    // (Form.Icon) and the tray icon (NotifyIcon.Icon) - see MainForm's
    // constructor and SetupTrayIcon. CreatePlaceholderTrayIcon is kept
    // as a fallback: if the embedded resource is ever missing or fails
    // to load for any reason, CreateAppIcon falls back to it instead of
    // crashing the app over a missing icon.
    internal static class IconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        // Must match the <LogicalName> set on the EmbeddedResource item
        // in the .csproj exactly - if the two ever drift apart,
        // GetManifestResourceStream below returns null and CreateAppIcon
        // silently falls back to the placeholder.
        private const string EmbeddedIconResourceName = "VolumeLeveler.vl.ico";

        // Step 13: updated to the app's specific accent orange (#FB6D06)
        // to match the dark theme. Still used by the placeholder
        // fallback below.
        private static readonly Color AccentColor = Color.FromArgb(0xFB, 0x6D, 0x06);
        private static readonly Color OutlineColor = Color.FromArgb(0xB0, 0x4E, 0x00);

        // Step 15: loads the real designed icon from the embedded
        // resource. The source .ico contains multiple resolutions, so
        // the Icon class automatically picks the best-fitting size for
        // each usage (window icon, tray icon, etc.) - no manual size
        // selection needed here.
        public static Icon CreateAppIcon()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(EmbeddedIconResourceName);
                if (stream != null)
                    return new Icon(stream);
            }
            catch
            {
                // Fall through to the placeholder below - a malformed or
                // missing icon resource shouldn't prevent the app from
                // starting.
            }

            return CreatePlaceholderTrayIcon();
        }

        // Draws a filled orange circle with a thin darker outline - not
        // meant to be pretty, just distinguishable in the tray. Kept as
        // a fallback for CreateAppIcon (Step 15) rather than removed.
        public static Icon CreatePlaceholderTrayIcon()
        {
            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                using var fill = new SolidBrush(AccentColor);
                using var outline = new Pen(OutlineColor, 2f);

                g.FillEllipse(fill, 2, 2, 28, 28);
                g.DrawEllipse(outline, 2, 2, 28, 28);
            }

            IntPtr hIcon = bitmap.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
    }
}