using System;
using System.Windows.Forms;
using VolumeLeveler;

namespace VolumeLeveler
{
    // Application entry point.
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Standard WinForms bootstrap for .NET 8+.
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }
}
