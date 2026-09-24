using System;
using System.Threading;
using System.Windows;

namespace GamepadKeyboard
{
    /// <summary>
    /// Application entry point. Code-behind only (no StartupUri), so we fully
    /// control lifetime: single instance, tray, overlays, gamepad polling.
    /// </summary>
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private AppOrchestrator? _orchestrator;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstanceMutex = new Mutex(true, "DevelopmentGamepadKeyboard_SingleInstance", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            Settings.AppSettings.Load();

            _orchestrator = new AppOrchestrator();
            _orchestrator.Start();

            // keep running with no visible window (tray app)
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _orchestrator?.Dispose();
            _singleInstanceMutex?.Dispose();
            Settings.AppSettings.Save();
            base.OnExit(e);
        }
    }
}