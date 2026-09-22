using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.DonationAlerts;
using ChroniclesDonationBridge.GameIpc;

namespace ChroniclesDonationBridge.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly BridgeRuntime _runtime;
    private string _donationStatus = "Не подключён";
    private string _gameStatus = "Ожидание игры";
    private bool _donationConnected;
    private bool _gameConnected;
    private bool _gameReady;
    private EventHistoryEntry? _selectedHistory;
    private string _notice = string.Empty;
    private bool _isRefreshingEvents;
    private bool _restoringViewPreferences;
    private CancellationTokenSource? _viewSave;
    private DispatcherTimer? _nextEffectTimer;
    private bool _updatingNextEffect;
    private string _nextEffectStatus = string.Empty;
    private DateTimeOffset _nextPresetScanAt;

    public MainViewModel(BridgeRuntime runtime)
    {
        _runtime = runtime;
        ActiveBrowser = new ActionBrowserViewModel(ActiveActions);
        PresetBrowser = new ActionBrowserViewModel(Presets);
        UserPresetBrowser = new ActionBrowserViewModel(UserPresets);
        ActiveBrowser.PreferencesChanged += SaveViewPreferences;
        PresetBrowser.PreferencesChanged += SaveViewPreferences;
        UserPresetBrowser.PreferencesChanged += SaveViewPreferences;
        CodeCommand = new RelayCommand(parameter => Run(() => _runtime.OpenActionCode(((ActionViewModel)parameter!).Model)));
        TestCommand = new AsyncRelayCommand(
            parameter => TestActionAsync((ActionViewModel)parameter!),
            allowConcurrentExecutions: true);
        AddFromPresetCommand = new AsyncRelayCommand(parameter => AddFromPresetAsync((ActionViewModel)parameter!));
        SaveAsUserPresetCommand = new AsyncRelayCommand(parameter => SaveAsUserPresetAsync((ActionViewModel)parameter!));
        RenameUserPresetCommand = new AsyncRelayCommand(parameter => RenameUserPresetAsync((ActionViewModel)parameter!));
        DeleteUserPresetCommand = new AsyncRelayCommand(parameter => DeleteUserPresetAsync((ActionViewModel)parameter!));
        DeleteActiveCommand = new AsyncRelayCommand(parameter => DeleteActiveAsync((ActionViewModel)parameter!));
        AddTriggerCommand = new AsyncRelayCommand(parameter => EditTriggerAsync((ActionViewModel)parameter!, null));
        EditTriggerCommand = new AsyncRelayCommand(parameter => EditTriggerAsync(((TriggerRuleViewModel)parameter!).Owner, (TriggerRuleViewModel)parameter!));
        DeleteTriggerCommand = new AsyncRelayCommand(parameter => DeleteTriggerAsync((TriggerRuleViewModel)parameter!));
        RetryCommand = new AsyncRelayCommand(() => RunAsync(() => _runtime.RetryManuallyAsync(SelectedHistory!)), CanRetrySelected);
        RemoveQueuedCommand = new AsyncRelayCommand(
            parameter => RunAsync(() => RemoveQueuedAsync((EventHistoryEntry)parameter!)),
            CanRemoveQueued);
        ClearQueueCommand = new AsyncRelayCommand(() => RunAsync(ClearQueueAsync));
        SortActiveByPriceCommand = new AsyncRelayCommand(
            SortActiveByPriceAsync,
            () => ActiveActions.Count > 1);
        SaveCommand = new AsyncRelayCommand(() => SaveActionsAsync());
        SaveActionCommand = new AsyncRelayCommand(parameter => SaveActionsAsync(parameter as ActionViewModel));
        RefreshEventsCommand = new AsyncRelayCommand(RefreshEventsAsync);
        RefreshPresetsCommand = new AsyncRelayCommand(RefreshPresetsAsync);

        _runtime.GameConnectionChanged += snapshot => OnUi(() => ApplyGameSnapshot(snapshot));
        _runtime.DonationConnectionChanged += state => OnUi(() => ApplyDonationState(state));
        _runtime.HistoryAdded += entry => OnUi(() => AddHistory(entry));
        _runtime.SettingsChanged += () => OnUi(RefreshFromSettings);
    }

    public ObservableCollection<ActionViewModel> ActiveActions { get; } = [];
    public ObservableCollection<ActionViewModel> Presets { get; } = [];
    public ObservableCollection<ActionViewModel> UserPresets { get; } = [];
    public ObservableCollection<EventHistoryEntry> History { get; } = [];
    public ObservableCollection<string> Currencies { get; } = [];
    public ActionBrowserViewModel ActiveBrowser { get; }
    public ActionBrowserViewModel PresetBrowser { get; }
    public ActionBrowserViewModel UserPresetBrowser { get; }
    public Func<ActionViewModel, TriggerRuleViewModel?, TriggerEditorResult?>? ShowTriggerEditor { get; set; }
    public Func<ActionViewModel, string?>? ShowRenameUserPreset { get; set; }
    public Func<Task>? ShowSettings { get; set; }

    public ICommand CodeCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand AddFromPresetCommand { get; }
    public ICommand SaveAsUserPresetCommand { get; }
    public ICommand RenameUserPresetCommand { get; }
    public ICommand DeleteUserPresetCommand { get; }
    public ICommand DeleteActiveCommand { get; }
    public ICommand AddTriggerCommand { get; }
    public ICommand EditTriggerCommand { get; }
    public ICommand DeleteTriggerCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RemoveQueuedCommand { get; }
    public ICommand ClearQueueCommand { get; }
    public ICommand SortActiveByPriceCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveActionCommand { get; }
    public ICommand RefreshEventsCommand { get; }
    public ICommand RefreshPresetsCommand { get; }
    public string RefreshEventsButtonText => _isRefreshingEvents ? "Обновление…" : "Обновить события";

    public string DonationStatus { get => _donationStatus; private set => SetProperty(ref _donationStatus, value); }
    public string GameStatus { get => _gameStatus; private set => SetProperty(ref _gameStatus, value); }
    public bool DonationConnected { get => _donationConnected; private set => SetProperty(ref _donationConnected, value); }
    public bool GameConnected { get => _gameConnected; private set => SetProperty(ref _gameConnected, value); }
    public bool GameReady { get => _gameReady; private set => SetProperty(ref _gameReady, value); }
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }
    public string NextEffectStatus { get => _nextEffectStatus; internal set => SetProperty(ref _nextEffectStatus, value); }
    public string DataDirectory => _runtime.DataDirectory;
    public bool IsLightTheme => _runtime.Settings.UseLightTheme;
    public bool CanArm => _runtime.CanArmDonationProcessing;
    public string ProcessingSummary => Armed ? "Включена · активные действия выполняются по донатам" : "Остановлена · донаты не обрабатываются";
    public string ArmHint => !_runtime.DonationAccountConfigured
        ? "Сначала подключите аккаунт DonationAlerts в настройках"
        : !_runtime.AccountMatchesExpected
            ? $"Подключён аккаунт {_runtime.Settings.DonationAlertsAccountCode}, ожидался {_runtime.Settings.ExpectedAccountCode}"
            : "Включить или приостановить обработку донатов";

    public bool Armed
    {
        get => _runtime.Settings.Armed;
        set
        {
            if (_runtime.Settings.Armed == value) return;
            if (value && !_runtime.DonationAccountConfigured)
            {
                ShowError("Сначала подключите аккаунт DonationAlerts в настройках.");
                RaisePropertyChanged();
                return;
            }
            if (value && !_runtime.AccountMatchesExpected)
            {
                ShowError($"Подключён аккаунт {_runtime.Settings.DonationAlertsAccountCode}, ожидался {_runtime.Settings.ExpectedAccountCode}. Проверьте настройки.");
                RaisePropertyChanged();
                return;
            }
            _runtime.Settings.Armed = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ProcessingSummary));
            _ = RunAsync(() => _runtime.SetArmedAsync(value));
        }
    }

    public EventHistoryEntry? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (SetProperty(ref _selectedHistory, value) && RetryCommand is AsyncRelayCommand command) command.RaiseCanExecuteChanged();
        }
    }

    public async Task InitializeAsync()
    {
        var history = await _runtime.InitializeAsync();
        var visibleHistory = EventHistoryProjection.LatestPerCommand(history.Concat(History));
        History.Clear();
        foreach (var entry in visibleHistory) History.Add(entry);
        RefreshFromSettings();
        StartNextEffectTimer();
        await UpdateNextEffectStatusAsync();
    }

    public async Task OpenSettingsAsync()
    {
        if (ShowSettings is not null) await ShowSettings();
        RefreshFromSettings();
    }

    public async Task SetLightThemeAsync(bool useLightTheme)
    {
        if (_runtime.Settings.UseLightTheme == useLightTheme) return;
        var previous = _runtime.Settings.UseLightTheme;
        _runtime.Settings.UseLightTheme = useLightTheme;
        try
        {
            ThemeManager.Apply(useLightTheme);
            RaisePropertyChanged(nameof(IsLightTheme));
            await _runtime.SaveSettingsAsync();
        }
        catch
        {
            _runtime.Settings.UseLightTheme = previous;
            ThemeManager.Apply(previous);
            RaisePropertyChanged(nameof(IsLightTheme));
            throw;
        }
    }

    private void RefreshFromSettings()
    {
        _restoringViewPreferences = true;
        try
        {
            RestoreView(ActiveBrowser, _runtime.Settings.ActiveView);
            RestoreView(PresetBrowser, _runtime.Settings.PresetView);
            RestoreView(UserPresetBrowser, _runtime.Settings.UserPresetView);
        }
        finally { _restoringViewPreferences = false; }
        var activeEditors = ActiveActions.ToDictionary(action => action.Id, StringComparer.Ordinal);
        var presetEditors = Presets.ToDictionary(action => action.HandlerId, StringComparer.Ordinal);
        var userPresetEditors = UserPresets.ToDictionary(action => action.Id, StringComparer.Ordinal);
        var currentResults = ActiveActions
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().LastResult, StringComparer.Ordinal);
        var currentDrafts = Presets
            .GroupBy(item => item.HandlerId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Model.Clone(), StringComparer.Ordinal);
        ActiveActions.Clear();
        Presets.Clear();
        UserPresets.Clear();
        foreach (var action in _runtime.Settings.Actions)
        {
            var viewModel = new ActionViewModel(
                action,
                availabilityMessage: _runtime.GetActionOriginLabel(action.HandlerId))
                { DisplayOrdinal = ActiveActions.Count + 1 };
            if (activeEditors.TryGetValue(action.Id, out var editor) && ReferenceEquals(editor.Model, action))
                viewModel.RestorePendingEditsFrom(editor);
            if (currentResults.TryGetValue(action.Id, out var result)) viewModel.LastResult = result;
            ActiveActions.Add(viewModel);
        }
        foreach (var preset in _runtime.Presets)
        {
            currentDrafts.TryGetValue(preset.HandlerId, out var existingDraft);
            var viewModel = new ActionViewModel(CreatePresetDraft(preset, existingDraft), isPreset: true);
            if (presetEditors.TryGetValue(preset.HandlerId, out var editor)) viewModel.RestorePendingEditsFrom(editor);
            Presets.Add(viewModel);
        }
        foreach (var preset in _runtime.Settings.UserPresets)
        {
            var viewModel = new ActionViewModel(
                preset,
                isPreset: true,
                isUserPreset: true,
                availabilityMessage: _runtime.GetActionOriginLabel(preset.HandlerId));
            if (userPresetEditors.TryGetValue(preset.Id, out var editor) && ReferenceEquals(editor.Model, preset))
                viewModel.RestorePendingEditsFrom(editor);
            UserPresets.Add(viewModel);
        }
        Currencies.Clear();
        foreach (var currency in _runtime.Settings.ObservedCurrencies) Currencies.Add(currency);
        RaisePropertyChanged(nameof(Armed));
        RaisePropertyChanged(nameof(ProcessingSummary));
        RaisePropertyChanged(nameof(CanArm));
        RaisePropertyChanged(nameof(ArmHint));
        RaisePropertyChanged(nameof(DataDirectory));
        RaisePropertyChanged(nameof(IsLightTheme));
        if (SortActiveByPriceCommand is AsyncRelayCommand sortCommand) sortCommand.RaiseCanExecuteChanged();
    }

    private static void RestoreView(ActionBrowserViewModel browser, ChroniclesDonationBridge.Persistence.ActionBrowserPreferences preferences)
    {
        browser.SelectCategory(preferences.Category);
        browser.Columns = preferences.Columns;
        browser.AutomaticColumns = preferences.AutomaticColumns;
    }

    private async void SaveViewPreferences()
    {
        if (_restoringViewPreferences) return;
        _runtime.Settings.ActiveView = new() { Category = ActiveBrowser.SelectedCategory.Id, Columns = ActiveBrowser.Columns, AutomaticColumns = ActiveBrowser.AutomaticColumns };
        _runtime.Settings.PresetView = new() { Category = PresetBrowser.SelectedCategory.Id, Columns = PresetBrowser.Columns, AutomaticColumns = PresetBrowser.AutomaticColumns };
        _runtime.Settings.UserPresetView = new() { Category = UserPresetBrowser.SelectedCategory.Id, Columns = UserPresetBrowser.Columns, AutomaticColumns = UserPresetBrowser.AutomaticColumns };
        _viewSave?.Cancel();
        var pending = new CancellationTokenSource();
        _viewSave = pending;
        try
        {
            await Task.Delay(400, pending.Token);
            await _runtime.SaveSettingsAsync();
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception exception) { Notice = "Не удалось сохранить вид: " + exception.Message; }
        finally
        {
            if (ReferenceEquals(_viewSave, pending)) _viewSave = null;
            pending.Dispose();
        }
    }

    public async Task SaveEditorDraftAsync(ActionViewModel original, ActionViewModel draft)
    {
        if (!draft.TryCommitPendingEdits(out var error)) throw new InvalidOperationException(error);
        // A background settings refresh can replace view models while the dialog is open.
        var target = ResolveAction(original);
        if (target is null) throw new InvalidOperationException("Действие больше недоступно. Закройте редактор и выберите его заново.");
        var rollback = target.CreateEditorDraft();
        target.ApplyEditorDraft(draft);
        try
        {
            if (!target.IsBuiltInPreset) await _runtime.SaveSettingsAsync();
            Notice = target.IsBuiltInPreset
                ? "Настройки пресета сохранены в черновике. Его можно сохранить как пользовательскую предустановку."
                : target.IsUserPreset ? "Пользовательский пресет обновлён." : "Настройки действия сохранены.";
        }
        catch { target.ApplyEditorDraft(rollback); throw; }
    }

    private async Task AddFromPresetAsync(ActionViewModel preset)
    {
        if (!preset.IsUserPreset) return;
        if (!preset.TryCommitPendingEdits(out var pendingError))
        {
            ShowError(pendingError);
            return;
        }
        var errors = preset.Model.Validate();
        if (errors.Count > 0)
        {
            ShowError(string.Join(Environment.NewLine, errors));
            return;
        }
        try
        {
            var activeDraft = preset.Model.Clone();
            activeDraft.Triggers.Clear();
            await _runtime.AddActionInstanceAsync(activeDraft);
            Notice = $"«{preset.DisplayName}» добавлено в активные. Назначьте ему сумму во вкладке «Активные».";
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async Task SaveAsUserPresetAsync(ActionViewModel preset)
    {
        if (!preset.IsBuiltInPreset) return;
        if (!preset.TryCommitPendingEdits(out var error)) { ShowError(error); return; }
        try
        {
            await _runtime.AddUserPresetAsync(preset.Model);
            Notice = $"«{preset.DisplayName}» сохранено в пользовательские пресеты.";
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task DeleteUserPresetAsync(ActionViewModel preset)
    {
        if (!preset.IsUserPreset) return;
        var answer = MessageBox.Show(
            $"Удалить пользовательский пресет «{preset.DisplayName}»?\n\nУже добавленные активные варианты останутся без изменений.",
            "Chronicles Donation Bridge — Beta", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _runtime.DeleteUserPresetAsync(preset.Id);
            Notice = $"Пользовательский пресет «{preset.DisplayName}» удалён.";
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task RenameUserPresetAsync(ActionViewModel preset)
    {
        if (!preset.IsUserPreset || ShowRenameUserPreset is null) return;
        var displayName = ShowRenameUserPreset(preset);
        if (displayName is null) return;
        try
        {
            await _runtime.RenameUserPresetAsync(preset.Id, displayName);
            Notice = $"Пользовательский пресет переименован в «{displayName.Trim()}».";
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task DeleteActiveAsync(ActionViewModel action)
    {
        if (action.IsPreset) return;
        var answer = MessageBox.Show(
            $"Удалить «{action.DisplayName}» из активных?\n\nИсходный пресет останется во вкладке «Пресеты».",
            "Chronicles Donation Bridge — Beta",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _runtime.DeleteActionInstanceAsync(action.Id);
            Notice = $"«{action.DisplayName}» удалено из активных. Пресет остался доступен.";
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async Task EditTriggerAsync(ActionViewModel action, TriggerRuleViewModel? existing)
    {
        var result = ShowTriggerEditor?.Invoke(action, existing);
        if (result is null) return;
        var current = ResolveAction(action);
        if (current is null) { ShowError("Действие больше недоступно. Откройте список заново."); return; }
        if (existing is not null)
        {
            existing = current.Triggers.FirstOrDefault(trigger => trigger.Model.Id == existing.Model.Id);
            if (existing is null) { ShowError("Условие уже удалено. Откройте редактор заново."); return; }
        }
        action = current;
        (List<ActionDefinition> Actions, List<ActionDefinition> UserPresets)? rollback =
            action.IsBuiltInPreset ? null : ClonePersistedActions();
        if (result.ReplaceAllPrices)
        {
            action.ReplaceTriggers(result.Rules);
        }
        else if (existing is null)
        {
            action.AddTrigger(result.SingleRule);
        }
        else
        {
            var index = action.Model.Triggers.IndexOf(existing.Model);
            if (index < 0)
            {
                if (rollback is not null) RestoreActions(rollback.Value);
                ShowError("Ценовое условие было изменено в другом окне. Откройте его заново.");
                return;
            }
            existing.Model.Comparator = result.SingleRule.Comparator;
            existing.Model.Amount = result.SingleRule.Amount;
            existing.Model.Currency = result.SingleRule.Currency;
            existing.Model.Enabled = result.SingleRule.Enabled;
            existing.Refresh();
        }
        if (action.IsBuiltInPreset)
        {
            Notice = result.ReplaceAllPrices ? $"Цены черновика пересчитаны для {result.Rules.Count} валют." : "Сумма добавлена в черновик пресета.";
            return;
        }
        try
        {
            await _runtime.SaveSettingsAsync();
            Notice = result.ReplaceAllPrices ? $"Прежние цены заменены расчётом для {result.Rules.Count} валют." : "Ценовое условие сохранено.";
        }
        catch (Exception exception)
        {
            if (rollback is not null) RestoreActions(rollback.Value);
            ShowError(exception.Message);
        }
    }

    private async Task DeleteTriggerAsync(TriggerRuleViewModel trigger)
    {
        (List<ActionDefinition> Actions, List<ActionDefinition> UserPresets)? rollback =
            trigger.Owner.IsBuiltInPreset ? null : ClonePersistedActions();
        trigger.Owner.RemoveTrigger(trigger);
        if (trigger.Owner.IsBuiltInPreset)
        {
            Notice = "Сумма удалена из черновика пресета.";
            return;
        }
        try { await _runtime.SaveSettingsAsync(); Notice = "Ценовое условие удалено."; }
        catch (Exception exception) { if (rollback is not null) RestoreActions(rollback.Value); ShowError(exception.Message); }
    }

    private async Task TestActionAsync(ActionViewModel action)
    {
        if (!action.TryCommitPendingEdits(out var error)) { ShowError(error); return; }
        await RunAsync(() => _runtime.TestActionAsync(action.Model));
    }

    private async Task SaveActionsAsync(ActionViewModel? requestedAction = null)
    {
        var edited = requestedAction is null ? ActiveActions.ToList() : [requestedAction];
        foreach (var action in edited)
            if (!action.TryValidatePendingEdits(out var error)) { ShowError($"«{action.DisplayName}»: {error}"); return; }
        var rollback = CloneActions();
        foreach (var action in edited) action.TryCommitPendingEdits(out _);
        try
        {
            await _runtime.SaveSettingsAsync();
            Notice = "Параметры и паузы после срабатывания сохранены.";
        }
        catch (Exception exception)
        {
            // The draft remains visible when validation fails, but it cannot reach the pipe;
            // SaveSettings writes atomically and the dispatcher validates again before enqueue.
            RestoreActions(rollback);
            ShowError(exception.Message);
        }
    }

    private async Task SortActiveByPriceAsync()
    {
        var previousOrder = _runtime.Settings.Actions;
        var sorted = ActionPriceOrdering.SortByMinimumAssignedAmount(previousOrder);
        if (sorted.Select(action => action.Id).SequenceEqual(previousOrder.Select(action => action.Id), StringComparer.Ordinal))
        {
            Notice = "Активные действия уже отсортированы по цене.";
            return;
        }

        _runtime.Settings.Actions = sorted;
        try
        {
            await _runtime.SaveSettingsAsync();
            RefreshFromSettings();
            Notice = "Активные действия отсортированы по минимальной назначенной сумме.";
        }
        catch (Exception exception)
        {
            _runtime.Settings.Actions = previousOrder;
            RefreshFromSettings();
            ShowError(exception.Message);
        }
    }

    private (List<ActionDefinition> Actions, List<ActionDefinition> UserPresets) ClonePersistedActions() =>
        (_runtime.Settings.Actions.Select(item => item.Clone()).ToList(),
         _runtime.Settings.UserPresets.Select(item => item.Clone()).ToList());

    private List<ActionDefinition> CloneActions() => _runtime.Settings.Actions.Select(item => item.Clone()).ToList();

    private static ActionDefinition CreatePresetDraft(ActionDefinition preset, ActionDefinition? existing)
    {
        var draft = preset.Clone();
        draft.IsActive = false;
        if (existing is null) return draft;

        draft.CooldownSeconds = existing.CooldownSeconds;
        if (string.Equals(draft.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal))
        {
            draft.ItemSpawns = existing.ItemSpawns.Select(entry => entry.Clone()).ToList();
        }
        if (SpawnGroupBundleCodec.Supports(draft.HandlerId))
        {
            draft.SpawnGroups = existing.SpawnGroups.Select(entry => entry.Clone()).ToList();
        }
        foreach (var definition in draft.ParameterDefinitions)
        {
            if (existing.Parameters.TryGetValue(definition.Key, out var value) && definition.Validate(value) is null)
            {
                draft.Parameters[definition.Key] = value;
            }
        }
        draft.Triggers.Clear();
        return draft;
    }

    private void RestoreActions(List<ActionDefinition> actions)
    {
        _runtime.Settings.Actions = actions;
        RefreshFromSettings();
    }

    private void RestoreActions((List<ActionDefinition> Actions, List<ActionDefinition> UserPresets) snapshot)
    {
        _runtime.Settings.Actions = snapshot.Actions;
        _runtime.Settings.UserPresets = snapshot.UserPresets;
        RefreshFromSettings();
    }

    private ActionViewModel? ResolveAction(ActionViewModel action) => action.IsUserPreset
        ? UserPresets.FirstOrDefault(item => item.Id == action.Id)
        : action.IsBuiltInPreset
            ? Presets.FirstOrDefault(item => item.HandlerId == action.HandlerId)
            : ActiveActions.FirstOrDefault(item => item.Id == action.Id);

    private void ApplyGameSnapshot(GameConnectionSnapshot snapshot)
    {
        GameConnected = snapshot.Connected;
        GameReady = snapshot.Ready;
        GameStatus = GameStatusPresentation.Connection(snapshot);
        _ = UpdateNextEffectStatusAsync();
    }

    private void ApplyDonationState(DonationConnectionState state)
    {
        DonationConnected = state.Connected;
        DonationStatus = state.Message;
        RaisePropertyChanged(nameof(CanArm));
        RaisePropertyChanged(nameof(ArmHint));
    }

    private void AddHistory(EventHistoryEntry entry)
    {
        var existingIndex = -1;
        for (var index = 0; index < History.Count; index++)
        {
            if (!EventHistoryProjection.IsSameLogicalEvent(History[index], entry)) continue;
            existingIndex = index;
            break;
        }

        if (existingIndex >= 0)
        {
            var keepSelected = EventHistoryProjection.IsSameLogicalEvent(SelectedHistory, entry);
            History[existingIndex] = entry;
            if (keepSelected) SelectedHistory = entry;
        }
        else
        {
            var insertionIndex = 0;
            var entryTime = EventHistoryProjection.SortTime(entry);
            while (insertionIndex < History.Count &&
                   EventHistoryProjection.SortTime(History[insertionIndex]) >= entryTime)
            {
                insertionIndex++;
            }
            History.Insert(insertionIndex, entry);
        }
        while (History.Count > 500) History.RemoveAt(History.Count - 1);
        if (RemoveQueuedCommand is AsyncRelayCommand removeCommand) removeCommand.RaiseCanExecuteChanged();
        var action = ActiveActions.FirstOrDefault(item => item.Id == entry.ActionId);
        if (action is not null) action.LastResult = $"{entry.State}: {entry.Detail}";
        _ = UpdateNextEffectStatusAsync();
    }

    private void StartNextEffectTimer()
    {
        if (_nextEffectTimer is not null) return;
        _nextEffectTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _nextEffectTimer.Tick += NextEffectTimerOnTick;
        _nextEffectTimer.Start();
    }

    private async void NextEffectTimerOnTick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.UtcNow >= _nextPresetScanAt)
        {
            _nextPresetScanAt = DateTimeOffset.UtcNow.AddSeconds(2);
            _runtime.RefreshActionManifestIfFilesChanged();
        }
        await UpdateNextEffectStatusAsync();
    }

    private async Task RefreshPresetsAsync()
    {
        var warnings = _runtime.RefreshActionManifest();
        Notice = warnings.Count == 0
            ? _runtime.FilePresetStatus
            : _runtime.FilePresetStatus + " · " + string.Join("; ", warnings.Take(2));
        await Task.CompletedTask;
    }

    private async Task UpdateNextEffectStatusAsync()
    {
        if (_updatingNextEffect) return;
        _updatingNextEffect = true;
        try
        {
            var snapshot = await _runtime.GetQueueTimingAsync();
            if (string.IsNullOrWhiteSpace(snapshot.ActionName))
            {
                NextEffectStatus = string.Empty;
                return;
            }

            var actionName = $"«{snapshot.ActionName}»";
            if (snapshot.AwaitingResult)
            {
                NextEffectStatus = snapshot.WaitingCount > 0
                    ? $"Выполняется: {actionName} · ещё в очереди: {snapshot.WaitingCount}"
                    : $"Выполняется: {actionName} · ждём подтверждение игры";
                return;
            }
            if (snapshot.WaitingForGame)
            {
                NextEffectStatus = $"Следующий эффект: {actionName} · в очереди: {snapshot.WaitingCount} · ждём готовность игры";
                return;
            }

            if (snapshot.SecondsUntilNext <= 0)
            {
                NextEffectStatus = $"Следующий эффект: {actionName} · готов к запуску · в очереди: {snapshot.WaitingCount}";
                return;
            }

            var seconds = snapshot.SecondsUntilNext;
            var countdown = seconds >= 3600
                ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss")
                : TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
            NextEffectStatus = $"Следующий эффект: {actionName} · через {countdown} · в очереди: {snapshot.WaitingCount}";
        }
        catch (ObjectDisposedException)
        {
            Dispose();
        }
        catch (OperationCanceledException)
        {
            Dispose();
        }
        finally
        {
            _updatingNextEffect = false;
        }
    }

    public void Dispose()
    {
        if (_nextEffectTimer is null) return;
        _nextEffectTimer.Stop();
        _nextEffectTimer.Tick -= NextEffectTimerOnTick;
        _nextEffectTimer = null;
    }

    private async Task RemoveQueuedAsync(EventHistoryEntry entry)
    {
        await _runtime.RemoveFromQueueAsync(entry);
        Notice = $"«{entry.ActionName}» удалено из очереди.";
    }

    private async Task ClearQueueAsync()
    {
        var answer = MessageBox.Show(
            "Удалить все ещё не отправленные эффекты из очереди? Уже переданный игре эффект отменён не будет, но если игра его отложит, он не вернётся в очередь.",
            "Очистить очередь", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var result = await _runtime.ClearQueueAsync();
        Notice = result switch
        {
            { AffectedCount: 0 } => "Очередь уже пуста.",
            { CancelledEntries.Count: 0, SuppressedInFlightRetries: > 0 } =>
                "Очередь очищена. Текущий отложенный эффект не вернётся в неё после ответа игры.",
            { SuppressedInFlightRetries: > 0 } =>
                $"Из очереди удалено эффектов: {result.CancelledEntries.Count}. Текущий отложенный эффект также не будет повторён.",
            _ => $"Из очереди удалено эффектов: {result.CancelledEntries.Count}."
        };
        await UpdateNextEffectStatusAsync();
    }

    private async Task RefreshEventsAsync()
    {
        _isRefreshingEvents = true;
        RaisePropertyChanged(nameof(RefreshEventsButtonText));
        try
        {
            var refreshed = await _runtime.RefreshEventsAsync();
            var selectedHistory = SelectedHistory;
            // Keep events arriving while the file was being read.
            var merged = EventHistoryProjection.LatestPerCommand(refreshed.History.Concat(History));
            History.Clear();
            foreach (var entry in merged) History.Add(entry);
            SelectedHistory = History.FirstOrDefault(entry =>
                EventHistoryProjection.IsSameLogicalEvent(entry, selectedHistory));
            if (RemoveQueuedCommand is AsyncRelayCommand remove) remove.RaiseCanExecuteChanged();
            var report = refreshed.Recovery;
            Notice = report.RequestedCount == 0
                ? "История обновлена. Неполученных ответов нет."
                : report.RemainingCount == 0
                    ? $"История обновлена. Уточнено статусов: {report.ResolvedCount}. Эффекты повторно не запускались."
                    : $"Ожидается ответов: {report.RemainingCount}. Запрос сохранённых результатов продолжится после возврата игры; эффекты не повторяются.";
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally
        {
            _isRefreshingEvents = false;
            RaisePropertyChanged(nameof(RefreshEventsButtonText));
        }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private bool CanRetrySelected() => SelectedHistory is { ActionId.Length: > 0, CanRetryManually: true };

    private bool CanRemoveQueued(object? parameter) =>
        parameter is EventHistoryEntry entry && _runtime.CanRemoveFromQueue(entry);

    private void Run(Action operation)
    {
        try { operation(); }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void ShowError(string message)
    {
        Notice = message;
        MessageBox.Show(message, "Chronicles Donation Bridge — Beta", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
