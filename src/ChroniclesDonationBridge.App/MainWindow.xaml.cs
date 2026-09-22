using System.Windows;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.App.ViewModels;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly BridgeRuntime _runtime;

    public MainWindow(MainViewModel viewModel, BridgeRuntime runtime)
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        _viewModel = viewModel;
        _runtime = runtime;
        DataContext = viewModel;
        viewModel.ShowTriggerEditor = ShowTriggerEditor;
        viewModel.ShowRenameUserPreset = ShowRenameUserPreset;
        viewModel.ShowSettings = ShowSettingsAsync;
    }

    private TriggerEditorResult? ShowTriggerEditor(ActionViewModel action, TriggerRuleViewModel? existing)
    {
        var viewModel = new TriggerEditorViewModel(existing?.Model, _runtime.Settings.ObservedCurrencies);
        var dialog = new TriggerEditorWindow(viewModel) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private string? ShowRenameUserPreset(ActionViewModel preset)
    {
        var dialog = new RenameUserPresetWindow(preset.DisplayName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.ResultName : null;
    }

    private void ConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ActionViewModel action }) return;
        var editor = new ActionEditorViewModel(action, draft => _viewModel.SaveEditorDraftAsync(action, draft));
        new ActionEditorWindow(editor, (DataTemplate)FindResource("ActionEditorTemplate")) { Owner = this }.ShowDialog();
    }

    private void CardMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await _viewModel.OpenSettingsAsync();

    private async void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox toggle) return;
        toggle.IsEnabled = false;
        try
        {
            await _viewModel.SetLightThemeAsync(toggle.IsChecked == true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Не удалось сменить тему",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    private async Task ShowSettingsAsync()
    {
        var dialog = new SettingsWindow(new SettingsViewModel(_runtime)) { Owner = this };
        dialog.ShowDialog();
        await Task.CompletedTask;
    }
}
