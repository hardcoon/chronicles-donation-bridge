using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.Persistence;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = SettingsNormalizer.CurrentSchemaVersion;
    public string GamePath { get; set; } = string.Empty;
    public string EditorPath { get; set; } = "notepad.exe";
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool UseLightTheme { get; set; }
    public ActionBrowserPreferences ActiveView { get; set; } = new();
    public ActionBrowserPreferences PresetView { get; set; } = new();
    public ActionBrowserPreferences UserPresetView { get; set; } = new();
    public bool Armed { get; set; }
    public string DonationAlertsClientId { get; set; } = string.Empty;
    public long? DonationAlertsUserId { get; set; }
    public string DonationAlertsAccountCode { get; set; } = string.Empty;
    public string DonationAlertsAccountName { get; set; } = string.Empty;
    public string ExpectedAccountCode { get; set; } = string.Empty;
    public bool LimitDonationAlertsApiRequests { get; set; } = true;
    public bool InitialDonationSnapshotCompleted { get; set; }
    public string LastDonationId { get; set; } = string.Empty;
    public DateTimeOffset? LastDonationCreatedAt { get; set; }
    public QueuePolicy QueuePolicy { get; set; } = new();
    public int GlobalCooldownSeconds { get; set; } = 5;
    public List<string> ObservedCurrencies { get; set; } = ["RUB", "USD", "EUR", "UAH", "KZT", "BYN", "BRL", "TRY"];
    public List<ActionDefinition> Actions { get; set; } = DefaultActionCatalog.CreateDefaultActiveInstances();
    public List<ActionDefinition> UserPresets { get; set; } = [];
}

public sealed class ActionBrowserPreferences
{
    public string Category { get; set; } = "all";
    public int Columns { get; set; } = 2;
    public bool AutomaticColumns { get; set; } = true;
    public ActionBrowserPreferences Clone() => new() { Category = Category, Columns = Columns, AutomaticColumns = AutomaticColumns };
    public void Normalize()
    {
        Columns = Math.Clamp(Columns, 1, 4);
        if (Category == "player") Category = "player_state";
        if (Category == "simulation") Category = "all";
        if (Category is not ("all" or "npc_mutants" or "player_state" or "inventory" or "world")) Category = "all";
    }
}

public sealed class SecretBundle
{
    public string ClientSecret { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string SocketConnectionToken { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAt { get; set; }

    public bool HasOAuthTokens => !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(RefreshToken);

    public void ClearTokens()
    {
        AccessToken = string.Empty;
        RefreshToken = string.Empty;
        SocketConnectionToken = string.Empty;
        AccessTokenExpiresAt = default;
    }
}

public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");
    public string SecretsFile => Path.Combine(RootDirectory, "secrets.dpapi");
    public string HistoryFile => Path.Combine(RootDirectory, "history.jsonl");
    public string ProcessedDonationsFile => Path.Combine(RootDirectory, "processed-donations.jsonl");
    public string PendingQueueFile => Path.Combine(RootDirectory, "pending-queue.json");
    public string LogFile => Path.Combine(RootDirectory, "bridge.log");
    public string AddonBackupsDirectory => Path.Combine(RootDirectory, "addon-backups");

    public static AppPaths CreateDefault()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "ChroniclesDonationBridge"));
    }

    public void EnsureExists() => Directory.CreateDirectory(RootDirectory);
}

public static class SettingsNormalizer
{
    public const int CurrentSchemaVersion = 6;
    private const int ActiveInstanceSchemaVersion = 2;

