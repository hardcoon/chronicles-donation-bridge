using System.Windows;
using System.Windows.Threading;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.App.ViewModels;
using ChroniclesDonationBridge.Persistence;

namespace ChroniclesDonationBridge.App;

public partial class App : Application
{
    private BridgeRuntime? _runtime;
    private MainViewModel? _viewModel;
    private TrayIconController? _trayIcon;
    private bool _fatalUiErrorReported;
    private bool _shutdownStarted;
    private SingleInstanceGate? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(await SelfTestRunner.RunAsync());
            return;
        }
        if (e.Args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await SelfTestRunner.RunUiSmokeAsync());
            return;
        }

        DispatcherUnhandledException += HandleDispatcherUnhandledException;
        try
        {
            _singleInstance = SingleInstanceGate.TryAcquire();
            if (_singleInstance is null)
            {
                MessageBox.Show("Chronicles Donation Bridge уже запущен.\n\nПроверьте значок программы в трее рядом с часами.",
                    "Chronicles Donation Bridge — Beta", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(2);
                return;
            }
            _runtime = new BridgeRuntime();
            var viewModel = new MainViewModel(_runtime);
            _viewModel = viewModel;
            await viewModel.InitializeAsync();
            ThemeManager.Apply(_runtime.Settings.UseLightTheme);
            var window = new MainWindow(viewModel, _runtime);
            MainWindow = window;
            _trayIcon = new TrayIconController(
                window,
                viewModel,
                () => _runtime.Settings.MinimizeToTrayOnClose,
                () => ShutdownApplication());
            window.Show();
            if (string.IsNullOrWhiteSpace(_runtime.Settings.GamePath))
            {
                await viewModel.OpenSettingsAsync();
            }
        }
        catch (Exception exception)
        {
            TryWriteFatalLog("Startup", exception);
            MessageBox.Show(exception.Message, "Chronicles Donation Bridge — Beta", MessageBoxButton.OK, MessageBoxImage.Error);
            ShutdownApplication(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= HandleDispatcherUnhandledException;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _viewModel?.Dispose();
        _viewModel = null;
        if (_runtime is not null)
        {
            var runtime = _runtime;
            _runtime = null;
            try
            {
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                TryWriteFatalLog("Shutdown", exception);
            }
        }
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private void HandleDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryWriteFatalLog("Unhandled UI exception", e.Exception);

        // The UI may be in an unknown state, so report the failure and shut down
        // deliberately instead of silently disappearing or attempting to continue.
        // UI smoke tests deliberately use the default dispatcher behavior so binding
        // regressions still produce a failing process exit code.
        e.Handled = true;
        if (!_fatalUiErrorReported)
        {
            _fatalUiErrorReported = true;
            var logPath = AppPaths.CreateDefault().LogFile;
            try
            {
                MessageBox.Show(
                    $"Приложение остановлено из-за внутренней ошибки. Подробности записаны в журнал:\n{logPath}",
                    "Chronicles Donation Bridge — Beta",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Nothing else should obscure the original exception during shutdown.
            }
        }
        ShutdownApplication(1);
    }

    private void ShutdownApplication(int exitCode = 0)
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _trayIcon?.PrepareForShutdown();
        _ = CompleteShutdownAsync(exitCode);
    }

    private async Task CompleteShutdownAsync(int exitCode)
    {
        try
        {
            _viewModel?.Dispose();
            _viewModel = null;
            if (_runtime is not null)
            {
                var runtime = _runtime;
                _runtime = null;
                await runtime.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            TryWriteFatalLog("Shutdown", exception);
            exitCode = exitCode == 0 ? 1 : exitCode;
        }
        Shutdown(exitCode);
    }

    internal static bool TryWriteFatalLog(string context, Exception exception, AppPaths? paths = null)
    {
        try
        {
            var logger = new SafeFileLogger(paths ?? AppPaths.CreateDefault());
            logger.WriteAsync("FATAL", $"{context}: {exception}").GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            // Logging must never replace the original startup/UI failure.
            return false;
        }
    }
}
