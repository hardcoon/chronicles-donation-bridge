using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.App.ViewModels;
using ChroniclesDonationBridge.DonationAlerts;

namespace ChroniclesDonationBridge.App;

public partial class SettingsWindow : Window
{
    private const string SupportCardNumber = "2200396126139183";
    private readonly SettingsViewModel _viewModel;
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await RunAsync(_viewModel.RefreshInstallationStatusAsync, showDialog: false);
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку игры", Multiselect = false };
        if (Directory.Exists(_viewModel.GamePath)) dialog.InitialDirectory = _viewModel.GamePath;
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.GamePath = dialog.FolderName;
            _ = RunAsync(_viewModel.RefreshInstallationStatusAsync, showDialog: false);
        }
    }

    private void BrowseEditor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите редактор", Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) _viewModel.EditorPath = dialog.FileName;
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_viewModel.DataDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _viewModel.DataDirectory,
            UseShellExecute = true
        });
    }

    private async void InstallPackage_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(_viewModel.InstallBridgeAsync);

    private void RepositoryLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void CopySupportCard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SupportCardNumber);
            _viewModel.Status = "Номер карты Т-Банка скопирован.";
        }
        catch
        {
            _viewModel.Status = "Не удалось скопировать номер. Выделите и скопируйте его вручную.";
        }
    }

    private void OpenApplications_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DonationAlertsEndpoints.Applications) { UseShellExecute = true });

    private void OpenApiDocumentation_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DonationAlertsEndpoints.ApiDocumentation) { UseShellExecute = true });

    private async void Connect_Click(object sender, RoutedEventArgs e)
        => await RunAsync(() => _viewModel.ConnectAsync(ClientSecretBox.Password, LoopbackOAuthFlow.OpenSystemBrowser));

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
        => await RunAsync(() => _viewModel.DisconnectAsync(false));

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Удалить токены, Client ID и сохранённый Client Secret?", "Chronicles Donation Bridge — Beta", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await RunAsync(() => _viewModel.DisconnectAsync(true));
            ClientSecretBox.Clear();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await RunAsync(_viewModel.SaveAsync)) DialogResult = true;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Экспорт правил", Filter = "JSON (*.json)|*.json", FileName = "ChroniclesDonationBridge.rules.json" };
        if (dialog.ShowDialog(this) == true) await RunAsync(() => _viewModel.ExportAsync(dialog.FileName));
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Импорт правил", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) await RunAsync(() => _viewModel.ImportAsync(dialog.FileName));
    }

    private async Task<bool> RunAsync(Func<Task> operation, bool showDialog = true)
    {
        try { await operation(); return true; }
        catch (Exception exception)
        {
            _viewModel.Status = exception.Message;
            if (showDialog)
                MessageBox.Show(this, exception.Message, "Chronicles Donation Bridge Beta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
