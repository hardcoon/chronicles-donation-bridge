using System.Windows;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.App;

public partial class RenameUserPresetWindow : Window
{
    public RenameUserPresetWindow(string currentName)
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string ResultName { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validation.TryNormalizeUserPresetName(NameBox.Text, out var normalizedName))
        {
            ErrorText.Text =
                $"Введите от 1 до {Validation.UserPresetNameMaxLength} символов без переносов строк.";
            NameBox.Focus();
            return;
        }

        ResultName = normalizedName;
        DialogResult = true;
    }
}
