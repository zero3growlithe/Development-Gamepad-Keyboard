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

            _singleInstanceMutex = new Mutex(true, "DevelopmentGamepadKeyboard_SingleInstance", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            Log("=== launch ===");

            Settings.AppSettings.Load();

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