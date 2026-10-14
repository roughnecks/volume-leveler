// AutoStartHelper.cs
using Microsoft.Win32.TaskScheduler;
using System;
using System.Security.Principal;
using Windows.ApplicationModel;

namespace VolumeLeveler
{
    // Manages the "start with Windows" setting via a custom Scheduled Task
    // (not the MSIX windows.startupTask manifest extension, which proved
    // unreliable at launch time on some systems). The task fires at logon
    // after a short delay and launches the app through its AUMID via
    // "explorer.exe shell:AppsFolder\<AUMID>" - launching the installed
    // .exe path directly is denied by the WindowsApps folder's ACLs
    // outside of the package activation machinery Explorer uses.
    //
    // This task does not appear in Task Manager's Startup tab (that tab
    // only covers Run-key, Startup-folder, and windows.startupTask
    // entries) - it shows up in Task Scheduler instead.
    internal static class AutoStartHelper
    {
        private const string TaskName = "VolumeLevelerAutoStart";
        private const string AppId = "App"; // Matches Package.appxmanifest's <Application Id="App" ...>.

        // How long after logon to wait before launching, to give the
        // shell and package infrastructure time to finish settling.
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

        private static string Aumid => $"{Package.Current.Id.FamilyName}!{AppId}";

        public static System.Threading.Tasks.Task<bool> IsEnabledAsync() => System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var ts = new TaskService();
                var task = ts.GetTask(TaskName);
                return task is { Enabled: true };
            }
            catch
            {
                return false;
            }
        });

        public static System.Threading.Tasks.Task SetEnabledAsync(bool enabled) => System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string arguments = $"shell:AppsFolder\\{Aumid}";

                using var ts = new TaskService();
                var existing = ts.GetTask(TaskName);
                TaskDefinition td;

                if (existing != null)
                {
                    td = existing.Definition;
                }
                else
                {
                    td = ts.NewTask();
                    td.RegistrationInfo.Description = "Starts Volume Leveler shortly after sign-in.";
                    td.Principal.LogonType = TaskLogonType.InteractiveToken;

                    var trigger = new LogonTrigger
                    {
                        UserId = WindowsIdentity.GetCurrent().Name,
                        Delay = StartupDelay
                    };
                    td.Triggers.Add(trigger);

                    td.Settings.DisallowStartIfOnBatteries = false;
                    td.Settings.StopIfGoingOnBatteries = false;
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero; // No limit - this is a persistent tray app, not a short-lived task.
                    td.Settings.AllowHardTerminate = true;

                    td.Actions.Add(new ExecAction("explorer.exe", arguments));
                }

                if (td.Actions.Count > 0 && td.Actions[0] is ExecAction action)
                {
                    action.Path = "explorer.exe";
                    action.Arguments = arguments;
                }

                td.Settings.Enabled = enabled;

                ts.RootFolder.RegisterTaskDefinition(TaskName, td, TaskCreation.CreateOrUpdate, null, null, TaskLogonType.InteractiveToken);
            }
            catch
            {
                // Best-effort: if this fails, the checkbox simply won't
                // reflect the change and the user can retry.
            }
        });
    }
}
