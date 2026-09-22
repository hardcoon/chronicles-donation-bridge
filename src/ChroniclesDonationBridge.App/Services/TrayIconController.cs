using System.ComponentModel;
using System.Drawing;
using System.Windows;
using ChroniclesDonationBridge.App.ViewModels;
using Forms = System.Windows.Forms;

namespace ChroniclesDonationBridge.App.Services;

/// <summary>
/// Keeps the bridge reachable while its main WPF window is hidden. The tray
/// callbacks are marshalled through the WPF dispatcher so they use the same
/// settings and commands as the visible interface.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly Window _window;
    private readonly MainViewModel _viewModel;
    private readonly Func<bool> _minimizeOnClose;
    private readonly Action _shutdown;
    private readonly Icon _applicationIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _processingItem;
    private bool _exitRequested;
    private bool _disposed;

    public TrayIconController(
        Window window,
        MainViewModel viewModel,
        Func<bool> minimizeOnClose,
        Action shutdown)
    {
        _window = window;
        _viewModel = viewModel;
        _minimizeOnClose = minimizeOnClose;
        _shutdown = shutdown;

        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь запущенной программы.");
        _applicationIcon = Icon.ExtractAssociatedIcon(processPath)
            ?? throw new InvalidOperationException("Не удалось загрузить значок программы.");

        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var openItem = CreateMenuItem("Открыть");
        openItem.Click += (_, _) => Dispatch(OpenWindow);

        _processingItem = CreateMenuItem(string.Empty);
        _processingItem.Click += (_, _) => Dispatch(ToggleProcessing);

        var exitItem = CreateMenuItem("Закрыть программу");
        exitItem.Click += (_, _) => Dispatch(ExitProgram);

        _menu.Items.Add(openItem);
        _menu.Items.Add(_processingItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += MenuOnOpening;
        ApplyMenuTheme();

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _applicationIcon,
            Text = "Chronicles Donation Bridge — Beta",
            Visible = true
        };
        _notifyIcon.DoubleClick += NotifyIconOnDoubleClick;

        _window.Closing += WindowOnClosing;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        ThemeManager.ThemeChanged += ThemeOnChanged;
        UpdateProcessingItem();
    }

    private static Forms.ToolStripMenuItem CreateMenuItem(string text) => new(text);

    /// <summary>Allows Application.Shutdown to close the window instead of hiding it.</summary>
    public void PrepareForShutdown()
    {
        _exitRequested = true;
        _notifyIcon.Visible = false;
    }

    private void WindowOnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested || _disposed) return;

        if (_minimizeOnClose())
        {
            e.Cancel = true;
            _window.Hide();
            return;
        }

        // ShutdownMode is explicit so hiding the window never stops the bridge.
        // When the option is disabled, finish a normal close by explicitly
        // terminating the application after this Closing event completes.
        _exitRequested = true;
        _notifyIcon.Visible = false;
        _window.Dispatcher.BeginInvoke(_shutdown);
    }

    private void NotifyIconOnDoubleClick(object? sender, EventArgs e) => Dispatch(OpenWindow);

    private void MenuOnOpening(object? sender, CancelEventArgs e) => UpdateProcessingItem();

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Armed) or nameof(MainViewModel.CanArm))
        {
            Dispatch(UpdateProcessingItem);
        }
    }

    private void ThemeOnChanged() => Dispatch(ApplyMenuTheme);

    private void ApplyMenuTheme()
    {
        var light = ThemeManager.IsLightTheme;
        var surface = light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(28, 31, 34);
        var text = light ? Color.FromArgb(32, 35, 39) : Color.FromArgb(243, 244, 245);
        var hover = light ? Color.FromArgb(220, 225, 230) : Color.FromArgb(54, 59, 64);
        var border = light ? Color.FromArgb(173, 181, 190) : Color.FromArgb(75, 82, 90);
        var divider = light ? Color.FromArgb(205, 210, 216) : Color.FromArgb(56, 61, 67);

        _menu.BackColor = surface;
        _menu.ForeColor = text;
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(
            new ThemeMenuColorTable(surface, hover, border, divider));
        foreach (Forms.ToolStripItem item in _menu.Items)
        {
            item.BackColor = surface;
            item.ForeColor = text;
        }
    }

    private void OpenWindow()
    {
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ToggleProcessing()
    {
        _viewModel.Armed = !_viewModel.Armed;
        UpdateProcessingItem();
    }

    private void UpdateProcessingItem()
    {
        var armed = _viewModel.Armed;
        _processingItem.Checked = armed;
        _processingItem.Enabled = armed || _viewModel.CanArm;
        _processingItem.Text = armed
            ? "Отключить обработку донатов"
            : _viewModel.CanArm
                ? "Включить обработку донатов"
                : "Обработка донатов: сначала подключите аккаунт";
    }

    private void ExitProgram()
    {
        if (_exitRequested) return;
        PrepareForShutdown();
        _shutdown();
    }

    private void Dispatch(Action action)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        if (_window.Dispatcher.CheckAccess()) action();
        else _window.Dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exitRequested = true;
        _window.Closing -= WindowOnClosing;
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        ThemeManager.ThemeChanged -= ThemeOnChanged;
        _notifyIcon.DoubleClick -= NotifyIconOnDoubleClick;
        _menu.Opening -= MenuOnOpening;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
        _menu.Dispose();
    }

    private sealed class ThemeMenuColorTable : Forms.ProfessionalColorTable
    {
        private readonly Color _surface;
        private readonly Color _hover;
        private readonly Color _border;
        private readonly Color _divider;

        public ThemeMenuColorTable(Color surface, Color hover, Color border, Color divider)
        {
            _surface = surface;
            _hover = hover;
            _border = border;
            _divider = divider;
        }

        public override Color ToolStripDropDownBackground => _surface;
        public override Color ImageMarginGradientBegin => _surface;
        public override Color ImageMarginGradientMiddle => _surface;
        public override Color ImageMarginGradientEnd => _surface;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color MenuItemBorder => _border;
        public override Color SeparatorDark => _divider;
        public override Color SeparatorLight => _divider;
        public override Color CheckBackground => _hover;
        public override Color CheckSelectedBackground => _hover;
        public override Color CheckPressedBackground => _hover;
    }
}
