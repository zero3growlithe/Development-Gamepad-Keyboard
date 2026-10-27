using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace GamepadKeyboard
{
    /// <summary>
    /// Application entry point. Code-behind only (no StartupUri), so we fully
    /// control lifetime: single instance, tray, overlays, gamepad polling.
    /// Any startup crash is logged to %APPDATA%\DevelopmentGamepadKeyboard\crash.log.
    /// </summary>
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private AppOrchestrator? _orchestrator;

        protected override void OnStartup(StartupEventArgs e)
        {
            // log unhandled exceptions instead of dying silently (tray app, no console)
            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainException;

            base.OnStartup(e);

            Settings.AppSettings.Load();
            if (Settings.AppSettings.Instance.AdminLaunch
                && !Util.LaunchUtil.IsAdmin()
                && Util.LaunchUtil.RestartElevated())
            {
                Shutdown();
                return;
            }

            _singleInstanceMutex = new Mutex(true, "DevelopmentGamepadKeyboard_SingleInstance", out bool isNew);
            if (!isNew && Array.Exists(e.Args,
                    arg => string.Equals(arg, "--elevated-restart", StringComparison.OrdinalIgnoreCase)))
            {
                try { isNew = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(3)); }
                catch (AbandonedMutexException) { isNew = true; }
            }
            if (!isNew)
            {
                Log("single instance: another copy is already running - this launch exits. " +
                    "Close the running instance (tray icon -> Exit) before starting the installed UIAccess copy.");
                Shutdown();
                return;
            }

            Log("=== launch ===");
            Log(DescribeBuild());

            // UIAccess is what puts our windows above the Start menu, and Windows grants
            // it only to a signed exe in a secure location started WITHOUT elevation.
            bool uiAccess = Native.ProcessToken.HasUiAccess();
            bool elevated = Native.ProcessToken.IsElevated();
            bool secureLocation = Native.ProcessToken.IsInSecureLocation();
            Log("run mode: uiaccess=" + (uiAccess ? "yes" : "no") +
                " elevated=" + (elevated ? "yes" : "no") +
                " secure_location=" + (secureLocation ? "yes" : "no") +
                " exe=" + (Environment.ProcessPath ?? "?"));
            if (elevated)
            {
                Log("WARNING: this copy is elevated - an elevated process can never hold UIAccess, " +
                    "so the overlay stays below the Start menu. Turn OFF \"Run as Administrator\" " +
                    "(and Settings > AdminLaunch) and start the installed copy from the Start menu.");
            }
            else if (!uiAccess)
            {
                Log("note: no UIAccess in this process - stock build, or the installed copy is unsigned / " +
                    "not in Program Files / launch refused. The overlay stays below the Start menu.");
            }
            if (Settings.AppSettings.Instance.AdminLaunch)
            {
                Log("WARNING: AdminLaunch is enabled in settings.json - every launch elevates the app, " +
                    "which permanently costs the UIAccess flag. Set it to false for the UIAccess build.");
            }

            Input.GamepadSnapshot.Deadzone = Settings.AppSettings.Instance.StickDeadzone;
            // overlay position is restored in KeyboardOverlay ctor via OverlayLeft/Top

            _orchestrator = new AppOrchestrator();
            _orchestrator.Start();

            // keep running with no visible window (tray app)
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Log("startup complete");
        }

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log("dispatcher exception: " + e.Exception);
            e.Handled = true; // tray app: survive UI-thread hiccups
        }

        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Log("domain exception: " + e.ExceptionObject);
        }

        internal static void Log(string message)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DevelopmentGamepadKeyboard");
                System.IO.Directory.CreateDirectory(dir);
                File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch { /* logging must never crash the app */ }
        }

        /// <summary>
        /// Version + build provenance for crash.log diagnostics: the binary can
        /// identify itself when field reports and deployments drift apart.
        /// BuildInfo.Sha is generated at build time from CI's GITHUB_SHA (local
        /// builds embed "-local").
        /// </summary>
        private static string DescribeBuild()
        {
            string sha;
            try { sha = GamepadKeyboard.BuildInfo.Sha; }
            catch { sha = ""; }
            if (string.IsNullOrWhiteSpace(sha) || sha == "$(GITHUB_SHA)") sha = "-local";
            return "version 0.2.0 (" + Environment.OSVersion.VersionString + "; build " + sha + ")";
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("exit");
            _orchestrator?.Dispose();
            _singleInstanceMutex?.Dispose();
            Settings.AppSettings.Save();
            base.OnExit(e);
        }
    }
}
