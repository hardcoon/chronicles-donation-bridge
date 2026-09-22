using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.DonationAlerts;

namespace ChroniclesDonationBridge.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly BridgeRuntime _runtime;
    private string _gamePath;
    private string _editorPath;
    private string _clientId;
    private string _expectedAccountCode;
    private QueueMode _queueMode;
    private int _waitMinutes;
    private int _maximumQueuedEvents;
    private int _globalCooldownSeconds;
    private bool _limitDonationAlertsApiRequests;
    private bool _minimizeToTrayOnClose;
    private string _status = string.Empty;
    private bool _busy;
    private AddonInstallationStatus? _addonStatus;

    public SettingsViewModel(BridgeRuntime runtime)
    {
        _runtime = runtime;
        var settings = runtime.Settings;
        _gamePath = settings.GamePath;
        _editorPath = settings.EditorPath;
        _clientId = settings.DonationAlertsClientId;
        _expectedAccountCode = settings.ExpectedAccountCode;
        _queueMode = settings.QueuePolicy.Mode;
        _waitMinutes = settings.QueuePolicy.WaitMinutes;
        _maximumQueuedEvents = settings.QueuePolicy.MaximumQueuedEvents;
        _globalCooldownSeconds = settings.GlobalCooldownSeconds;
        _limitDonationAlertsApiRequests = settings.LimitDonationAlertsApiRequests;
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
    }

    public IReadOnlyList<QueueMode> QueueModes { get; } = Enum.GetValues<QueueMode>();
    public string RedirectUri => DonationAlertsEndpoints.RedirectUri;
    public string Scopes => DonationAlertsEndpoints.Scopes;
    public string ApplicationVersion => (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "неизвестна") + " Beta";
    public string SecretHint => string.IsNullOrWhiteSpace(_runtime.Secrets.ClientSecret) ? "Секрет ещё не сохранён" : "Секрет сохранён в DPAPI; оставьте поле пустым, чтобы использовать его";
    public string AccountLabel => string.IsNullOrWhiteSpace(_runtime.Settings.DonationAlertsAccountCode)
        ? "Аккаунт не подключён"
        : _runtime.AccountMatchesExpected
            ? $"✓ {_runtime.Settings.DonationAlertsAccountName} ({_runtime.Settings.DonationAlertsAccountCode})"
            : $"⚠ Подключён {_runtime.Settings.DonationAlertsAccountCode}, ожидался {_runtime.Settings.ExpectedAccountCode}";
    public string DataDirectory => _runtime.DataDirectory;
    public string GamePath
    {
        get => _gamePath;
        set
        {
            if (!SetProperty(ref _gamePath, value)) return;
            _addonStatus = null;
            RaisePropertyChanged(nameof(InstallationStatus));
            RaisePropertyChanged(nameof(CanInstallBridge));
        }
    }
    public string EditorPath { get => _editorPath; set => SetProperty(ref _editorPath, value); }
    public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }
    public string ExpectedAccountCode { get => _expectedAccountCode; set => SetProperty(ref _expectedAccountCode, value); }
    public QueueMode QueueMode { get => _queueMode; set { if (SetProperty(ref _queueMode, value)) RaisePropertyChanged(nameof(WaitControlsEnabled)); } }
    public int WaitMinutes { get => _waitMinutes; set => SetProperty(ref _waitMinutes, value); }
    public int MaximumQueuedEvents { get => _maximumQueuedEvents; set => SetProperty(ref _maximumQueuedEvents, value); }
    public int GlobalCooldownSeconds { get => _globalCooldownSeconds; set => SetProperty(ref _globalCooldownSeconds, value); }
    public bool LimitDonationAlertsApiRequests { get => _limitDonationAlertsApiRequests; set => SetProperty(ref _limitDonationAlertsApiRequests, value); }
    public bool MinimizeToTrayOnClose { get => _minimizeToTrayOnClose; set => SetProperty(ref _minimizeToTrayOnClose, value); }
    public bool WaitControlsEnabled => QueueMode == QueueMode.WaitForDuration;
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }
    public string InstallationStatus => _addonStatus is null
        ? "Состояние аддона и шаблонов будет проверено автоматически."
        : _addonStatus.Message;
    public bool CanInstallBridge => !Busy && string.IsNullOrEmpty(_runtime.ValidateGamePath(GamePath));

    public async Task RefreshInstallationStatusAsync()
    {
        var gameError = _runtime.ValidateGamePath(GamePath);
        if (!string.IsNullOrEmpty(gameError))
        {
            _addonStatus = new AddonInstallationStatus(AddonInstallState.NotInstalled, gameError);
        }
        else
        {
            _addonStatus = await _runtime.InspectAddonAsync(GamePath);
        }
        RaisePropertyChanged(nameof(InstallationStatus));
        RaisePropertyChanged(nameof(CanInstallBridge));
    }

    public async Task InstallBridgeAsync()
    {
        if (Busy) return;
        Busy = true;
        RaisePropertyChanged(nameof(CanInstallBridge));
        try
        {
            await SaveAsync();
            _addonStatus = await _runtime.InstallOrUpdateAddonAsync(GamePath);
            _runtime.RefreshActionManifest();
            Status = _addonStatus.Message + " Аддон и шаблоны установлены одним пакетом. Полностью перезапустите игру.";
            RaisePropertyChanged(nameof(InstallationStatus));
        }
        finally
        {
            Busy = false;
            RaisePropertyChanged(nameof(CanInstallBridge));
        }
    }

    public async Task SaveAsync()
    {
        var gameError = _runtime.ValidateGamePath(GamePath);
        if (!string.IsNullOrEmpty(gameError)) throw new InvalidOperationException(gameError);
        _runtime.Settings.GamePath = Path.GetFullPath(GamePath);
        _runtime.Settings.EditorPath = string.IsNullOrWhiteSpace(EditorPath) ? "notepad.exe" : EditorPath.Trim();
        _runtime.Settings.DonationAlertsClientId = ClientId.Trim();
        _runtime.Settings.ExpectedAccountCode = ExpectedAccountCode.Trim().ToLowerInvariant();
        _runtime.Settings.QueuePolicy.Mode = QueueMode.WaitIndefinitely;
        _runtime.Settings.QueuePolicy.WaitMinutes = Math.Clamp(WaitMinutes, 1, 24 * 60);
        _runtime.Settings.QueuePolicy.MaximumQueuedEvents = Math.Clamp(MaximumQueuedEvents, 1, 10_000);
        _runtime.Settings.GlobalCooldownSeconds = Math.Clamp(GlobalCooldownSeconds, 0, 300);
        _runtime.Settings.LimitDonationAlertsApiRequests = LimitDonationAlertsApiRequests;
        _runtime.Settings.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        var warnings = _runtime.RefreshActionManifest();
        await _runtime.SaveSettingsAsync();
        Status = warnings.Count == 0 ? "Настройки сохранены." : "Сохранено с предупреждением: " + string.Join("; ", warnings);
    }

    public async Task ConnectAsync(string secret, Action<Uri> openBrowser)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            await SaveWithoutGameRequirementAsync();
            Status = "Ожидание авторизации в DonationAlerts…";
            await _runtime.ConnectDonationAlertsAsync(ClientId, secret, openBrowser);
            Status = _runtime.AccountMatchesExpected
                ? "DonationAlerts подключён: " + _runtime.Settings.DonationAlertsAccountCode
                : $"Внимание: подключён {_runtime.Settings.DonationAlertsAccountCode}, ожидался {_runtime.Settings.ExpectedAccountCode}. Обработка выключена.";
            RaisePropertyChanged(nameof(AccountLabel));
            RaisePropertyChanged(nameof(SecretHint));
        }
        finally { Busy = false; }
    }

    public async Task DisconnectAsync(bool forgetSecret)
    {
        await _runtime.DisconnectDonationAlertsAsync(forgetSecret);
        Status = "Аккаунт отключён.";
        if (forgetSecret) ClientId = string.Empty;
        RaisePropertyChanged(nameof(AccountLabel));
        RaisePropertyChanged(nameof(SecretHint));
    }

    public Task ExportAsync(string path) => _runtime.ExportRulesAsync(path);
    public async Task ImportAsync(string path)
    {
        await _runtime.ImportRulesAsync(path);
        Status = "Правила импортированы. Секреты не затрагивались.";
    }

    private async Task SaveWithoutGameRequirementAsync()
    {
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("Введите Client ID.");
        _runtime.Settings.DonationAlertsClientId = ClientId.Trim();
        _runtime.Settings.ExpectedAccountCode = ExpectedAccountCode.Trim().ToLowerInvariant();
        _runtime.Settings.QueuePolicy.Mode = QueueMode.WaitIndefinitely;
        _runtime.Settings.QueuePolicy.WaitMinutes = Math.Clamp(WaitMinutes, 1, 24 * 60);
        _runtime.Settings.QueuePolicy.MaximumQueuedEvents = Math.Clamp(MaximumQueuedEvents, 1, 10_000);
        _runtime.Settings.GlobalCooldownSeconds = Math.Clamp(GlobalCooldownSeconds, 0, 300);
        _runtime.Settings.LimitDonationAlertsApiRequests = LimitDonationAlertsApiRequests;
        _runtime.Settings.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        await _runtime.SaveSettingsAsync();
    }
}
