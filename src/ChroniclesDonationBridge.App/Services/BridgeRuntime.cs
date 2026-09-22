using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.DonationAlerts;
using ChroniclesDonationBridge.GameIpc;
using ChroniclesDonationBridge.Persistence;

namespace ChroniclesDonationBridge.App.Services;

public sealed class BridgeRuntime : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly JsonSettingsStore _settingsStore;
    private readonly DpapiSecretStore _secretStore;
    private readonly JsonLineHistoryStore _historyStore;
    private readonly JsonLineProcessedDonationStore _processedStore;
    private readonly JsonPendingDispatchStore _pendingDispatchStore;
    private readonly SafeFileLogger _logger;
    private readonly NotifyingHistorySink _notifyingHistory;
    private readonly NamedPipeGameServer _gameServer;
    private readonly HttpClient _httpClient;
    private readonly DonationAlertsApiRateLimiter _apiRateLimiter;
    private readonly DonationAlertsRestClient _donationRest;
    private readonly LoopbackOAuthFlow _oauthFlow;
    private readonly SemaphoreSlim _donationGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ActionManifestLoader _manifestLoader = new();
    private readonly AddonPackageManager _addonPackageManager;
    private readonly FilePresetLoader _filePresetLoader;
    private readonly ConcurrentDictionary<string, byte> _persistedQueuedCommands = new(StringComparer.Ordinal);
    private List<ActionDefinition> _presets = [];
    private IReadOnlyDictionary<string, string> _filePresetSources =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private string _filePresetFingerprint = string.Empty;
    private string _filePresetStatus = "Файловые пресеты ещё не проверены";
    private DonationDispatcher _dispatcher;
    private DonationAlertsSession? _donationSession;
    private Task? _drainLoop;

    public BridgeRuntime(AppPaths? paths = null)
    {
        _paths = paths ?? AppPaths.CreateDefault();
        _settingsStore = new JsonSettingsStore(_paths);
        _secretStore = new DpapiSecretStore(_paths);
        _historyStore = new JsonLineHistoryStore(_paths);
        _processedStore = new JsonLineProcessedDonationStore(_paths);
        _pendingDispatchStore = new JsonPendingDispatchStore(_paths);
        _logger = new SafeFileLogger(_paths);
        _addonPackageManager = new AddonPackageManager(_paths);
        _filePresetLoader = new FilePresetLoader();
        _notifyingHistory = new NotifyingHistorySink(_historyStore, entry =>
        {
            UpdateCheckpointFromHistory(entry);
            UpdatePersistedQueueAvailability(entry);
            HistoryAdded?.Invoke(entry);
        });
        _dispatcher = new DonationDispatcher(
            _processedStore, _notifyingHistory, pendingStore: _pendingDispatchStore);
        _gameServer = new NamedPipeGameServer(typeof(BridgeRuntime).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        _httpClient = new HttpClient();
        _apiRateLimiter = new DonationAlertsApiRateLimiter(() => Settings.LimitDonationAlertsApiRequests);
        _donationRest = new DonationAlertsRestClient(_httpClient, _apiRateLimiter);
        _oauthFlow = new LoopbackOAuthFlow(_donationRest);
    }

    public AppSettings Settings { get; private set; } = new();
    public SecretBundle Secrets { get; private set; } = new();
    public IReadOnlyList<ActionDefinition> Presets => _presets;
    public string DataDirectory => _paths.RootDirectory;
    public string FilePresetStatus => _filePresetStatus;
    public bool AccountMatchesExpected => string.IsNullOrWhiteSpace(Settings.ExpectedAccountCode) ||
        string.IsNullOrWhiteSpace(Settings.DonationAlertsAccountCode) ||
        string.Equals(Settings.ExpectedAccountCode, Settings.DonationAlertsAccountCode, StringComparison.OrdinalIgnoreCase);
    public bool DonationAccountConfigured => Secrets.HasOAuthTokens &&
        Settings.DonationAlertsUserId is not null &&
        !string.IsNullOrWhiteSpace(Settings.DonationAlertsAccountCode);
    public bool CanArmDonationProcessing => DonationAccountConfigured && AccountMatchesExpected;

    public Task<QueueTimingSnapshot> GetQueueTimingAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.GetTimingAsync(_gameServer.IsConnected, _gameServer.IsGameReady, cancellationToken);

    public event Action<GameConnectionSnapshot>? GameConnectionChanged;
    public event Action<DonationConnectionState>? DonationConnectionChanged;
    public event Action<EventHistoryEntry>? HistoryAdded;
    public event Action? SettingsChanged;

    public async Task<IReadOnlyList<EventHistoryEntry>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        Secrets = await _secretStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var warning in RefreshFilePresets())
        {
            await _logger.WriteAsync("WARN", warning, cancellationToken).ConfigureAwait(false);
        }
        if (Settings.Armed && !CanArmDonationProcessing)
        {
            Settings.Armed = false;
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(Settings.GamePath))
        {
            var manifestWarnings = _manifestLoader.MergeFromGame(Settings.GamePath, _presets, allowNewActions: false).ToList();
            manifestWarnings.AddRange(_manifestLoader.ApplyPresetsToInstances(Settings.Actions, _presets));
            manifestWarnings.AddRange(_manifestLoader.ApplyPresetsToInstances(Settings.UserPresets, _presets, preserveDisplayNames: true));
            foreach (var warning in manifestWarnings)
            {
                await _logger.WriteAsync("WARN", warning, cancellationToken).ConfigureAwait(false);
            }
        }

        _dispatcher = new DonationDispatcher(
            _processedStore, _notifyingHistory, pendingStore: _pendingDispatchStore)
        {
            GlobalCooldown = TimeSpan.FromSeconds(Settings.GlobalCooldownSeconds)
        };
        await _dispatcher.RestoreAsync(cancellationToken).ConfigureAwait(false);
        var history = await _historyStore.LoadRecentAsync(500, cancellationToken).ConfigureAwait(false);
        await LoadPersistedQueueAvailabilityAsync(history, cancellationToken).ConfigureAwait(false);

        _gameServer.ConnectionChanged += snapshot =>
        {
            GameConnectionChanged?.Invoke(snapshot);
            _ = DrainSafelyAsync();
        };
        _gameServer.ResultProcessing += CompleteResultSafelyAsync;
        _gameServer.ProtocolError += exception => _ = _logger.WriteAsync("WARN", $"Game IPC: {exception.Message}");
        _gameServer.Start();
        _drainLoop = Task.Run(() => DrainLoopAsync(_lifetime.Token));

        if (Secrets.HasOAuthTokens && !string.IsNullOrWhiteSpace(Settings.DonationAlertsClientId) && !string.IsNullOrWhiteSpace(Secrets.ClientSecret))
        {
            _ = Task.Run(() => StartDonationSessionSafelyAsync(_lifetime.Token));
        }
        else
        {
            DonationConnectionChanged?.Invoke(new DonationConnectionState(false, "Аккаунт не подключён"));
        }

        return history;
    }

    public async Task ConnectDonationAlertsAsync(
        string clientId,
        string? newClientSecret,
        Action<Uri> openBrowser,
        CancellationToken cancellationToken = default)
    {
        var clientSecret = string.IsNullOrWhiteSpace(newClientSecret) ? Secrets.ClientSecret : newClientSecret.Trim();
        var result = await _oauthFlow.AuthorizeAsync(clientId.Trim(), clientSecret, openBrowser, cancellationToken).ConfigureAwait(false);

        Settings.DonationAlertsClientId = clientId.Trim();
        Settings.DonationAlertsUserId = result.Profile.Id;
        Settings.DonationAlertsAccountCode = result.Profile.Code;
        Settings.DonationAlertsAccountName = result.Profile.Name;
        Settings.InitialDonationSnapshotCompleted = false;
        Settings.LastDonationId = string.Empty;
        Settings.LastDonationCreatedAt = null;
        Secrets.ClientSecret = clientSecret;
        ApplyTokens(result.Tokens);
        Secrets.SocketConnectionToken = result.Profile.SocketConnectionToken;
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        await StartDonationSessionAsync(result.Profile, cancellationToken).ConfigureAwait(false);
        if (!AccountMatchesExpected)
        {
            Settings.Armed = false;
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
            DonationConnectionChanged?.Invoke(new DonationConnectionState(false, $"Подключён {Settings.DonationAlertsAccountCode}, ожидался {Settings.ExpectedAccountCode}. Обработка выключена."));
        }
        SettingsChanged?.Invoke();
    }

    public async Task DisconnectDonationAlertsAsync(bool forgetApplicationSecret, CancellationToken cancellationToken = default)
    {
        if (_donationSession is not null)
        {
            await _donationSession.DisposeAsync().ConfigureAwait(false);
            _donationSession = null;
        }
        Secrets.ClearTokens();
        if (forgetApplicationSecret)
        {
            Secrets.ClientSecret = string.Empty;
            Settings.DonationAlertsClientId = string.Empty;
        }
        Settings.DonationAlertsUserId = null;
        Settings.DonationAlertsAccountCode = string.Empty;
        Settings.DonationAlertsAccountName = string.Empty;
        Settings.Armed = false;
        Settings.InitialDonationSnapshotCompleted = false;
        Settings.LastDonationId = string.Empty;
        Settings.LastDonationCreatedAt = null;
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        DonationConnectionChanged?.Invoke(new DonationConnectionState(false, "Аккаунт отключён"));
        SettingsChanged?.Invoke();
    }

    public async Task SetArmedAsync(bool armed, CancellationToken cancellationToken = default)
    {
        if (armed && !DonationAccountConfigured)
        {
            throw new InvalidOperationException("Сначала подключите аккаунт DonationAlerts в настройках.");
        }
        if (armed && !AccountMatchesExpected)
        {
            throw new InvalidOperationException($"Нельзя включить обработку: подключён аккаунт {Settings.DonationAlertsAccountCode}, ожидался {Settings.ExpectedAccountCode}.");
        }
        Settings.Armed = armed;
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        var errors = CatalogValidation.Validate(Settings.Actions.Concat(Settings.UserPresets));
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
        _dispatcher.GlobalCooldown = TimeSpan.FromSeconds(Math.Clamp(Settings.GlobalCooldownSeconds, 0, 300));
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke();
    }

    public async Task<(IReadOnlyList<EventHistoryEntry> History, ResultRecoveryReport Recovery)> RefreshEventsAsync(
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var recovery = await _gameServer.RecoverPendingResultsAsync(linked.Token).ConfigureAwait(false);
        var history = await _historyStore.LoadRecentAsync(500, linked.Token).ConfigureAwait(false);
        return (history, recovery);
    }

    public async Task AddActionInstanceAsync(ActionDefinition draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var preset = _presets.FirstOrDefault(item =>
            string.Equals(item.HandlerId, draft.HandlerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Выбранный пресет больше не существует. Обновите список пресетов.");

        var instance = preset.Clone();
        instance.CooldownSeconds = draft.CooldownSeconds;
        instance.Parameters = new Dictionary<string, string>(draft.Parameters, StringComparer.OrdinalIgnoreCase);
        instance.ItemSpawns = draft.ItemSpawns.Select(entry => entry.Clone()).ToList();
        instance.SpawnGroups = draft.SpawnGroups.Select(entry => entry.Clone()).ToList();
        instance.Triggers = draft.Triggers.Select(trigger => trigger.Clone()).ToList();
        if (draft.HasCustomDisplayName &&
            Validation.TryNormalizeUserPresetName(draft.DisplayName, out var customDisplayName))
        {
            instance.DisplayName = customDisplayName;
            instance.HasCustomDisplayName = true;
        }
        instance = instance.CloneAsNewInstance();
        var errors = instance.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        Settings.Actions.Add(instance);
        try
        {
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.Actions.RemoveAll(item => string.Equals(item.Id, instance.Id, StringComparison.Ordinal));
            throw;
        }
    }

    public async Task AddUserPresetAsync(ActionDefinition draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var preset = _presets.FirstOrDefault(item =>
            string.Equals(item.HandlerId, draft.HandlerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Исходный пресет больше не существует. Обновите список пресетов.");

        var custom = preset.Clone();
        custom.CooldownSeconds = draft.CooldownSeconds;
        custom.Parameters = new Dictionary<string, string>(draft.Parameters, StringComparer.OrdinalIgnoreCase);
        custom.ItemSpawns = draft.ItemSpawns.Select(entry => entry.Clone()).ToList();
        custom.SpawnGroups = draft.SpawnGroups.Select(entry => entry.Clone()).ToList();
        custom.Triggers.Clear();
        custom.DisplayName = Validation.TryNormalizeUserPresetName(draft.DisplayName, out var customDisplayName)
            ? customDisplayName
            : preset.DisplayName;
        custom.HasCustomDisplayName = true;
        custom = custom.CloneAsNewInstance();
        custom.IsActive = false;
        var errors = custom.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        Settings.UserPresets.Add(custom);
        try
        {
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.UserPresets.RemoveAll(item => string.Equals(item.Id, custom.Id, StringComparison.Ordinal));
            throw;
        }
    }

    public async Task DeleteUserPresetAsync(string presetId, CancellationToken cancellationToken = default)
    {
        var preset = Settings.UserPresets.FirstOrDefault(item =>
            string.Equals(item.Id, presetId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Пользовательский пресет уже удалён.");
        var index = Settings.UserPresets.IndexOf(preset);
        Settings.UserPresets.RemoveAt(index);
        try
        {
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.UserPresets.Insert(index, preset);
            throw;
        }
    }

    public async Task RenameUserPresetAsync(
        string presetId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var preset = Settings.UserPresets.FirstOrDefault(item =>
            string.Equals(item.Id, presetId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Пользовательский пресет больше не существует.");
        if (!Validation.TryNormalizeUserPresetName(displayName, out var normalizedName))
        {
            throw new InvalidOperationException(
                $"Название должно содержать от 1 до {Validation.UserPresetNameMaxLength} символов без переносов строк.");
        }

        var previousName = preset.DisplayName;
        var previouslyCustom = preset.HasCustomDisplayName;
        preset.DisplayName = normalizedName;
        preset.HasCustomDisplayName = true;
        try
        {
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            preset.DisplayName = previousName;
            preset.HasCustomDisplayName = previouslyCustom;
            throw;
        }
    }

    public async Task DeleteActionInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var instance = Settings.Actions.FirstOrDefault(item =>
            string.Equals(item.Id, instanceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Активный вариант уже удалён.");
        Settings.Actions.Remove(instance);
        try
        {
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.Actions.Add(instance);
            throw;
        }
    }

    public string ValidateGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Выберите папку игры.";
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return exception.Message; }
        if (!File.Exists(Path.Combine(fullPath, "bin", "xrEngine.exe"))) return "Не найден bin\\xrEngine.exe.";
        if (!File.Exists(Path.Combine(fullPath, "fsgame.ltx"))) return "Не найден fsgame.ltx.";
        return string.Empty;
    }

    public IReadOnlyList<string> RefreshActionManifest()
    {
        var warnings = RefreshFilePresets().ToList();
        warnings.AddRange(_manifestLoader.MergeFromGame(Settings.GamePath, _presets, allowNewActions: false));
        warnings.AddRange(_manifestLoader.ApplyPresetsToInstances(Settings.Actions, _presets));
        warnings.AddRange(_manifestLoader.ApplyPresetsToInstances(
            Settings.UserPresets,
            _presets,
            preserveDisplayNames: true));
        SettingsChanged?.Invoke();
        return warnings;
    }

    public void RefreshActionManifestIfFilesChanged()
    {
        var current = _filePresetLoader.Fingerprint(Settings.GamePath);
        if (!string.Equals(current, _filePresetFingerprint, StringComparison.Ordinal)) RefreshActionManifest();
    }

    public bool IsHandlerAvailable(string handlerId) =>
        _presets.Any(preset => string.Equals(preset.HandlerId, handlerId, StringComparison.Ordinal));

    public string GetActionOriginLabel(string handlerId) => IsHandlerAvailable(handlerId)
        ? string.Empty
        : $"Скрипт не установлен в выбранный игровой аддон: {handlerId}";

    public Task<AddonInstallationStatus> InspectAddonAsync(
        string gamePath, CancellationToken cancellationToken = default) =>
        _addonPackageManager.InspectAsync(gamePath, cancellationToken);

    public Task<AddonInstallationStatus> InstallOrUpdateAddonAsync(
        string gamePath, CancellationToken cancellationToken = default) =>
        _addonPackageManager.InstallOrUpdateAsync(gamePath, cancellationToken);

    public void OpenActionCode(ActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_filePresetSources.TryGetValue(action.HandlerId, out var scriptPath) || !File.Exists(scriptPath))
            throw new FileNotFoundException("Исходный файл пресета отсутствует в поставке аддона.", action.ScriptRelativePath);

        var editor = string.IsNullOrWhiteSpace(Settings.EditorPath) ? "notepad.exe" : Settings.EditorPath;
        var start = new ProcessStartInfo { FileName = editor, UseShellExecute = true };
        start.ArgumentList.Add(scriptPath);
        Process.Start(start);
    }

    public async Task TestActionAsync(ActionDefinition action, CancellationToken cancellationToken = default)
    {
        if (!IsHandlerAvailable(action.HandlerId))
            throw new InvalidOperationException(GetActionOriginLabel(action.HandlerId));
        var parameterErrors = action.Validate();
        if (parameterErrors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, parameterErrors));
        var trigger = action.Triggers.FirstOrDefault(rule => rule.Enabled);
        var donation = new DonationEvent
        {
            Id = "test:" + Guid.NewGuid().ToString("N"),
            Username = "Локальный тест",
            Amount = trigger?.Amount ?? 0m,
            Currency = trigger?.Currency ?? "RUB",
            CreatedAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        await _dispatcher.EnqueueActionAsync(
            donation,
            action,
            Settings.QueuePolicy,
            cancellationToken,
            Settings.Actions,
            _presets).ConfigureAwait(false);
        await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryManuallyAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var action = Settings.Actions.FirstOrDefault(item => item.Id == entry.ActionId)
            ?? throw new InvalidOperationException("Действие из истории больше не существует.");
        if (!IsHandlerAvailable(action.HandlerId))
            throw new InvalidOperationException(GetActionOriginLabel(action.HandlerId));
        var donation = new DonationEvent
        {
            Id = $"manual:{entry.DonationId}:{Guid.NewGuid():N}",
            Username = string.IsNullOrWhiteSpace(entry.Donor) ? "Ручной повтор" : entry.Donor,
            Amount = entry.Amount,
            Currency = string.IsNullOrWhiteSpace(entry.Currency) ? "RUB" : entry.Currency,
            CreatedAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        await _dispatcher.EnqueueActionAsync(
            donation,
            action,
            Settings.QueuePolicy,
            cancellationToken,
            Settings.Actions,
            _presets).ConfigureAwait(false);
        await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, cancellationToken).ConfigureAwait(false);
    }

    public bool CanRemoveFromQueue(EventHistoryEntry? entry)
    {
        if (entry is not { State: DispatchState.Queued, DonationId.Length: > 0 })
        {
            return false;
        }

        if (_dispatcher.IsQueued(entry.DonationId, entry.CommandId))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(entry.CommandId) &&
            _persistedQueuedCommands.ContainsKey(entry.CommandId);
    }

    public async Task RemoveFromQueueAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != DispatchState.Queued || string.IsNullOrWhiteSpace(entry.DonationId))
        {
            throw new InvalidOperationException("Удалить можно только событие, которое ещё находится в очереди.");
        }

        var cancelled = await _dispatcher.CancelQueuedAsync(entry, cancellationToken).ConfigureAwait(false);
        if (cancelled is null)
        {
            _persistedQueuedCommands.TryRemove(entry.CommandId, out _);
            throw new InvalidOperationException("Событие уже вышло из очереди и не может быть отменено.");
        }

        // A real donation becomes terminal here, so persist the checkpoint that
        // the notifying history sink advanced together with the cancellation.
        await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueueCancellationSummary> ClearQueueAsync(
        CancellationToken cancellationToken = default)
    {
        var queuedEntries = await _historyStore.LoadRecentAsync(500, cancellationToken).ConfigureAwait(false);
        var cancelled = await _dispatcher.CancelAllQueuedAsync(
            queuedEntries,
            cancellationToken).ConfigureAwait(false);
        _persistedQueuedCommands.Clear();
        if (cancelled.AffectedCount > 0)
        {
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }

        return cancelled;
    }

    public async Task ExportRulesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var export = new RuleExport
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = Settings.Actions,
            UserPresets = Settings.UserPresets,
            QueuePolicy = Settings.QueuePolicy,
            GlobalCooldownSeconds = Settings.GlobalCooldownSeconds
        };
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, export, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportRulesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var import = await JsonSerializer.DeserializeAsync<RuleExport>(stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Пустой файл правил.");
        if (import.SchemaVersion is < 1 or > SettingsNormalizer.CurrentSchemaVersion)
        {
            throw new InvalidDataException("Неподдерживаемая версия файла правил.");
        }
        var normalizedImport = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = import.SchemaVersion,
            Actions = import.Actions ?? [],
            UserPresets = import.UserPresets ?? [],
            QueuePolicy = import.QueuePolicy ?? new QueuePolicy(),
            GlobalCooldownSeconds = import.GlobalCooldownSeconds
        });
        var manifestWarnings = _manifestLoader.ApplyPresetsToInstances(normalizedImport.Actions, _presets);
        manifestWarnings = manifestWarnings
            .Concat(_manifestLoader.ApplyPresetsToInstances(normalizedImport.UserPresets, _presets, preserveDisplayNames: true))
            .ToList();
        if (manifestWarnings.Count > 0)
        {
            throw new InvalidDataException(
                "Файл правил содержит действия, которых нет в установленном аддоне:" +
                Environment.NewLine + string.Join(Environment.NewLine, manifestWarnings));
        }
        var errors = CatalogValidation.Validate(normalizedImport.Actions.Concat(normalizedImport.UserPresets));
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var previousActions = Settings.Actions.Select(action => action.Clone()).ToList();
        var previousUserPresets = Settings.UserPresets.Select(action => action.Clone()).ToList();
        var previousQueuePolicy = Settings.QueuePolicy.Clone();
        var previousGlobalCooldown = Settings.GlobalCooldownSeconds;
        try
        {
            Settings.Actions = normalizedImport.Actions;
            Settings.UserPresets = normalizedImport.UserPresets;
            Settings.QueuePolicy = normalizedImport.QueuePolicy;
            Settings.GlobalCooldownSeconds = normalizedImport.GlobalCooldownSeconds;
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Settings.Actions = previousActions;
            Settings.UserPresets = previousUserPresets;
            Settings.QueuePolicy = previousQueuePolicy;
            Settings.GlobalCooldownSeconds = previousGlobalCooldown;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await SaveSettingsAsync().ConfigureAwait(false); }
        catch (Exception exception) { await _logger.WriteAsync("WARN", $"Save on exit: {exception.Message}").ConfigureAwait(false); }
        if (_donationSession is not null) await _donationSession.DisposeAsync().ConfigureAwait(false);
        await _gameServer.DisposeAsync().ConfigureAwait(false);
        if (_drainLoop is not null)
        {
            try { await _drainLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _httpClient.Dispose();
        _apiRateLimiter.Dispose();
        _donationGate.Dispose();
        _recoveryGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ProcessDonationAsync(DonationEvent donation)
    {
        await _donationGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var existingRecord = await _processedStore.FindAsync(donation.Id, _lifetime.Token).ConfigureAwait(false);
            if (existingRecord is { State: not DispatchState.Queued })
            {
                if (existingRecord.State == DispatchState.Sent && _gameServer.IsAwaitingResult(existingRecord.CommandId)) return;
                if (existingRecord.State == DispatchState.Sent)
                {
                    EventHistoryEntry? commandSnapshot = null;
                    try
                    {
                        commandSnapshot = await _historyStore.FindCommandSnapshotAsync(
                            donation.Id,
                            existingRecord.CommandId,
                            _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        await _logger.WriteAsync(
                            "WARN",
                            "Sent command history recovery: " + exception.Message,
                            _lifetime.Token).ConfigureAwait(false);
                    }
                    var sourceAction = commandSnapshot is null
                        ? null
                        : Settings.Actions.FirstOrDefault(action =>
                            string.Equals(action.Id, commandSnapshot.ActionId, StringComparison.Ordinal));
                    var uncertainHistory = HistoryRecovery.CreateInterruptedSendEntry(
                        donation,
                        existingRecord,
                        commandSnapshot,
                        sourceAction,
                        DateTimeOffset.UtcNow);
                    await _processedStore.RecordAsync(new ProcessedDonationRecord
                    {
                        DonationId = donation.Id,
                        CommandId = existingRecord.CommandId,
                        State = DispatchState.Uncertain,
                        RecordedAt = DateTimeOffset.UtcNow
                    }, _lifetime.Token).ConfigureAwait(false);
                    await _notifyingHistory.AppendAsync(uncertainHistory, _lifetime.Token).ConfigureAwait(false);
                }
                else
                {
                    UpdateCheckpoint(donation);
                }
                await _settingsStore.SaveAsync(Settings, _lifetime.Token).ConfigureAwait(false);
                return;
            }
            var currency = Money.NormalizeCurrency(donation.Currency);
            if (!Settings.ObservedCurrencies.Contains(currency, StringComparer.Ordinal))
            {
                Settings.ObservedCurrencies.Add(currency);
                Settings.ObservedCurrencies.Sort(StringComparer.Ordinal);
            }

            if (!Settings.Armed)
            {
                await _processedStore.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = donation.Id,
                    State = DispatchState.Skipped,
                    RecordedAt = DateTimeOffset.UtcNow
                }, _lifetime.Token).ConfigureAwait(false);
                await _notifyingHistory.AppendAsync(new EventHistoryEntry
                {
                    DonationId = donation.Id,
                    DonationCreatedAt = donation.CreatedAt,
                    Donor = donation.Username,
                    Amount = donation.Amount,
                    Currency = currency,
                    State = DispatchState.Skipped,
                    Detail = "Обработка донатов выключена",
                    CanRetryManually = true
                }, _lifetime.Token).ConfigureAwait(false);
            }
            else
            {
                await _dispatcher.EnqueueAsync(
                    donation,
                    Settings.Actions.Where(action => IsHandlerAvailable(action.HandlerId)).ToList(),
                    Settings.QueuePolicy,
                    _lifetime.Token,
                    _presets).ConfigureAwait(false);
                await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, _lifetime.Token).ConfigureAwait(false);
            }

            await _settingsStore.SaveAsync(Settings, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _donationGate.Release();
        }
    }

    private async Task StartDonationSessionSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartDonationSessionAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DonationConnectionChanged?.Invoke(new DonationConnectionState(false, "Ошибка подключения: " + exception.Message));
            await _logger.WriteAsync("ERROR", "DonationAlerts session: " + exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartDonationSessionAsync(DonationAlertsProfile? knownProfile, CancellationToken cancellationToken)
    {
        if (_donationSession is not null) await _donationSession.DisposeAsync().ConfigureAwait(false);
        _donationSession = new DonationAlertsSession(
            _donationRest,
            Settings.DonationAlertsClientId,
            Secrets.ClientSecret,
            ReadTokens,
            SaveTokensAsync);
        _donationSession.ConnectionChanged += state =>
        {
            if (state.Connected && !AccountMatchesExpected)
            {
                DonationConnectionChanged?.Invoke(new DonationConnectionState(false, $"Подключён {Settings.DonationAlertsAccountCode}, ожидался {Settings.ExpectedAccountCode}. Обработка выключена."));
            }
            else
            {
                DonationConnectionChanged?.Invoke(state);
            }
            if (state.Connected && Settings.InitialDonationSnapshotCompleted)
            {
                _ = RecoverGapSafelyAsync();
            }
        };
        _donationSession.DonationReceived += ProcessDonationAsync;
        var accessToken = await _donationSession.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var profile = knownProfile ?? await _donationRest.GetProfileAsync(accessToken, cancellationToken).ConfigureAwait(false);
        Settings.DonationAlertsUserId = profile.Id;
        Settings.DonationAlertsAccountCode = profile.Code;
        Settings.DonationAlertsAccountName = profile.Name;
        if (!AccountMatchesExpected) Settings.Armed = false;
        Secrets.SocketConnectionToken = profile.SocketConnectionToken;
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);

        await _donationSession.StartAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!Settings.InitialDonationSnapshotCompleted)
        {
            var newestPage = await _donationRest.GetDonationsAsync(accessToken, 1, cancellationToken).ConfigureAwait(false);
            var newest = newestPage.Donations.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
            if (newest is not null) UpdateCheckpoint(newest);
            Settings.InitialDonationSnapshotCompleted = true;
            await _settingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RecoverGapSafelyAsync().ConfigureAwait(false);
        }
        SettingsChanged?.Invoke();
    }

    private OAuthTokenSet ReadTokens()
    {
        var seconds = (int)Math.Max(0, (Secrets.AccessTokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        return new OAuthTokenSet
        {
            AccessToken = Secrets.AccessToken,
            RefreshToken = Secrets.RefreshToken,
            ExpiresInSeconds = seconds,
            ObtainedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task SaveTokensAsync(OAuthTokenSet tokens, CancellationToken cancellationToken)
    {
        ApplyTokens(tokens);
        await _secretStore.SaveAsync(Secrets, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyTokens(OAuthTokenSet tokens)
    {
        Secrets.AccessToken = tokens.AccessToken;
        Secrets.RefreshToken = tokens.RefreshToken;
        Secrets.AccessTokenExpiresAt = tokens.ExpiresAt;
    }

    private IReadOnlyList<string> RefreshFilePresets()
    {
        var result = _filePresetLoader.Load(Settings.GamePath);
        _presets = result.Presets.Select(preset => preset.Clone()).ToList();
        _filePresetSources = result.SourcePaths;
        _filePresetFingerprint = _filePresetLoader.Fingerprint(Settings.GamePath);
        _filePresetStatus = result.Warnings.Count == 0
            ? $"Файловых пресетов установлено: {_presets.Count}; исходников: {_filePresetSources.Count}"
            : $"Корректных пресетов: {_presets.Count}; ошибок: {result.Warnings.Count}";
        return result.Warnings;
    }

    private void UpdateCheckpoint(DonationEvent donation)
    {
        if (Settings.LastDonationCreatedAt is null || donation.CreatedAt >= Settings.LastDonationCreatedAt)
        {
            Settings.LastDonationCreatedAt = donation.CreatedAt;
            Settings.LastDonationId = donation.Id;
        }
    }

    private async Task CompleteResultSafelyAsync(GameExecutionResult result)
    {
        try
        {
            var completed = await _dispatcher.CompleteAsync(result, _lifetime.Token).ConfigureAwait(false);
            if (completed is not null) await _settingsStore.SaveAsync(Settings, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { await _logger.WriteAsync("WARN", "Result handling: " + exception.Message).ConfigureAwait(false); }
    }

    private async Task RecoverGapSafelyAsync()
    {
        if (_donationSession is null || !_recoveryGate.Wait(0)) return;
        try
        {
            var checkpoint = new RecoveryCheckpoint
            {
                Initialized = Settings.InitialDonationSnapshotCompleted,
                LastDonationId = Settings.LastDonationId,
                LastCreatedAt = Settings.LastDonationCreatedAt
            };
            var recovered = await _donationSession.RecoverGapAsync(checkpoint, Settings.QueuePolicy, _lifetime.Token).ConfigureAwait(false);
            foreach (var donation in recovered) await ProcessDonationAsync(donation).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _logger.WriteAsync("WARN", "Donation recovery: " + exception.Message).ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task DrainSafelyAsync()
    {
        try { await _dispatcher.DrainAsync(_gameServer, Settings.QueuePolicy, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { await _logger.WriteAsync("WARN", "Queue drain: " + exception.Message).ConfigureAwait(false); }
    }

    private void UpdateCheckpointFromHistory(EventHistoryEntry entry)
    {
        if (entry.State is DispatchState.Queued or DispatchState.Deferred or DispatchState.Sent) return;
        if (entry.DonationCreatedAt == default || string.IsNullOrWhiteSpace(entry.DonationId) ||
            entry.DonationId.StartsWith("test:", StringComparison.Ordinal) ||
            entry.DonationId.StartsWith("manual:", StringComparison.Ordinal)) return;
        UpdateCheckpoint(new DonationEvent { Id = entry.DonationId, CreatedAt = entry.DonationCreatedAt });
    }

    private async Task LoadPersistedQueueAvailabilityAsync(
        IReadOnlyList<EventHistoryEntry> history,
        CancellationToken cancellationToken)
    {
        _persistedQueuedCommands.Clear();
        foreach (var entry in history
                     .Where(entry => entry.State == DispatchState.Queued &&
                         !string.IsNullOrWhiteSpace(entry.DonationId) &&
                         !string.IsNullOrWhiteSpace(entry.CommandId))
                     .GroupBy(entry => entry.CommandId, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            var current = await _processedStore.FindCommandAsync(
                entry.DonationId, entry.CommandId, cancellationToken).ConfigureAwait(false);
            if (current is not { State: DispatchState.Queued } ||
                string.IsNullOrWhiteSpace(current.CommandId) ||
                !string.Equals(entry.CommandId, current.CommandId, StringComparison.Ordinal))
            {
                continue;
            }

            _persistedQueuedCommands[current.CommandId] = 0;
        }
    }

    private void UpdatePersistedQueueAvailability(EventHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DonationId))
        {
            return;
        }

        if (entry.State == DispatchState.Queued && !string.IsNullOrWhiteSpace(entry.CommandId))
        {
            _persistedQueuedCommands[entry.CommandId] = 0;
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.CommandId))
        {
            _persistedQueuedCommands.TryRemove(entry.CommandId, out _);
        }
    }

    private async Task DrainLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) await DrainSafelyAsync().ConfigureAwait(false);
    }

    private sealed class NotifyingHistorySink : IEventHistorySink
    {
        private readonly IEventHistorySink _inner;
        private readonly Action<EventHistoryEntry> _notify;
        public NotifyingHistorySink(IEventHistorySink inner, Action<EventHistoryEntry> notify) { _inner = inner; _notify = notify; }
        public async Task AppendAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            await _inner.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            _notify(entry);
        }
    }
}

public sealed class RuleExport
{
    public int SchemaVersion { get; set; } = SettingsNormalizer.CurrentSchemaVersion;
    public List<ActionDefinition> Actions { get; set; } = [];
    public List<ActionDefinition> UserPresets { get; set; } = [];
    public QueuePolicy QueuePolicy { get; set; } = new();
    public int GlobalCooldownSeconds { get; set; } = 5;
}
