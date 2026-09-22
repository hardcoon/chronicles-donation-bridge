using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChroniclesDonationBridge.App;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.App.ViewModels;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.Persistence;

namespace ChroniclesDonationBridge.UiTests;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var output = Path.GetFullPath(args.Single());
            Directory.CreateDirectory(output);
            // Intentionally NOT App.Run / App.OnStartup: no pipe, OAuth, tray or visible windows.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var resourcesSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.resources-source.xaml"));
            var begin = resourcesSource.IndexOf("<Application.Resources>", StringComparison.Ordinal) + "<Application.Resources>".Length;
            var end = resourcesSource.IndexOf("</Application.Resources>", StringComparison.Ordinal);
            app.Resources = (ResourceDictionary)XamlReader.Parse("<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:local='clr-namespace:ChroniclesDonationBridge.App;assembly=ChroniclesDonationBridge'>" + resourcesSource[begin..end] + "</ResourceDictionary>");
            var errors = new StringWriter();
            var listener = new TextWriterTraceListener(errors);
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var runtime = new BridgeRuntime(new AppPaths(Path.Combine(output, "isolated-data")));
            var vm = new MainViewModel(runtime);
            vm.NextEffectStatus = "Следующий эффект: «Случайный телепорт» · через 00:05";
            SetBrowserStateForTest(vm.PresetBrowser, categoryId: "player_state");
            SeedViewModels();
            var window = new MainWindow(vm, runtime);
            ThemeManager.Apply(false);
            Render(window, 1328, 810, Path.Combine(output, "main-dark-1380.png"));
            Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "Main window never created a native window");
            var panels = Descendants<ActionCardPanel>((DependencyObject)window.Content).ToList();
            Check(panels.Count > 0 && panels[0].InternalColumnCountForTest() == 3, "Automatic grid uses three columns at wide width");
            Check(Descendants<Button>((DependencyObject)window.Content).Count(b => b.Content?.ToString() == "Настроить") == 2, "Both active cards expose Configure");
            Check(Descendants<Expander>((DependencyObject)window.Content).Count() == 0, "No inline expanders remain");
            Check(Descendants<Button>((DependencyObject)window.Content).Any(button =>
                    button.Name == "ThemeToggleButton" && button.Content?.ToString() == "Светлая тема") &&
                  Descendants<CheckBox>((DependencyObject)window.Content).All(checkBox =>
                    checkBox.Name != "ThemeToggleButton"),
                "Theme selection is exposed as a button instead of a checkbox");
            var tabs = Descendants<TabControl>((DependencyObject)window.Content).Single();
            Check(tabs.Items.Count == 4, "Active / User presets / Presets / History remain");
            Check(tabs.Items.Cast<TabItem>().Select(TabHeader).SequenceEqual(
                ["Активные", "Пользовательские пресеты", "Пресеты", "История"]),
                "User presets appear before built-in presets");
            Check(Descendants<Border>((DependencyObject)window.Content).Any(border =>
                    System.Windows.Automation.AutomationProperties.GetName(border) == "Таймер следующего эффекта" &&
                    border.Visibility == Visibility.Visible),
                "Live next-effect countdown is visible in the main header");
            SetBrowserStateForTest(vm.ActiveBrowser, automaticColumns: false, columns: 4);
            SeedViewModels();
            Render(window, 908, 700, Path.Combine(output, "main-dark-960-four-columns.png"));
            CheckPricesVisible(window);
            tabs.SelectedIndex = 1;
            Render(window, 1068, 710, Path.Combine(output, "user-presets-rename.png"));
            Check(Descendants<Button>((DependencyObject)window.Content).Count(button =>
                button.Content?.ToString() == "✎") == 1,
                "User preset exposes one pencil rename button");
            Check(Descendants<Button>((DependencyObject)window.Content).Count(button =>
                    button.Content?.ToString() == "В активные" && IsEffectivelyVisible(button)) == 1 &&
                  Descendants<Button>((DependencyObject)window.Content).All(button =>
                    button.Content?.ToString() != "+ Сумма" || !IsEffectivelyVisible(button)),
                "User presets can be activated but never carry donation prices");
            tabs.SelectedIndex = 2;
            Render(window, 1328, 810, Path.Combine(output, "presets-player-state-dark.png"));
            Check(Descendants<Button>((DependencyObject)window.Content).All(button =>
                    button.Content?.ToString() is not "В активные" and not "+ Сумма" || !IsEffectivelyVisible(button)) &&
                  Descendants<Button>((DependencyObject)window.Content).Count(button =>
                    button.Content?.ToString() == "Сохранить предустановку" && IsEffectivelyVisible(button)) == 7,
                "Built-in presets are price-free templates saved through the single target action");
            ThemeManager.Apply(true);
            Render(window, 1068, 710, Path.Combine(output, "presets-player-state-light.png"));
            foreach (var brushName in new[] { "BackgroundBrush", "AccentBrush", "TriggerChipBrush" })
            {
                var color = ((SolidColorBrush)app.FindResource(brushName)).Color;
                Check(color.B > color.G && color.G > color.R, "Cool slate palette: " + brushName);
            }
            ThemeManager.Apply(false);
            tabs.SelectedIndex = 3;
            Render(window, 1328, 810, Path.Combine(output, "history-clear-queue.png"));
            Check(Descendants<Button>((DependencyObject)window.Content).Any(button =>
                button.Content?.ToString() == "Очистить очередь"),
                "History exposes the clear queue button");
            var renameWindow = new RenameUserPresetWindow("Моя стая");
            Render(renameWindow, 500, 260, Path.Combine(output, "rename-user-preset.png"));
            Check(new WindowInteropHelper(renameWindow).Handle == IntPtr.Zero &&
                Descendants<TextBox>((DependencyObject)renameWindow.Content).Single().Text == "Моя стая",
                "Rename popup renders without showing a native window");
            renameWindow.Close();
            var template = (DataTemplate)window.FindResource("ActionEditorTemplate");
            foreach (var id in new[] { "spawn_items", "spawn_mutants", "spawn_npcs", "god_mode", "random_all_presets", "spawn_vehicle", "explode_nearest_vehicle" })
            {
                var preset = vm.Presets.SingleOrDefault(p => p.HandlerId == id)
                    ?? throw new InvalidOperationException($"Preset missing from UI test catalog: {id}; present: {string.Join(", ", vm.Presets.Select(item => item.HandlerId))}");
                var draft = new ActionEditorViewModel(preset, _ => Task.CompletedTask);
                var editor = new ActionEditorWindow(draft, template);
                Render(editor, 780, 670, Path.Combine(output, "editor-" + id + ".png"));
                Check(new WindowInteropHelper(editor).Handle == IntPtr.Zero && Descendants<TextBox>((DependencyObject)editor.Content).Any(), "Popup editor renders without showing: " + id);
                if (id == "spawn_items")
                {
                    Check(Descendants<Button>((DependencyObject)editor.Content).Any(button =>
                        button.Content?.ToString() == "+ Добавить предмет"), "Item editor keeps the permanent add-row button");
                    draft.Draft.AddItemSpawnCommand.Execute(null);
                    Render(editor, 780, 670, Path.Combine(output, "editor-spawn_items-two-rows.png"));
                    Check(draft.Draft.ItemSpawnRows.Count == 2, "Item editor adds another row inside the same preset");
                }
                if (id is "spawn_mutants" or "spawn_npcs")
                {
                    Check(Descendants<Button>((DependencyObject)editor.Content).Any(button =>
                        button.Content?.ToString() == "+ Добавить группу"), id + " editor keeps the permanent add-row button");
                    draft.Draft.AddSpawnGroupCommand.Execute(null);
                    Render(editor, 780, 670, Path.Combine(output, "editor-" + id + "-two-rows.png"));
                    Check(draft.Draft.SpawnGroupRows.Count == 2, id + " editor adds another row inside the same preset");
                }
                if (id == "spawn_vehicle")
                {
                    var boxes = Descendants<ComboBox>((DependencyObject)editor.Content).Where(box => box.Items.Count == 30).ToList();
                    Check(boxes.Count == 1, "Vehicle editor lists all 30 F1 vehicles");
                    Check(Descendants<TextBlock>((DependencyObject)editor.Content).Any(text => text.Text == "Бензин, литры (1–10)"), "Vehicle fuel label shows actual litres");
                    draft.Draft.Parameters.Single(parameter => parameter.Definition.Key == "vehicle").Value = "veh_kamaz";
                    Render(editor, 780, 670, Path.Combine(output, "editor-spawn_vehicle-legacy.png"));
                    Check(boxes[0].SelectedItem?.ToString() == "veh_kamaz" && new WindowInteropHelper(editor).Handle == IntPtr.Zero,
                        "Legacy vehicle can be selected without opening a native window");
                }
                editor.Close();
            }

            void SeedViewModels()
            {
                vm.ActiveActions.Clear();
                vm.Presets.Clear();
                vm.UserPresets.Clear();
                foreach (var preset in DefaultActionCatalog.CreatePresets())
                    vm.Presets.Add(new ActionViewModel(preset, true));
                var first = vm.Presets.Single(p => p.HandlerId == "spawn_random_mutant_pack").Model.CloneAsNewInstance();
                first.Triggers = [new() { Amount = 100, Currency = "RUB" }, new() { Amount = 1.25m, Currency = "USD" }, new() { Amount = 1m, Currency = "EUR" }, new() { Amount = 4m, Currency = "BYN" }, new() { Amount = 500m, Currency = "KZT" }, new() { Amount = 50m, Currency = "UAH" }, new() { Amount = 6.25m, Currency = "BRL" }, new() { Amount = 40m, Currency = "TRY" }];
                vm.ActiveActions.Add(new ActionViewModel(first) { DisplayOrdinal = 1 });
                vm.ActiveActions.Add(new ActionViewModel(vm.Presets.Single(p => p.HandlerId == "spawn_random_hostile_squad").Model.CloneAsNewInstance()) { DisplayOrdinal = 2 });
                var userPreset = vm.Presets.Single(p => p.HandlerId == "spawn_mutants").Model.CloneAsNewInstance();
                userPreset.DisplayName = "Моя стая";
                userPreset.IsActive = false;
                userPreset.Triggers.Clear();
                vm.UserPresets.Add(new ActionViewModel(userPreset, isPreset: true, isUserPreset: true));
            }
            var priceModel = new TriggerEditorViewModel(null, ["RUB"], new FixedRates()) { AmountText = "100" };
            priceModel.CalculateAllAsync().GetAwaiter().GetResult();
            var price = new TriggerEditorWindow(priceModel);
            Render(price, 520, 740, Path.Combine(output, "prices-preview-dark.png"));
            var saveButton = Descendants<Button>((DependencyObject)price.Content).Single(b => b.Content?.ToString() == "Заменить цены (8)");
            var saveText = Descendants<TextBlock>(saveButton).FirstOrDefault();
            Check(saveText is not null && saveText.Foreground is SolidColorBrush foreground && foreground.Color == ((SolidColorBrush)app.FindResource("AccentTextBrush")).Color,
                "Primary button label uses contrasting foreground");
            Check(priceModel.CalculatedPrices.Count == 8 && new WindowInteropHelper(price).Handle == IntPtr.Zero, "Currency preview renders all eight prices without networking");
            price.Close();
            ThemeManager.Apply(true);
            var settings = new SettingsWindow(new SettingsViewModel(runtime));
            Render(settings, 800, 720, Path.Combine(output, "settings-light.png"));
            Check(new WindowInteropHelper(settings).Handle == IntPtr.Zero, "Settings share new theme without opening a window");
            var installTabButtons = Descendants<Button>((DependencyObject)settings.Content)
                .Select(button => button.Content?.ToString())
                .Where(content => content is not null)
                .ToList();
            Check(installTabButtons.Count(content => content == "Установить / обновить всё") == 1 &&
                  !installTabButtons.Contains("Проверить заново"),
                "Settings expose one-click add-on and template installation");
            var settingsTabs = Descendants<TabControl>((DependencyObject)settings.Content).Single();
            settingsTabs.SelectedIndex = settingsTabs.Items.Cast<TabItem>().ToList().FindIndex(item =>
                string.Equals(item.Header?.ToString(), "Общие", StringComparison.Ordinal));
            Render(settings, 800, 720, Path.Combine(output, "settings-general-light.png"));
            var settingsButtons = Descendants<Button>((DependencyObject)settings.Content)
                .Select(button => button.Content?.ToString())
                .Where(content => content is not null)
                .ToList();
            var settingsText = string.Join(' ', Descendants<TextBlock>((DependencyObject)settings.Content)
                .Select(text => text.Text));
            Check(settingsButtons.Count(content => content == "Открыть папку данных") == 1 &&
                  !settingsText.Contains("MO2", StringComparison.OrdinalIgnoreCase),
                "Settings expose one non-duplicated data-directory action without legacy MO2 controls");
            var contextMenuStyle = (Style)app.FindResource(typeof(ContextMenu));
            Check(contextMenuStyle.Setters.OfType<Setter>().Any(setter =>
                    setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate),
                "Context menus use an explicit dark template without the native white icon gutter");
            settings.Close();
            var errorText = errors.ToString();
            File.WriteAllText(Path.Combine(output, "binding-errors.txt"), errorText);
            Check(string.IsNullOrWhiteSpace(errorText), "No WPF binding errors: " + errorText);
            window.Close();
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"PASS {_checks} headless UI checks. No window was shown; no app/game was started.\nRenders: {output}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Render(Window window, int width, int height, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.Width = width;
        root.Height = height;
        root.DataContext = window.DataContext;
        var outerWidth = width + root.Margin.Left + root.Margin.Right;
        var outerHeight = height + root.Margin.Top + root.Margin.Bottom;
        root.Measure(new Size(outerWidth, outerHeight));
        root.Arrange(new Rect(0, 0, outerWidth, outerHeight));
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(outerWidth), (int)Math.Ceiling(outerHeight), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen()) context.DrawRectangle(window.Background, null, new Rect(0, 0, outerWidth, outerHeight));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static void CheckPricesVisible(MainWindow window)
    {
        var buttons = Descendants<Button>((DependencyObject)window.Content).Where(b => b.DataContext is TriggerRuleViewModel && b.Content is string text && text.Contains("RUB") || b.DataContext is TriggerRuleViewModel && b.Content is string label && label.Contains("USD")).ToList();
        var sizes = string.Join(", ", buttons.Select(button => $"{button.ActualWidth:0.#}×{button.ActualHeight:0.#}"));
        Check(buttons.Count == 2 && buttons.All(b => b.ActualWidth > 60 && b.ActualHeight >= 30),
            $"Prices retain usable hit targets at four-column narrow density (count={buttons.Count}; sizes={sizes})");
    }
    private static string? TabHeader(TabItem item) => item.Header is StackPanel panel
        ? panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text
        : item.Header?.ToString();
    private static void SetBrowserStateForTest(
        ActionBrowserViewModel browser,
        string? categoryId = null,
        bool? automaticColumns = null,
        int? columns = null)
    {
        var type = typeof(ActionBrowserViewModel);
        var changed = new List<string>();
        if (categoryId is not null)
        {
            var category = ActionBrowserViewModel.Categories.Single(item => item.Id == categoryId);
            type.GetField("_selectedCategory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(browser, category);
            changed.Add(nameof(ActionBrowserViewModel.SelectedCategory));
            browser.VisibleActions.Refresh();
        }
        if (automaticColumns is not null)
        {
            type.GetField("_automaticColumns", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(browser, automaticColumns.Value);
            changed.Add(nameof(ActionBrowserViewModel.AutomaticColumns));
        }
        if (columns is not null)
        {
            type.GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(browser, columns.Value);
            changed.Add(nameof(ActionBrowserViewModel.Columns));
        }
        if (automaticColumns is not null || columns is not null)
        {
            changed.Add(nameof(ActionBrowserViewModel.EffectiveColumns));
            changed.Add(nameof(ActionBrowserViewModel.GridLabel));
        }
        var notify = typeof(ObservableObject).GetMethod("RaisePropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var propertyName in changed) notify.Invoke(browser, [propertyName]);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static bool IsEffectivelyVisible(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement uiElement && uiElement.Visibility != Visibility.Visible) return false;
        }
        return true;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
    private static int InternalColumnCountForTest(this ActionCardPanel panel)
    {
        var method = typeof(ActionCardPanel).GetMethod("ColumnCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (int)method.Invoke(panel, [panel.ActualWidth])!;
    }
    private sealed class FixedRates : IExchangeRateProvider
    {
        public Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ExchangeRateSnapshot(new DateOnly(2026, 9, 5), new Dictionary<string, decimal> { ["RUB"] = 1, ["USD"] = 80, ["EUR"] = 100, ["BYN"] = 25, ["KZT"] = .2m, ["UAH"] = 2, ["BRL"] = 16, ["TRY"] = 2.5m }, "Тестовый курс"));
    }
}
