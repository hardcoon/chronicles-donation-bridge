using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.GameIpc;
using ChroniclesDonationBridge.Persistence;
using ChroniclesDonationBridge.App.ViewModels;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChroniclesDonationBridge.App.Services;

public static class SelfTestRunner
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var actions = DefaultActionCatalog.Create();
            var match = RuleMatcher.Match(new DonationEvent { Id = "self", Amount = 1m, Currency = "RUB", CreatedAt = DateTimeOffset.UtcNow }, actions);
            if (match?.Action.HandlerId != "add_radiation") throw new InvalidOperationException("Rule matcher self-test failed.");

            var command = new GameCommand
            {
                CommandId = "self",
                DonationId = "тест",
                ActionId = "add_radiation",
                AmountMinor = 100,
                Currency = "RUB",
                Parameters = new() { ["percent"] = "25" }
            };
            var fields = PipeProtocol.Command(command).Split('\t');
            if (PipeProtocol.PercentDecode(fields[2]) != "тест") throw new InvalidOperationException("Protocol self-test failed.");

            var responsiveWidth = new ResponsiveItemWidthConverter { MinimumColumnWidth = 750 };
            if (responsiveWidth.CalculateItemWidth(1_499) != 1_499 ||
                responsiveWidth.CalculateItemWidth(1_500) != 750 ||
                responsiveWidth.CalculateItemWidth(1_840) != 920 ||
                responsiveWidth.CalculateItemWidth(3_000) != 1_500 ||
                !double.IsNaN(responsiveWidth.CalculateItemWidth(double.NaN)))
            {
                throw new InvalidOperationException("Responsive preset column self-test failed.");
            }

            var directory = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.SelfTest", Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new AppPaths(directory);
                var store = new JsonSettingsStore(paths);
                await store.SaveAsync(new AppSettings());
                _ = await store.LoadAsync();
                if (OperatingSystem.IsWindows())
                {
                    var secrets = new DpapiSecretStore(paths);
                    await secrets.SaveAsync(new SecretBundle { ClientSecret = "self-test" });
                    if ((await secrets.LoadAsync()).ClientSecret != "self-test") throw new InvalidOperationException("DPAPI self-test failed.");
                }

                const string fatalTestSecret = "fatal-self-test-secret";
                if (!ChroniclesDonationBridge.App.App.TryWriteFatalLog(
                        "Self-test",
                        new InvalidOperationException($"access_token={fatalTestSecret}"),
                        paths))
                {
                    throw new InvalidOperationException("Fatal UI logging self-test failed to write.");
                }
                var fatalLog = await File.ReadAllTextAsync(paths.LogFile);
                if (!fatalLog.Contains("\tFATAL\t", StringComparison.Ordinal) ||
                    !fatalLog.Contains("access_token=[REDACTED]", StringComparison.Ordinal) ||
                    fatalLog.Contains(fatalTestSecret, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Fatal UI logging self-test failed to redact sensitive data.");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            return 0;
        }
        catch { return 1; }
    }

    public static async Task<int> RunUiSmokeAsync()
    {
        MainWindow? mainWindow = null;
        SettingsWindow? settingsWindow = null;
        TriggerEditorWindow? triggerEditorWindow = null;
        string? directory = null;
        try
        {
            directory = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.UiSmoke", Guid.NewGuid().ToString("N"));
            try
            {
                ValidateItemCatalogAllowlist(directory);
                var paths = new AppPaths(directory);
                var presetLoader = new FilePresetLoader();
                var presetRoot = Directory.GetParent(presetLoader.SourceScriptsDirectory)?.FullName;
                if (string.IsNullOrWhiteSpace(presetRoot) ||
                    !Directory.Exists(presetLoader.SourceScriptsDirectory))
                {
                    throw new InvalidOperationException("The UI smoke test could not locate the packaged preset scripts.");
                }
                await new JsonSettingsStore(paths).SaveAsync(new AppSettings { GamePath = presetRoot });
                await using var runtime = new BridgeRuntime(paths);
                var viewModel = new MainViewModel(runtime);
                await viewModel.InitializeAsync();
                ThemeManager.Apply(runtime.Settings.UseLightTheme);
                mainWindow = new MainWindow(viewModel, runtime)
                {
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowState = WindowState.Minimized,
                    Opacity = 0
                };
                mainWindow.Show();
                ValidateNativeSystemFrame(mainWindow);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (viewModel.CanArm)
                {
                    throw new InvalidOperationException("Donation processing must stay disabled before OAuth is configured.");
                }
                var expectedPresetCount = DefaultActionCatalog.CreatePresets().Count;
                if (viewModel.Presets.Count != expectedPresetCount ||
                    viewModel.Presets.Select(item => item.HandlerId).Distinct(StringComparer.Ordinal).Count() != expectedPresetCount ||
                    viewModel.Presets.Any(item => item.HandlerId == "spawn_dogs_3"))
                {
                    throw new InvalidOperationException("Reusable preset catalog is not unique.");
                }
                ValidateMainTabs(mainWindow);

                var knownIds = runtime.Settings.Actions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                var mutantPreset = viewModel.Presets.Single(item => item.HandlerId == "spawn_mutants");
                var itemPreset = viewModel.Presets.Single(item => item.HandlerId == "spawn_items");
                if (!mutantPreset.IsSpawnGroupBundle || mutantPreset.SpawnGroupRows.Count != 1 ||
                    mutantPreset.Parameters.Count != 0 || itemPreset.CompactParameterColumns != 2)
                {
                    throw new InvalidOperationException(
                        "Row-based action editors must expose the item and NPC/mutant bundle layouts.");
                }
                var dogsDraft = mutantPreset.Model;
                dogsDraft.Triggers.Add(new TriggerRule { Amount = 40m, Currency = "RUB" });
                await runtime.AddActionInstanceAsync(dogsDraft);
                var firstAdded = runtime.Settings.Actions.Single(item => !knownIds.Contains(item.Id));
                knownIds.Add(firstAdded.Id);

                var chimeraDraft = viewModel.Presets.Single(item => item.HandlerId == "spawn_mutants").Model;
                chimeraDraft.Parameters["species"] = "chimera";
                chimeraDraft.Parameters["strength"] = "medium";
                chimeraDraft.Parameters["count"] = "1";
                chimeraDraft.SpawnGroups =
                [
                    new SpawnGroupEntry { VariantId = "chimera", Strength = "medium", Count = 1 }
                ];
                chimeraDraft.Triggers.Clear();
                chimeraDraft.Triggers.Add(new TriggerRule { Amount = 41m, Currency = "RUB" });
                await runtime.AddActionInstanceAsync(chimeraDraft);
                var secondAdded = runtime.Settings.Actions.Single(item => !knownIds.Contains(item.Id));
                if (firstAdded.Id == secondAdded.Id || firstAdded.HandlerId != "spawn_mutants" ||
                    secondAdded.HandlerId != "spawn_mutants" ||
                    firstAdded.SpawnGroups.Single().VariantId != "dog" ||
                    secondAdded.SpawnGroups.Single().VariantId != "chimera" ||
                    viewModel.Presets.Count != expectedPresetCount)
                {
                    throw new InvalidOperationException("Preset instances are not independent.");
                }

                var customDraft = DefaultActionCatalog.CreatePresets()
                    .Single(preset => preset.HandlerId == "spawn_mutants");
                customDraft.Triggers.Add(new TriggerRule { Amount = 999m, Currency = "RUB" });
                await runtime.AddUserPresetAsync(customDraft);
                var customPreset = runtime.Settings.UserPresets.Single();
                if (customPreset.Triggers.Count != 0)
                {
                    throw new InvalidOperationException("A user preset retained a donation price instead of remaining a template.");
                }
                await runtime.RenameUserPresetAsync(customPreset.Id, "Стая тушканов");
                var customPresetId = customPreset.Id;
                var namedActiveDraft = customPreset.Clone();
                namedActiveDraft.Triggers.Add(new TriggerRule { Amount = 17m, Currency = "RUB" });
                await runtime.AddActionInstanceAsync(namedActiveDraft);
                var namedActive = runtime.Settings.Actions.Single(item =>
                    item.HasCustomDisplayName && item.DisplayName == "Стая тушканов");
                var namedActiveId = namedActive.Id;

                var rulesFile = Path.Combine(directory, "preset-roundtrip.json");
                await runtime.ExportRulesAsync(rulesFile);
                await runtime.DeleteActionInstanceAsync(firstAdded.Id);
                await runtime.DeleteActionInstanceAsync(secondAdded.Id);
                await runtime.DeleteActionInstanceAsync(namedActiveId);
                await runtime.DeleteUserPresetAsync(customPresetId);
                await runtime.ImportRulesAsync(rulesFile);
                firstAdded = runtime.Settings.Actions.Single(item => item.Id == firstAdded.Id);
                secondAdded = runtime.Settings.Actions.Single(item => item.Id == secondAdded.Id);
                if (firstAdded.SpawnGroups.Single().VariantId != "dog" ||
                    secondAdded.SpawnGroups.Single().VariantId != "chimera")
                {
                    throw new InvalidOperationException("Preset export/import roundtrip lost instance parameters.");
                }
                customPreset = runtime.Settings.UserPresets.Single(item => item.Id == customPresetId);
                if (customPreset.DisplayName != "Стая тушканов" || customPreset.Triggers.Count != 0)
                {
                    throw new InvalidOperationException(
                        "Preset export/import roundtrip lost a custom name or added a price.");
                }
                namedActive = runtime.Settings.Actions.Single(item => item.Id == namedActiveId);
                if (namedActive.DisplayName != "Стая тушканов" || !namedActive.HasCustomDisplayName)
                {
                    throw new InvalidOperationException(
                        "A user preset title was lost when moved to active or exported.");
                }
                await runtime.DeleteActionInstanceAsync(firstAdded.Id);
                await runtime.DeleteActionInstanceAsync(secondAdded.Id);
                await runtime.DeleteActionInstanceAsync(namedActiveId);
                await runtime.DeleteUserPresetAsync(customPresetId);

                settingsWindow = new SettingsWindow(new SettingsViewModel(runtime))
                {
                    Owner = mainWindow,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32_000,
                    Top = -32_000,
                    Opacity = 0
                };
                settingsWindow.Show();
                settingsWindow.UpdateLayout();
                ValidateNativeSystemFrame(settingsWindow);
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                await Dispatcher.Yield(DispatcherPriority.Render);
                ValidateWritableTargetBindings(settingsWindow);

                triggerEditorWindow = new TriggerEditorWindow(
                    new TriggerEditorViewModel(null, runtime.Settings.ObservedCurrencies))
                {
                    Owner = mainWindow,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32_000,
                    Top = -32_000,
                    Opacity = 0
                };
                triggerEditorWindow.Show();
                triggerEditorWindow.UpdateLayout();
                ValidateNativeSystemFrame(triggerEditorWindow);
                await Dispatcher.Yield(DispatcherPriority.Render);
                ValidateWritableTargetBindings(triggerEditorWindow);
                ValidateThemedControlTemplates(expectLight: false);
                ValidateThemePalette(mainWindow, expectLight: false);

                await viewModel.SetLightThemeAsync(true);
                mainWindow.UpdateLayout();
                settingsWindow.UpdateLayout();
                triggerEditorWindow.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                ValidateThemedControlTemplates(expectLight: true);
                ValidateThemePalette(mainWindow, expectLight: true);

                await viewModel.SetLightThemeAsync(false);
                mainWindow.UpdateLayout();
                settingsWindow.UpdateLayout();
                triggerEditorWindow.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                ValidateThemedControlTemplates(expectLight: false);
                ValidateThemePalette(mainWindow, expectLight: false);

                // Give the app-first named-pipe wait loop time to remain idle. Waiting
                // for the game is a normal state and must not terminate the process.
                await Task.Delay(250);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            finally
            {
                triggerEditorWindow?.Close();
                settingsWindow?.Close();
                mainWindow?.Close();
                if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            return 0;
        }
        catch (Exception exception)
        {
            var diagnosticPath = Environment.GetEnvironmentVariable("CHRONICLES_BRIDGE_UI_SMOKE_ERROR_FILE");
            if (!string.IsNullOrWhiteSpace(diagnosticPath))
            {
                try { File.WriteAllText(diagnosticPath, exception.ToString()); }
                catch { }
            }
            return 1;
        }
    }

    private static void ValidateWritableTargetBindings(DependencyObject root)
    {
        foreach (var target in EnumerateVisualTree(root))
        {
            if (target is TextBox textBox)
            {
                ValidateWritableTargetBinding(textBox, TextBox.TextProperty);
            }
            if (target is Selector selector)
            {
                ValidateWritableTargetBinding(selector, Selector.SelectedItemProperty);
                ValidateWritableTargetBinding(selector, Selector.SelectedValueProperty);
            }
            if (target is ToggleButton toggleButton)
            {
                ValidateWritableTargetBinding(toggleButton, ToggleButton.IsCheckedProperty);
            }
        }
    }

    private static void ValidateNativeSystemFrame(Window window)
    {
        if (window.WindowStyle == WindowStyle.None)
        {
            throw new InvalidOperationException($"{window.GetType().Name} must retain the native Windows frame.");
        }
        if (OperatingSystem.IsWindows() && new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{window.GetType().Name} did not create a native window handle.");
        }
    }

    private static void ValidateThemedControlTemplates(bool expectLight)
    {
        var scrollBarStyle = Application.Current.TryFindResource(typeof(ScrollBar)) as Style
            ?? throw new InvalidOperationException("The application ScrollBar style is missing.");
        foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
        {
            var scrollBar = new ScrollBar
            {
                Style = scrollBarStyle,
                Orientation = orientation,
                Minimum = 2,
                Maximum = 100,
                Value = 30,
                ViewportSize = 12
            };
            scrollBar.ApplyTemplate();
            var track = scrollBar.Template.FindName("PART_Track", scrollBar) as Track
                ?? throw new InvalidOperationException($"The {orientation} ScrollBar template has no PART_Track.");
            if (track.Orientation != orientation || track.Minimum != 2 || track.Maximum != 100 ||
                track.Value != 30 || track.ViewportSize != 12)
            {
                throw new InvalidOperationException($"The {orientation} ScrollBar track is not bound to its owner.");
            }
            track.Value = 45;
            if (scrollBar.Value != 45)
            {
                throw new InvalidOperationException($"The {orientation} ScrollBar track does not update its owner.");
            }
        }

        var comboBoxStyle = Application.Current.TryFindResource(typeof(ComboBox)) as Style
            ?? throw new InvalidOperationException("The application ComboBox style is missing.");
        var comboBox = new ComboBox
        {
            Style = comboBoxStyle,
            IsEditable = true,
            ItemsSource = new[] { "RUB", "USD" },
            Text = "RUB"
        };
        comboBox.ApplyTemplate();
        if (comboBox.ItemsPanel?.LoadContent() is not VirtualizingStackPanel)
        {
            throw new InvalidOperationException("The ComboBox item list is not virtualized.");
        }
        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is not TextBox editableTextBox ||
            editableTextBox.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("The editable ComboBox text field is unavailable.");
        }
        if (comboBox.Template.FindName("PART_Popup", comboBox) is not Popup ||
            comboBox.Template.FindName("DropDownBorder", comboBox) is not Border dropDownBorder ||
            dropDownBorder.Background is not SolidColorBrush popupBrush ||
            Application.Current.TryFindResource("PanelBrush") is not SolidColorBrush panelBrush ||
            popupBrush.Color != panelBrush.Color)
        {
            throw new InvalidOperationException("The themed ComboBox popup template is unavailable.");
        }
        if (expectLight != ThemeManager.IsLightTheme)
        {
            throw new InvalidOperationException("The requested ComboBox theme was not applied.");
        }
    }

    private static void ValidateThemePalette(MainWindow mainWindow, bool expectLight)
    {
        var background = ThemeColor("BackgroundBrush");
        var text = ThemeColor("TextBrush");
        var even = ThemeColor("ActionRowEvenBrush");
        var odd = ThemeColor("ActionRowOddBrush");
        if (even == odd)
        {
            throw new InvalidOperationException("Action rows must use two distinct alternating colors.");
        }
        if (expectLight)
        {
            if (background.R < 220 || background.G < 220 || background.B < 220 ||
                text.R > 80 || text.G > 80 || text.B > 80)
            {
                throw new InvalidOperationException(
                    $"The light palette does not provide a light surface with dark text: background={background}, text={text}.");
            }
        }
        else if (background.R > 60 || background.G > 60 || background.B > 60 ||
                 text.R < 200 || text.G < 200 || text.B < 200)
        {
            throw new InvalidOperationException("The dark palette does not provide a graphite surface with light text.");
        }

        if (mainWindow.Background is not SolidColorBrush windowBackground || windowBackground.Color != background)
        {
            throw new InvalidOperationException("The open main window did not update to the selected theme.");
        }
        if (mainWindow.FindName("ActiveActionsList") is not ItemsControl active ||
            mainWindow.FindName("PresetsList") is not ItemsControl presets)
        {
            throw new InvalidOperationException("Active actions and presets must expose independent card collections.");
        }

        var tabs = mainWindow.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("The main tab control is missing.");
        tabs.SelectedIndex = 2;
        mainWindow.UpdateLayout();
        presets.UpdateLayout();
        if (presets.ItemContainerGenerator.ContainerFromIndex(0) is not ContentPresenter firstContainer ||
            presets.ItemContainerGenerator.ContainerFromIndex(1) is not ContentPresenter secondContainer ||
            mainWindow.TryFindResource("ActionRowCardStyle") is not Style rowStyle)
        {
            throw new InvalidOperationException("Preset card containers were not generated.");
        }

        var firstRow = EnumerateVisualTree(firstContainer).OfType<Border>()
            .FirstOrDefault(border => ReferenceEquals(border.Style, rowStyle));
        var secondRow = EnumerateVisualTree(secondContainer).OfType<Border>()
            .FirstOrDefault(border => ReferenceEquals(border.Style, rowStyle));
        if (firstRow?.Background is not SolidColorBrush firstBrush || firstBrush.Color != even ||
            secondRow?.Background is not SolidColorBrush secondBrush || secondBrush.Color != even)
        {
            throw new InvalidOperationException("Preset cards do not render the current neutral surface color.");
        }
    }

    private static Color ThemeColor(string resourceKey) =>
        Application.Current.TryFindResource(resourceKey) is SolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"Theme color {resourceKey} is unavailable.");

    private static void ValidateMainTabs(MainWindow mainWindow)
    {
        mainWindow.UpdateLayout();
        var tabs = mainWindow.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("The main tab control is missing.");
        if (tabs.Items.Count != 4)
        {
            throw new InvalidOperationException("The main window must expose four primary tabs.");
        }
        var actualOrder = tabs.Items.Cast<TabItem>()
            .Select(item => item.Header is StackPanel panel
                ? panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text
                : item.Header?.ToString())
            .ToArray();
        var expectedOrder = new[] { "Активные", "Пользовательские пресеты", "Пресеты", "История" };
        if (!actualOrder.SequenceEqual(expectedOrder, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected main tab order: {string.Join(", ", actualOrder)}.");
        }
        for (var index = 0; index < tabs.Items.Count; index++)
        {
            tabs.SelectedIndex = index;
            mainWindow.UpdateLayout();
        }
        if (!EnumerateVisualTree(mainWindow).OfType<DataGrid>().Any())
        {
            throw new InvalidOperationException("The History tab has no event grid.");
        }
    }

    private static void ValidateItemCatalogAllowlist(string temporaryRoot)
    {
        var gamePath = Path.Combine(temporaryRoot, "catalog-game");
        var gameConfigs = Path.Combine(gamePath, "gamedata", "configs");
        var addonConfigs = Path.Combine(gamePath, "ixr_addons", "test", "configs");
        Directory.CreateDirectory(gameConfigs);
        Directory.CreateDirectory(addonConfigs);
        File.WriteAllText(
            Path.Combine(gameConfigs, "mod_system_pf_inventory_groups.ltx"),
            "[inventory_sort_groups]\nweapons = 10\nmedicine = 70\nammo_explosives = 20\nmaterials = 100\nmutant_parts = 110\ncontainers = 120\nmisc = 130\n\n" +
            "[inventory_sort_registry]\nwpn_pm = weapons\nmedkit = medicine\nstory_medkit = medicine\nammo_9x18_fmj = ammo_explosives\n" +
            "ammo_5.45x39_fmj = ammo_explosives\ngrenade_f1 = ammo_explosives\nlz_nm_metal_part = materials\n" +
            "mutant_part_dog_tail = mutant_parts\nitm_backpack = containers\nguitar_a = misc\n\n" +
            "[derived]:base\nnoise_item = medicine\n");
        File.WriteAllText(
            Path.Combine(addonConfigs, "mod_system_pf_donation_item_allowlist.ltx"),
            "[pf_donation_item_allowlist]\nwpn_pm = weapons\nmedkit = medicine\nnoise_item = medicine\nammo_9x18_fmj = ammo_explosives\n" +
            "ammo_5.45x39_fmj = ammo_explosives\ngrenade_f1 = ammo_explosives\nlz_nm_metal_part = materials\n" +
            "mutant_part_dog_tail = mutant_parts\nitm_backpack = containers\nguitar_a = misc\n");
        File.WriteAllText(
            Path.Combine(addonConfigs, "pf_donation_item_labels_ru.ltx"),
            "[pf_donation_item_labels_ru]\nwpn_pm = Пистолет Макарова\nmedkit = Аптечка\nnoise_item = Шумовой предмет\n" +
            "ammo_9x18_fmj = Патроны 9х18\nammo_5.45x39_fmj = Патроны 5,45х39\ngrenade_f1 = Граната Ф-1\n" +
            "lz_nm_metal_part = Металл\nmutant_part_dog_tail = Хвост собаки\nitm_backpack = Рюкзак\nguitar_a = Гитара\n");

        var parameter = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "spawn_items")
            .ParameterDefinitions.Single(definition => definition.Key == "item");
        var warnings = new List<string>();
        if (!new InventoryCatalogLoader().ApplyTo(gamePath, parameter, warnings) ||
            !parameter.OptionGroups.TryGetValue("medicine", out var medicine) || medicine.Count != 1 || medicine[0] != "medkit" ||
            parameter.Options.Contains("story_medkit", StringComparer.OrdinalIgnoreCase) ||
            parameter.OptionLabels.GetValueOrDefault("medkit") != "Аптечка")
        {
            throw new InvalidOperationException(
                $"The item catalog did not enforce its donation allowlist: {string.Join(" | ", warnings)}");
        }

        if (!parameter.OptionGroups.TryGetValue("weapons", out var weapons) ||
            weapons.Count != 2 ||
            weapons[0] != InventoryCatalogLoader.RandomWeaponSentinel ||
            weapons[1] != "wpn_pm" ||
            parameter.OptionLabels.GetValueOrDefault(InventoryCatalogLoader.RandomWeaponSentinel) !=
                InventoryCatalogLoader.RandomWeaponLabel ||
            parameter.Validate(InventoryCatalogLoader.RandomWeaponSentinel) is not null)
        {
            throw new InvalidOperationException(
                "The random-weapon choice is missing, misplaced, unlabelled or rejected by item validation.");
        }

        var hiddenGroups = new[] { "materials", "mutant_parts", "containers", "misc" };
        if (hiddenGroups.Any(parameter.OptionGroups.ContainsKey) ||
            !parameter.OptionGroups.TryGetValue("ammo_explosives", out var ammo) ||
            ammo.Count != 2 ||
            !ammo.Contains("ammo_9x18_fmj", StringComparer.OrdinalIgnoreCase) ||
            !ammo.Contains("ammo_5.45x39_fmj", StringComparer.OrdinalIgnoreCase) ||
            parameter.OptionGroups.Values.SelectMany(items => items).Contains("grenade_f1", StringComparer.OrdinalIgnoreCase) ||
            FriendlyValueConverter.Display("ammo_explosives") != "Патроны")
        {
            throw new InvalidOperationException("The item preset did not expose only the requested visible categories and safe ammo entries.");
        }

        if (!parameter.Options.Contains("lz_nm_metal_part", StringComparer.OrdinalIgnoreCase) ||
            parameter.Validate("lz_nm_metal_part") is not null)
        {
            throw new InvalidOperationException("A previously selected allowlisted item from a hidden category became invalid.");
        }

        var viewModel = new ParameterValueViewModel(
            new ActionDefinition
            {
                HandlerId = "spawn_items",
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["item"] = "medkit" }
            },
            parameter,
            () => { });
        if (viewModel.Value != "medkit" || viewModel.DisplayValue != "Аптечка" ||
            viewModel.FilteredOptionEntries.Single().Value != "medkit")
        {
            throw new InvalidOperationException("The localized item label replaced the technical section ID.");
        }
        viewModel.Value = null!;
        if (viewModel.Value != "medkit")
        {
            throw new InvalidOperationException("A transient empty ComboBox selection erased the technical section ID.");
        }

        var legacyViewModel = new ParameterValueViewModel(
            new ActionDefinition
            {
                HandlerId = "spawn_items",
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["item"] = "lz_nm_metal_part" }
            },
            parameter,
            () => { });
        if (legacyViewModel.Value != "lz_nm_metal_part" ||
            legacyViewModel.ValidationMessage.Length != 0 ||
            legacyViewModel.FilteredOptions.Contains("lz_nm_metal_part", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A hidden category leaked into the picker or its saved active selection was lost.");
        }
    }

    private static void ValidateWritableTargetBinding(FrameworkElement target, DependencyProperty targetProperty)
    {
        var binding = BindingOperations.GetBinding(target, targetProperty);
        if (binding is null) return;

        var mode = binding.Mode;
        if (mode == BindingMode.Default)
        {
            var metadata = targetProperty.GetMetadata(target.GetType());
            mode = metadata is FrameworkPropertyMetadata { BindsTwoWayByDefault: true }
                ? BindingMode.TwoWay
                : BindingMode.OneWay;
        }
        if (mode is not (BindingMode.TwoWay or BindingMode.OneWayToSource)) return;

        var path = binding.Path?.Path;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('.') || path.Contains('[')) return;
        var source = binding.Source ?? target.DataContext;
        var property = source?.GetType().GetProperty(path, BindingFlags.Instance | BindingFlags.Public);
        if (property is { CanWrite: false })
        {
            throw new InvalidOperationException(
                $"{target.GetType().Name}.{targetProperty.Name} uses {mode} for read-only {source!.GetType().Name}.{path}.");
        }
    }

    private static IEnumerable<DependencyObject> EnumerateVisualTree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in EnumerateVisualTree(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }
}