    // Never normalize the live model: open editors keep references to its actions.
    public static AppSettings CreatePersistenceSnapshot(AppSettings settings) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        GamePath = settings.GamePath,
        EditorPath = settings.EditorPath,
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
        UseLightTheme = settings.UseLightTheme,
        ActiveView = settings.ActiveView?.Clone() ?? new(),
        PresetView = settings.PresetView?.Clone() ?? new(),
        UserPresetView = settings.UserPresetView?.Clone() ?? new(),
        Armed = settings.Armed,
        DonationAlertsClientId = settings.DonationAlertsClientId,
        DonationAlertsUserId = settings.DonationAlertsUserId,
        DonationAlertsAccountCode = settings.DonationAlertsAccountCode,
        DonationAlertsAccountName = settings.DonationAlertsAccountName,
        ExpectedAccountCode = settings.ExpectedAccountCode,
        LimitDonationAlertsApiRequests = settings.LimitDonationAlertsApiRequests,
        InitialDonationSnapshotCompleted = settings.InitialDonationSnapshotCompleted,
        LastDonationId = settings.LastDonationId,
        LastDonationCreatedAt = settings.LastDonationCreatedAt,
        QueuePolicy = settings.QueuePolicy?.Clone() ?? new QueuePolicy(),
        GlobalCooldownSeconds = settings.GlobalCooldownSeconds,
        ObservedCurrencies = settings.ObservedCurrencies?.ToList() ?? [],
        Actions = settings.Actions?.Select(action => action.Clone()).ToList() ?? [],
        UserPresets = settings.UserPresets?.Select(action => action.Clone()).ToList() ?? []
    };

    public static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        var sourceSchemaVersion = settings.SchemaVersion <= 0 ? 1 : settings.SchemaVersion;
        if (sourceSchemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Версия settings.json {sourceSchemaVersion} новее поддерживаемой версии {CurrentSchemaVersion}.");
        }
        settings.SchemaVersion = CurrentSchemaVersion;
        settings.ActiveView ??= new();
        settings.PresetView ??= new();
        settings.UserPresetView ??= new();
        settings.ActiveView.Normalize();
        settings.PresetView.Normalize();
        settings.UserPresetView.Normalize();
        settings.GamePath ??= string.Empty;
        settings.EditorPath = string.IsNullOrWhiteSpace(settings.EditorPath) ? "notepad.exe" : settings.EditorPath.Trim();
        settings.GlobalCooldownSeconds = Math.Clamp(settings.GlobalCooldownSeconds, 0, 300);
        settings.ExpectedAccountCode = (settings.ExpectedAccountCode ?? string.Empty).Trim().ToLowerInvariant();
        settings.QueuePolicy ??= new QueuePolicy();
        if (!Enum.IsDefined(settings.QueuePolicy.Mode))
        {
            throw new InvalidDataException("settings.json содержит неизвестный режим очереди.");
        }
        // Schema 6 guarantees delivery: legacy Skip/TTL values remain readable,
        // but are migrated to an unlimited persistent queue.
        settings.QueuePolicy.Mode = QueueMode.WaitIndefinitely;
        settings.QueuePolicy.WaitMinutes = Math.Clamp(settings.QueuePolicy.WaitMinutes, 1, 24 * 60);
        settings.QueuePolicy.MaximumQueuedEvents = Math.Clamp(settings.QueuePolicy.MaximumQueuedEvents, 1, 10_000);
        settings.ObservedCurrencies = settings.ObservedCurrencies
            .Select(value =>
            {
                try { return Money.NormalizeCurrency(value); }
                catch (ArgumentException) { return null; }
            })
            .Where(value => value is not null)
            .Cast<string>()
            .Concat(["RUB", "USD", "EUR", "UAH", "KZT", "BYN", "BRL", "TRY"])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        settings.Actions = NormalizeActiveInstances(settings.Actions ?? [], sourceSchemaVersion);
        settings.UserPresets = NormalizeUserPresets(
            settings.UserPresets ?? [],
            settings.Actions.Select(action => action.Id));
        var usedTriggerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in settings.Actions.Concat(settings.UserPresets))
        {
            action.ParameterDefinitions ??= [];
            action.Parameters = action.Parameters is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(action.Parameters, StringComparer.OrdinalIgnoreCase);
            action.ItemSpawns ??= [];
            MigrateLegacyItemBundle(action);
            action.SpawnGroups ??= [];
            MigrateLegacySpawnGroupBundle(action);
            action.Triggers ??= [];
            foreach (var trigger in action.Triggers)
            {
                if (string.IsNullOrWhiteSpace(trigger.Id) || !usedTriggerIds.Add(trigger.Id))
                {
                    do
                    {
                        trigger.Id = Guid.NewGuid().ToString("N");
                    }
                    while (!usedTriggerIds.Add(trigger.Id));
                }
                if (Money.TryNormalizeCurrency(trigger.Currency, out var currency)) trigger.Currency = currency;
            }
        }
        return settings;
    }

    private static List<ActionDefinition> NormalizeActiveInstances(List<ActionDefinition> saved, int sourceSchemaVersion)
    {
        var result = new List<ActionDefinition>();
        var usedInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        var presets = DefaultActionCatalog.CreatePresets()
            .ToDictionary(action => action.HandlerId, StringComparer.Ordinal);

        foreach (var savedAction in saved)
        {
            // Schema v1 stored the preset catalog and used IsActive as a tab flag.
            // Schema v2 stores active instances only; inactive legacy catalog rows are templates, not instances.
            if (sourceSchemaVersion < ActiveInstanceSchemaVersion && !savedAction.IsActive)
            {
                continue;
            }

            var legacyHandlerId = sourceSchemaVersion < ActiveInstanceSchemaVersion
                ? savedAction.Id
                : savedAction.HandlerId;
            var handlerId = NormalizeLegacyHandlerId(legacyHandlerId);
            if (!Validation.ActionId().IsMatch(handlerId))
            {
                if (sourceSchemaVersion >= ActiveInstanceSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"У активного варианта {savedAction.Id} отсутствует допустимый HandlerId.");
                }
                continue;
            }

            var instanceId = NormalizeInstanceId(savedAction.Id, usedInstanceIds);
            var instance = sourceSchemaVersion < ActiveInstanceSchemaVersion && presets.TryGetValue(handlerId, out var preset)
                ? ResolveAgainstPreset(savedAction, preset)
                : savedAction.Clone();
            instance.Id = instanceId;
            instance.HandlerId = handlerId;
            instance.IsActive = true;
            result.Add(instance);
        }

        return result;
    }

    private static List<ActionDefinition> NormalizeUserPresets(
        List<ActionDefinition> saved,
        IEnumerable<string> reservedIds)
    {
        var result = new List<ActionDefinition>();
        var usedIds = new HashSet<string>(reservedIds, StringComparer.Ordinal);
        var presets = DefaultActionCatalog.CreatePresets()
            .ToDictionary(action => action.HandlerId, StringComparer.Ordinal);
        foreach (var savedPreset in saved)
        {
            var handlerId = NormalizeLegacyHandlerId(savedPreset.HandlerId);
            if (!Validation.ActionId().IsMatch(handlerId))
            {
                continue;
            }

            var normalized = presets.TryGetValue(handlerId, out var preset)
                ? ResolveAgainstPreset(savedPreset, preset)
                : savedPreset.Clone();
            if (Validation.TryNormalizeUserPresetName(savedPreset.DisplayName, out var displayName))
            {
                normalized.DisplayName = displayName;
            }
            normalized.HasCustomDisplayName = true;
            normalized.Id = NormalizeInstanceId(savedPreset.Id, usedIds);
            normalized.HandlerId = handlerId;
            normalized.IsActive = false;
            normalized.Triggers.Clear();
            result.Add(normalized);
        }
        return result;
    }

    private static ActionDefinition ResolveAgainstPreset(ActionDefinition saved, ActionDefinition preset)
    {
        var resolved = preset.Clone();
        resolved.CooldownSeconds = saved.CooldownSeconds;
        resolved.Triggers = (saved.Triggers ?? []).Select(trigger => trigger.Clone()).ToList();
        resolved.ItemSpawns = (saved.ItemSpawns ?? []).Select(entry => entry.Clone()).ToList();
        resolved.SpawnGroups = (saved.SpawnGroups ?? []).Select(entry => entry.Clone()).ToList();
        foreach (var definition in resolved.ParameterDefinitions)
        {
            if (saved.Parameters?.TryGetValue(definition.Key, out var value) == true && definition.Validate(value) is null)
            {
                resolved.Parameters[definition.Key] = value;
            }
        }
        MigrateLegacyItemBundle(resolved);
        MigrateLegacySpawnGroupBundle(resolved);
        return resolved;
    }

    private static void MigrateLegacyItemBundle(ActionDefinition action)
    {
        if (!string.Equals(action.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal) ||
            action.ItemSpawns.Count > 0)
        {
            return;
        }

        var item = action.Parameters.TryGetValue("item", out var savedItem) ? savedItem : "medkit";
        var countText = action.Parameters.TryGetValue("count", out var savedCount) ? savedCount : "1";
        if (!Validation.ItemSection().IsMatch(item) ||
            !int.TryParse(countText, out var count) || count is < 1 or > 50)
        {
            item = "medkit";
            count = 1;
        }
        action.ItemSpawns.Add(new ItemSpawnEntry { ItemId = item, Count = count });
    }

    private static void MigrateLegacySpawnGroupBundle(ActionDefinition action)
    {
        if (!SpawnGroupBundleCodec.Supports(action.HandlerId) || action.SpawnGroups.Count > 0)
        {
            return;
        }

        var variantKey = SpawnGroupBundleCodec.VariantParameterKey(action.HandlerId);
        var variantDefinition = action.ParameterDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Key, variantKey, StringComparison.OrdinalIgnoreCase));
        var strengthDefinition = action.ParameterDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Key, "strength", StringComparison.OrdinalIgnoreCase));
        var variant = action.Parameters.TryGetValue(variantKey, out var savedVariant)
            ? savedVariant
            : variantDefinition?.DefaultValue ?? string.Empty;
        var strength = action.Parameters.TryGetValue("strength", out var savedStrength)
            ? savedStrength
            : strengthDefinition?.DefaultValue ?? "medium";
        var countText = action.Parameters.TryGetValue("count", out var savedCount) ? savedCount : "1";
        if (variantDefinition?.Validate(variant) is not null ||
            strengthDefinition?.Validate(strength) is not null ||
            !int.TryParse(countText, out var count) || count is < 1 or > 99)
        {
            variant = variantDefinition?.DefaultValue ?? string.Empty;
            strength = strengthDefinition?.DefaultValue ?? "medium";
            count = action.HandlerId == "spawn_mutants" ? 3 : 1;
        }
        action.SpawnGroups.Add(new SpawnGroupEntry { VariantId = variant, Strength = strength, Count = count });
    }

    private static string NormalizeLegacyHandlerId(string? handlerId)
    {
        var normalized = (handlerId ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "spawn_dogs_3" ? "spawn_mutants" : normalized;
    }

    private static string NormalizeInstanceId(string? instanceId, ISet<string> usedInstanceIds)
    {
        var normalized = (instanceId ?? string.Empty).Trim().ToLowerInvariant();
        if (!Validation.InstanceId().IsMatch(normalized) || !usedInstanceIds.Add(normalized))
        {
            do
            {
                normalized = ActionDefinition.NewInstanceId();
            }
            while (!usedInstanceIds.Add(normalized));
        }
        return normalized;
    }
}
