using System.ComponentModel;
using System.Windows;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.App.ViewModels;

namespace ChroniclesDonationBridge.App;

public partial class ActionEditorWindow : Window
{
    private readonly ActionEditorViewModel _viewModel;
    public ActionEditorWindow(ActionEditorViewModel viewModel, DataTemplate editorTemplate)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        EditorContent.ContentTemplate = editorTemplate;
        DarkSystemFrame.Attach(this);
        // Fit the work area (including taskbar) on smaller / scaled displays.
        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height);
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await _viewModel.SaveAsync()) DialogResult = true;
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsSaving) e.Cancel = true;
        base.OnClosing(e);
    }
}
