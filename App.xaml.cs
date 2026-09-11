using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using CommandCenter.Model;

namespace CommandCenter
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Runs before App.xaml's StartupUri creates MainWindow. If the user opted into "Run as
        // Administrator" (Settings > RunAsAdministrator - see AppSettings.cs) and this process
        // isn't already elevated, relaunch elevated via the "runas" verb and shut this
        // (unelevated, about-to-be-redundant) instance down before it ever creates a window.
        // Falls through to the normal unelevated startup (base.OnStartup, which is what actually
        // triggers the StartupUri-driven MainWindow creation) for every other outcome - setting
        // off, already elevated, no exe path available, the user declined the UAC prompt, or any
        // other failure - so a declined prompt still leaves the user with a working (unelevated)
        // app rather than nothing.
        protected override void OnStartup(StartupEventArgs e)
        {
            if (TryRelaunchElevatedIfRequested())
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        // Returns true only when a relaunch was actually kicked off - the caller must then shut
        // this instance down without creating a window. Every other path (see comment above)
        // returns false so startup continues normally in this same process.
        private static bool TryRelaunchElevatedIfRequested()
        {
            AppSettings settings;
            try
            {
                settings = new SettingsService().Load();
            }
            catch (Exception)
            {
                // Can't tell what the user wants - safest is to just start normally.
                return false;
            }

            if (!settings.RunAsAdministrator || IsRunningElevated())
            {
                return false;
            }

            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Win32Exception)
            {
                // UAC prompt declined (or elevation blocked by policy) - continue unelevated
                // rather than trapping the user in a relaunch loop or leaving them with no app.
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsRunningElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
