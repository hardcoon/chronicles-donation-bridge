using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ChroniclesDonationBridge.Core;

public enum TriggerComparator
{
    Exact,
    GreaterOrEqual
}

public enum ParameterKind
{
    Boolean,
    Integer,
    Decimal,
    Percent,
    Text,
    Choice,
    ItemChoice,
    MultiChoice
}

public enum QueueMode
{
    Skip,
    WaitForDuration,
    WaitIndefinitely
}

public enum GameResultStatus
{
    Executed,
    Deferred,
    Rejected,
    Uncertain
}

public enum DispatchState
{
    Queued,
    Sent,
    Executed,
    Deferred,
    Rejected,
    Uncertain,
    Skipped,
    Cancelled,
    Expired,
    Duplicate,
    NoRule
}

public sealed class ItemSpawnEntry
{
    public string ItemId { get; set; } = "medkit";
    public int Count { get; set; } = 1;

    public ItemSpawnEntry Clone() => new() { ItemId = ItemId, Count = Count };
}

public sealed class SpawnGroupEntry
{
    public string VariantId { get; set; } = string.Empty;
    public string Strength { get; set; } = "medium";
    public int Count { get; set; } = 1;

    public SpawnGroupEntry Clone() => new() { VariantId = VariantId, Strength = Strength, Count = Count };
}

public static class ItemBundleCodec
{
    public const string HandlerId = "spawn_items";
    public const string ParameterKey = "items";
    // There is deliberately no row-count limit. The serialized payload is
    // bounded instead so it always fits the existing 16 KiB IPC message.
    // Separators are percent-encoded twice by the existing protocol. Four KiB
    // is conservative even for many one-character rows and stays below 16 KiB.
    public const int MaximumSerializedLength = 4_000;

    public static string Serialize(IEnumerable<ItemSpawnEntry> entries) => string.Join(';', entries.Select(entry =>
        $"{entry.ItemId.ToLowerInvariant()}*{entry.Count.ToString(CultureInfo.InvariantCulture)}"));
}

public static class SpawnGroupBundleCodec
{
    public const string ParameterKey = "groups";
    public const int MaximumSerializedLength = 4_000;

    public static bool Supports(string handlerId) => handlerId is "spawn_mutants" or "spawn_npcs";

    public static string VariantParameterKey(string handlerId) => handlerId switch
    {
        "spawn_mutants" => "species",
        "spawn_npcs" => "faction",
        _ => throw new ArgumentOutOfRangeException(nameof(handlerId), handlerId, "Unsupported spawn-group handler.")
    };

    public static string Serialize(IEnumerable<SpawnGroupEntry> entries) => string.Join(';', entries.Select(entry =>
        $"{entry.VariantId.ToLowerInvariant()}*{entry.Strength.ToLowerInvariant()}*{entry.Count.ToString(CultureInfo.InvariantCulture)}"));
}

public sealed class ParameterDefinition
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ParameterKind Kind { get; set; }
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public List<string> Options { get; set; } = [];
    public Dictionary<string, List<string>> OptionGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OptionLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DefaultValue { get; set; } = string.Empty;
    /// <summary>
    /// Optional live-catalog identifier. The desktop keeps the opaque selected
    /// section ids in Parameters while the installed game manifest supplies the
    /// current Options/labels. Empty means an ordinary static choice.
    /// </summary>
    public string CatalogKind { get; set; } = string.Empty;
    public int MinimumSelections { get; set; }

    public ParameterDefinition Clone() => new()
    {
        Key = Key,
        DisplayName = DisplayName,
        Kind = Kind,
        Minimum = Minimum,
        Maximum = Maximum,
        Options = [.. Options],
        OptionGroups = OptionGroups.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase),
        OptionLabels = new Dictionary<string, string>(OptionLabels, StringComparer.OrdinalIgnoreCase),
        DefaultValue = DefaultValue,
        CatalogKind = CatalogKind,
        MinimumSelections = MinimumSelections
    };

    public string? Validate(string? value)
    {
        value ??= string.Empty;
        if (!Validation.ParameterKey().IsMatch(Key))
        {
            return $"Недопустимый ключ параметра: {Key}";
        }

        if (Kind == ParameterKind.Boolean)
        {
            return value is "true" or "false" or "1" or "0"
                ? null
                : $"Параметр «{DisplayName}» должен быть логическим значением";
        }

        if (Kind == ParameterKind.Text)
        {
            if (value.Any(char.IsControl))
            {
                return $"Параметр «{DisplayName}» содержит управляющие символы";
            }
            if (Minimum is { } minimumLength && value.Length < minimumLength)
            {
                return $"Параметр «{DisplayName}» должен содержать не меньше {minimumLength} символов";
            }
            if (Maximum is { } maximumLength && value.Length > maximumLength)
            {
                return $"Параметр «{DisplayName}» должен содержать не больше {maximumLength} символов";
            }
            return null;
        }

        if (Kind is ParameterKind.Choice or ParameterKind.ItemChoice)
        {
            return Options.Contains(value, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"Параметр «{DisplayName}» должен быть одним из: {string.Join(", ", Options)}";
        }

        if (Kind == ParameterKind.MultiChoice)
        {
            var selected = SelectedOptions(value);
            if (selected.Count < Math.Max(0, MinimumSelections))
            {
                return $"Для параметра «{DisplayName}» нужно выбрать хотя бы {Math.Max(0, MinimumSelections)}";
            }
            if (selected.Count != selected.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                return $"Параметр «{DisplayName}» содержит повторяющиеся значения";
            }
            var unknown = selected.FirstOrDefault(option =>
                !Options.Contains(option, StringComparer.OrdinalIgnoreCase));
            return unknown is null
                ? null
                : $"Параметр «{DisplayName}» содержит недопустимое значение: {unknown}";
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return $"Параметр «{DisplayName}» должен быть числом";
        }

        if (Kind == ParameterKind.Integer && decimal.Truncate(number) != number)
        {
            return $"Параметр «{DisplayName}» должен быть целым числом";
        }

        if (Minimum is { } minimum && number < minimum)
        {
            return $"Параметр «{DisplayName}» не может быть меньше {minimum}";
        }

        if (Maximum is { } maximum && number > maximum)
        {
            return $"Параметр «{DisplayName}» не может быть больше {maximum}";
        }

        return null;
    }

    public IReadOnlyList<string> SelectedOptions(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}

public sealed class TriggerRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public TriggerComparator Comparator { get; set; } = TriggerComparator.Exact;
    public decimal Amount { get; set; } = 1m;
    public string Currency { get; set; } = "RUB";

    public TriggerRule Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Comparator = Comparator,
        Amount = Amount,
        Currency = Currency
    };
}

public sealed class ActionDefinition
{
    public string Id { get; set; } = NewInstanceId();
    public string HandlerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool HasCustomDisplayName { get; set; }
    public string Description { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string ScriptRelativePath { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int CooldownSeconds { get; set; } = 1;
    public List<ParameterDefinition> ParameterDefinitions { get; set; } = [];
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ItemSpawnEntry> ItemSpawns { get; set; } = [];
    public List<SpawnGroupEntry> SpawnGroups { get; set; } = [];
    public List<TriggerRule> Triggers { get; set; } = [];

    public ActionDefinition Clone() => new()
    {
        Id = Id,
        HandlerId = HandlerId,
        DisplayName = DisplayName,
        HasCustomDisplayName = HasCustomDisplayName,
        Description = Description,
        CategoryId = CategoryId,
        ScriptRelativePath = ScriptRelativePath,
        IsActive = IsActive,
        CooldownSeconds = CooldownSeconds,
        ParameterDefinitions = ParameterDefinitions.Select(definition => definition.Clone()).ToList(),
        Parameters = new Dictionary<string, string>(Parameters, StringComparer.OrdinalIgnoreCase),
        ItemSpawns = ItemSpawns.Select(entry => entry.Clone()).ToList(),
        SpawnGroups = SpawnGroups.Select(entry => entry.Clone()).ToList(),
        Triggers = Triggers.Select(trigger => trigger.Clone()).ToList()
    };

    public ActionDefinition CloneAsNewInstance()
    {
        var clone = Clone();
        clone.Id = NewInstanceId();
        clone.IsActive = true;
        foreach (var trigger in clone.Triggers)
        {
            trigger.Id = Guid.NewGuid().ToString("N");
        }
        return clone;
    }

    public static string NewInstanceId() => Guid.NewGuid().ToString("N");

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Validation.InstanceId().IsMatch(Id))
        {
            errors.Add($"Недопустимый идентификатор экземпляра действия: {Id}");
        }

        if (!Validation.ActionId().IsMatch(HandlerId))
        {
            errors.Add($"Недопустимый идентификатор игрового обработчика: {HandlerId}");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            errors.Add($"У действия {Id} отсутствует название");
        }

        if (CooldownSeconds < 0 || CooldownSeconds > 86_400)
        {
            errors.Add($"Пауза после срабатывания для «{DisplayName}» должна быть от 0 до 86 400 секунд");
        }

        foreach (var definition in ParameterDefinitions)
        {
            Parameters.TryGetValue(definition.Key, out var value);
            var error = definition.Validate(value ?? definition.DefaultValue);
            if (error is not null)
            {
                errors.Add(error);
            }
        }

        if (string.Equals(HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal))
        {
            if (ItemSpawns.Count == 0)
            {
                errors.Add($"{DisplayName}: добавьте хотя бы один предмет");
            }

            var itemDefinition = ParameterDefinitions.FirstOrDefault(definition =>
                string.Equals(definition.Key, "item", StringComparison.OrdinalIgnoreCase));
            for (var index = 0; index < ItemSpawns.Count; index++)
            {
                var entry = ItemSpawns[index];
                if (!Validation.ItemSection().IsMatch(entry.ItemId) ||
                    itemDefinition?.Validate(entry.ItemId) is not null)
                {
                    errors.Add($"{DisplayName}: недопустимый предмет в строке {index + 1}");
                }
                if (entry.Count is < 1 or > 50)
                {
                    errors.Add($"{DisplayName}: количество в строке {index + 1} должно быть от 1 до 50");
                }
            }

            if (ItemBundleCodec.Serialize(ItemSpawns).Length > ItemBundleCodec.MaximumSerializedLength)
            {
                errors.Add($"{DisplayName}: набор слишком велик для передачи игре; удалите несколько строк");
            }
        }

        if (SpawnGroupBundleCodec.Supports(HandlerId))
        {
            if (SpawnGroups.Count == 0)
            {
                errors.Add($"{DisplayName}: добавьте хотя бы одну группу");
            }

            var variantKey = SpawnGroupBundleCodec.VariantParameterKey(HandlerId);
            var variantDefinition = ParameterDefinitions.FirstOrDefault(definition =>
                string.Equals(definition.Key, variantKey, StringComparison.OrdinalIgnoreCase));
            var strengthDefinition = ParameterDefinitions.FirstOrDefault(definition =>
                string.Equals(definition.Key, "strength", StringComparison.OrdinalIgnoreCase));
            if (variantDefinition is null || strengthDefinition is null)
            {
                errors.Add($"{DisplayName}: схема составного спавна неполна");
            }
            for (var index = 0; index < SpawnGroups.Count; index++)
            {
                var entry = SpawnGroups[index];
                if (variantDefinition is not null && variantDefinition.Validate(entry.VariantId) is not null)
                {
                    errors.Add($"{DisplayName}: недопустимый состав в строке {index + 1}");
                }
                if (strengthDefinition is not null && strengthDefinition.Validate(entry.Strength) is not null)
                {
                    errors.Add($"{DisplayName}: недопустимая сила в строке {index + 1}");
                }
                if (entry.Count is < 1 or > 99)
                {
                    errors.Add($"{DisplayName}: количество в строке {index + 1} должно быть от 1 до 99");
                }
            }

            if (SpawnGroupBundleCodec.Serialize(SpawnGroups).Length > SpawnGroupBundleCodec.MaximumSerializedLength)
            {
                errors.Add($"{DisplayName}: список групп слишком велик для передачи игре; удалите несколько строк");
            }
        }

        ValidateOrderedRange(errors, "minimum_count", "maximum_count", "количества");
        ValidateOrderedRange(errors, "minimum_meters", "maximum_meters", "дальности");

        foreach (var trigger in Triggers)
        {
            errors.AddRange(Validation.ValidateTrigger(trigger).Select(error => $"{DisplayName}: {error}"));
        }

        var duplicates = Triggers.Where(rule => rule.Enabled)
            .Where(rule => Money.TryNormalizeCurrency(rule.Currency, out _))
            .GroupBy(rule => new
            {
                rule.Comparator,
                rule.Amount,
                Currency = Money.TryNormalizeCurrency(rule.Currency, out var currency) ? currency : string.Empty
            })
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicates)
        {
            errors.Add($"{DisplayName}: повторяющееся условие {duplicate.Key.Comparator} {duplicate.Key.Amount} {duplicate.Key.Currency}");
        }

        return errors;
    }

    private void ValidateOrderedRange(
        ICollection<string> errors,
        string minimumKey,
        string maximumKey,
        string rangeName)
    {
        if (!ParameterDefinitions.Any(definition =>
                string.Equals(definition.Key, minimumKey, StringComparison.OrdinalIgnoreCase)) ||
            !ParameterDefinitions.Any(definition =>
                string.Equals(definition.Key, maximumKey, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var minimumText = Parameters.TryGetValue(minimumKey, out var minimumValue)
            ? minimumValue
            : ParameterDefinitions.First(definition =>
                string.Equals(definition.Key, minimumKey, StringComparison.OrdinalIgnoreCase)).DefaultValue;
        var maximumText = Parameters.TryGetValue(maximumKey, out var maximumValue)
            ? maximumValue
            : ParameterDefinitions.First(definition =>
                string.Equals(definition.Key, maximumKey, StringComparison.OrdinalIgnoreCase)).DefaultValue;
        if (decimal.TryParse(minimumText, NumberStyles.Number, CultureInfo.InvariantCulture, out var minimum) &&
            decimal.TryParse(maximumText, NumberStyles.Number, CultureInfo.InvariantCulture, out var maximum) &&
            minimum > maximum)
        {
            errors.Add($"{DisplayName}: минимум {rangeName} не может быть больше максимума");
        }
    }

    public IReadOnlyDictionary<string, string> EffectiveParameters()
    {
        if (string.Equals(HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal) && ItemSpawns.Count > 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ItemBundleCodec.ParameterKey] = ItemBundleCodec.Serialize(ItemSpawns)
            };
        }

        if (SpawnGroupBundleCodec.Supports(HandlerId) && SpawnGroups.Count > 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SpawnGroupBundleCodec.ParameterKey] = SpawnGroupBundleCodec.Serialize(SpawnGroups)
            };
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in ParameterDefinitions)
        {
            result[definition.Key] = Parameters.TryGetValue(definition.Key, out var value)
                ? value
                : definition.DefaultValue;
        }

        return result;
    }
}

public sealed class DonationEvent
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "RUB";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    public long AmountMinor => Money.ToMinor(Amount, Currency);
}

public sealed class RuleMatch
{
    public required ActionDefinition Action { get; init; }
    public required TriggerRule Trigger { get; init; }
    public ActionDefinition? SourceAction { get; init; }
    public string? HistoryActionId { get; init; }
}

public sealed class GameCommand
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public string DonationId { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "RUB";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GameExecutionResult
{
    public string CommandId { get; set; } = string.Empty;
    public GameResultStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QueuePolicy
{
    public QueueMode Mode { get; set; } = QueueMode.WaitIndefinitely;
    public int WaitMinutes { get; set; } = 5;
    public int MaximumQueuedEvents { get; set; } = 100;

    public QueuePolicy Clone() => new()
    {
        Mode = Mode,
        WaitMinutes = WaitMinutes,
        MaximumQueuedEvents = MaximumQueuedEvents
    };

    public TimeSpan? GetLifetime() => Mode switch
    {
        // Skip is decided from transport readiness during Drain; a ready game must still execute immediately.
        QueueMode.Skip => null,
        QueueMode.WaitForDuration => TimeSpan.FromMinutes(Math.Clamp(WaitMinutes, 1, 24 * 60)),
        QueueMode.WaitIndefinitely => null,
        _ => TimeSpan.Zero
    };
}

public sealed class EventHistoryEntry
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DonationCreatedAt { get; set; }
    public string DonationId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string Donor { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string HandlerId { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public DispatchState State { get; set; }
    public string Detail { get; set; } = string.Empty;
    public bool CanRetryManually { get; set; }
}

public sealed class ProcessedDonationRecord
{
    public string DonationId { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public DispatchState State { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class Money
{
    private static readonly IReadOnlyDictionary<string, int> KnownMinorDigits =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["RUB"] = 2,
            ["USD"] = 2,
            ["EUR"] = 2,
            ["UAH"] = 2,
            ["KZT"] = 2,
            ["BYN"] = 2,
            ["GBP"] = 2
        };

    public static string NormalizeCurrency(string currency)
    {
        var normalized = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!Validation.Currency().IsMatch(normalized))
        {
            throw new ArgumentException("Код валюты должен состоять ровно из трёх латинских букв", nameof(currency));
        }

        return normalized;
    }

    public static int MinorDigits(string currency) => KnownMinorDigits.TryGetValue(NormalizeCurrency(currency), out var digits) ? digits : 2;

    public static bool TryNormalizeCurrency(string? currency, out string normalized)
    {
        try
        {
            normalized = NormalizeCurrency(currency ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    public static long ToMinor(decimal amount, string currency)
    {
        var factor = DecimalPower(10, MinorDigits(currency));
        return checked(decimal.ToInt64(decimal.Round(amount * factor, 0, MidpointRounding.AwayFromZero)));
    }

    public static decimal FromMinor(long minor, string currency)
    {
        var factor = DecimalPower(10, MinorDigits(currency));
        return minor / factor;
    }

    private static decimal DecimalPower(decimal value, int exponent)
    {
        var result = 1m;
        for (var index = 0; index < exponent; index++)
        {
            result *= value;
        }

        return result;
    }
}

public static partial class Validation
{
    public const int UserPresetNameMaxLength = 80;

    [GeneratedRegex("^[a-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    public static partial Regex ActionId();

    [GeneratedRegex("^[a-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    public static partial Regex InstanceId();

    [GeneratedRegex("^[a-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    public static partial Regex ParameterKey();

    [GeneratedRegex("^[a-z0-9_.@-]{1,128}$", RegexOptions.CultureInvariant)]
    public static partial Regex ItemSection();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    public static partial Regex Currency();

    public static bool TryNormalizeUserPresetName(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim();
        return normalized.Length is > 0 and <= UserPresetNameMaxLength &&
            !normalized.Any(char.IsControl);
    }

    public static IReadOnlyList<string> ValidateTrigger(TriggerRule trigger)
    {
        var errors = new List<string>();
        if (trigger.Amount < 0m || trigger.Amount > 1_000_000_000m)
        {
            errors.Add("сумма должна быть от 0 до 1 000 000 000");
        }

        try
        {
            trigger.Currency = Money.NormalizeCurrency(trigger.Currency);
        }
        catch (ArgumentException exception)
        {
            errors.Add(exception.Message);
        }

        if (string.IsNullOrWhiteSpace(trigger.Id))
        {
            errors.Add("отсутствует идентификатор условия");
        }

        return errors;
    }
}

public static class RuleMatcher
{
    public static RuleMatch? Match(DonationEvent donation, IEnumerable<ActionDefinition> actions)
        => MatchAll(donation, actions).FirstOrDefault();

    public static IReadOnlyList<RuleMatch> MatchAll(DonationEvent donation, IEnumerable<ActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(donation);
        ArgumentNullException.ThrowIfNull(actions);

        var currency = Money.NormalizeCurrency(donation.Currency);
        var candidates = actions
            .Where(action => action.IsActive)
            .SelectMany(action => action.Triggers
                .Where(trigger => trigger.Enabled)
                .Where(trigger => Money.TryNormalizeCurrency(trigger.Currency, out var triggerCurrency) && string.Equals(triggerCurrency, currency, StringComparison.Ordinal))
                .Where(trigger => trigger.Comparator == TriggerComparator.Exact
                    ? donation.Amount == trigger.Amount
                    : donation.Amount >= trigger.Amount)
                .Select(trigger => new RuleMatch { Action = action, Trigger = trigger }))
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var winningComparator = candidates.Any(match => match.Trigger.Comparator == TriggerComparator.Exact)
            ? TriggerComparator.Exact
            : TriggerComparator.GreaterOrEqual;
        var comparatorMatches = candidates
            .Where(match => match.Trigger.Comparator == winningComparator)
            .ToList();
        var winningAmount = winningComparator == TriggerComparator.Exact
            ? donation.Amount
            : comparatorMatches.Max(match => match.Trigger.Amount);

        return comparatorMatches
            .Where(match => match.Trigger.Amount == winningAmount)
            .ToList();
    }
}

public static class CatalogValidation
{
    public static IReadOnlyList<string> Validate(IEnumerable<ActionDefinition> actions)
    {
        var materialized = actions.ToList();
        var errors = materialized.SelectMany(action => action.Validate()).ToList();
        var duplicateInstanceIds = materialized
            .Where(action => action.IsActive && !string.IsNullOrWhiteSpace(action.Id))
            .GroupBy(action => action.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateInstanceIds)
        {
            errors.Add($"Идентификатор экземпляра {duplicate.Key} назначен нескольким активным действиям");
        }
        return errors;
    }
}

public static class DefaultActionCatalog
{
    public const string RandomAllSelectionParameterKey = "included_presets";
    private const string AddonScriptRoot = "ixr_addons\\zzzzzzzzzzzzzzzz_FOR_STREAMERS_pf-donation-alerts\\scripts\\";

    public static List<ActionDefinition> Create() => CreateDefaultActiveInstances();

    public static List<ActionDefinition> CreateDefaultActiveInstances()
    {
        var presets = CreatePresets().ToDictionary(action => action.HandlerId, StringComparer.Ordinal);
        return
        [
            Activate(presets["add_radiation"], Exact(1m)),
            Activate(presets["drop_active_weapon"], Exact(2m)),
            Activate(presets["spawn_mutants"], Exact(3m))
        ];
    }

    public static List<ActionDefinition> CreatePresets() => ConfigureRandomAllPresetSelection(
    [
        new()
        {
            Id = "add_radiation",
            HandlerId = "add_radiation",
            DisplayName = "Добавить радиацию",
            Description = "Изменяет уровень радиационного заражения актёра.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_add_radiation.script",
            ParameterDefinitions =
            [
                Number("percent", "Изменение, %", ParameterKind.Percent, -100m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["percent"] = "25" }
        },
        new()
        {
            Id = "drop_active_weapon",
            HandlerId = "drop_active_weapon",
            DisplayName = "Выбить оружие",
            Description = "Ждёт появления обычного оружия в руках указанное время, затем выбрасывает его.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_drop_active_weapon.script",
            ParameterDefinitions =
            [
                Number("wait_seconds", "Ждать оружие в руках, сек.", ParameterKind.Integer, 0m, 60m, "10")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["wait_seconds"] = "10" }
        },
        new()
        {
            Id = "spawn_mutants",
            HandlerId = "spawn_mutants",
            DisplayName = "Заспавнить мутантов",
            Description = "Создаёт одну или несколько настроенных групп мутантов из безопасного белого списка за одно срабатывание. Большие стаи могут сильно нагрузить игру.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_spawn_mutants.script",
            ParameterDefinitions =
            [
                Choice("species", "Вид", "dog",
                    "dog", "boar", "flesh", "tushkano", "pseudodog", "psy_dog",
                    "bloodsucker", "snork", "burer", "controller", "poltergeist",
                    "chimera", "pseudogiant"),
                Choice("strength", "Сила", "medium", "weak", "medium", "strong"),
                Number("count", "Количество (1–99)", ParameterKind.Integer, 1m, 99m, "3")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["species"] = "dog",
                ["strength"] = "medium",
                ["count"] = "3"
            },
            SpawnGroups = [new SpawnGroupEntry { VariantId = "dog", Strength = "medium", Count = 3 }]
        },
        new()
        {
            Id = "spawn_npcs",
            HandlerId = "spawn_npcs",
            DisplayName = "Заспавнить NPC",
            Description = "Создаёт одну или несколько групп NPC выбранных группировок и силы за одно срабатывание. Большие отряды могут сильно нагрузить игру.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_spawn_npcs.script",
            ParameterDefinitions =
            [
                Choice("faction", "Группировка", "bandit", "stalker", "bandit", "duty", "freedom", "military", "monolith", "killer"),
                Choice("strength", "Ранг", "medium", "weak", "medium", "strong"),
                Number("count", "Количество (1–99)", ParameterKind.Integer, 1m, 99m, "1")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["faction"] = "bandit",
                ["strength"] = "medium",
                ["count"] = "1"
            },
            SpawnGroups = [new SpawnGroupEntry { VariantId = "bandit", Strength = "medium", Count = 1 }]
        },
        new()
        {
            Id = "spawn_random_hostile_squad",
            HandlerId = "spawn_random_hostile_squad",
            DisplayName = "Случайный вражеский отряд",
            Description = "Создаёт независимых NPC случайной враждебной игроку группировки без simulation squad.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_spawn_random_hostile_squad.script",
            ParameterDefinitions =
            [
                Number("minimum_count", "Минимум бойцов", ParameterKind.Integer, 1m, 99m, "1"),
                Number("maximum_count", "Максимум бойцов (до 99)", ParameterKind.Integer, 1m, 99m, "10")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["minimum_count"] = "1",
                ["maximum_count"] = "10"
            }
        },
        new()
        {
            Id = "spawn_random_mutant_pack",
            HandlerId = "spawn_random_mutant_pack",
            DisplayName = "Случайная стая мутантов",
            Description = "Случайно выбирает безопасный вид и силу, затем создаёт независимых мутантов без simulation squad.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_spawn_random_mutant_pack.script",
            ParameterDefinitions =
            [
                Number("minimum_count", "Минимум мутантов", ParameterKind.Integer, 1m, 99m, "1"),
                Number("maximum_count", "Максимум мутантов (до 99)", ParameterKind.Integer, 1m, 99m, "10")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["minimum_count"] = "1",
                ["maximum_count"] = "10"
            }
        },
        new()
        {
            Id = "change_health",
            HandlerId = "change_health",
            DisplayName = "Изменить здоровье",
            Description = "Добавляет или отнимает процент здоровья.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_change_health.script",
            ParameterDefinitions =
            [
                Number("percent", "Изменение, %", ParameterKind.Percent, -100m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["percent"] = "25" }
        },
        new()
        {
            Id = "god_mode",
            HandlerId = "god_mode",
            DisplayName = "Год-мод",
            Description = "Включает штатный g_god на выбранное число секунд, затем выключает его.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_invulnerability.script",
            ParameterDefinitions =
            [
                Number("duration_seconds", "Длительность неуязвимости, сек.", ParameterKind.Integer, 5m, 600m, "30")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["duration_seconds"] = "30" }
        },
        new()
        {
            Id = "change_needs",
            HandlerId = "change_needs",
            DisplayName = "Изменить голод и жажду",
            Description = "Положительное значение утоляет голод или жажду, отрицательное усиливает, ноль не меняет шкалу.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_change_needs.script",
            ParameterDefinitions =
            [
                Number("hunger_percent", "Голод, % (+ утолить / − усилить)", ParameterKind.Percent, -100m, 100m, "25"),
                Number("thirst_percent", "Жажда, % (+ утолить / − усилить)", ParameterKind.Percent, -100m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["hunger_percent"] = "25",
                ["thirst_percent"] = "25"
            }
        },
        new()
        {
            Id = "spawn_items",
            HandlerId = "spawn_items",
            DisplayName = "Выдать предметы в инвентарь",
            Description = "Добавляет выбранные предметы из безопасных категорий игрового спавнера.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_spawn_items.script",
            ParameterDefinitions =
            [
                GroupedItem(
                    "item",
                    "Предмет",
                    "medkit",
                    ("medicine", new[] { "bandage", "medkit", "antirad" }),
                    ("food", new[] { "bread", "conserva", "vodka" }),
                    ("ammo_explosives", new[] { "ammo_9x18_fmj", "ammo_5.45x39_fmj", "ammo_12x70_buck" }),
                    ("fuel", new[] { "pf_vehicle_fuel_canister" })),
                Number("count", "Количество", ParameterKind.Integer, 1m, 50m, "1")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["item"] = "medkit",
                ["count"] = "1"
            },
            ItemSpawns = [new ItemSpawnEntry { ItemId = "medkit", Count = 1 }]
        },
        new()
        {
            Id = "spawn_anomaly",
            HandlerId = "spawn_anomaly",
            DisplayName = "Заспавнить аномалию",
            Description = "Создаёт временную аномалию на выбранной дальности; 0 м — прямо под персонажем.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_spawn_anomaly.script",
            ParameterDefinitions =
            [
                Choice("type", "Тип", "gravitational", "gravitational", "electric", "acidic", "thermal"),
                Choice("strength", "Сила", "medium", "weak", "medium", "strong"),
                Number("distance_meters", "Дальность, м", ParameterKind.Integer, 0m, 100m, "20"),
                Number("lifetime_seconds", "Время жизни, сек.", ParameterKind.Integer, 10m, 300m, "60")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "gravitational",
                ["strength"] = "medium",
                ["distance_meters"] = "20",
                ["lifetime_seconds"] = "60"
            }
        },
        new()
        {
            Id = "trigger_emission",
            HandlerId = "trigger_emission",
            DisplayName = "Вызвать выброс",
            Description = "Запускает штатный выброс на текущей локации, если он доступен и ещё не идёт.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_trigger_emission.script",
        },
        new()
        {
            Id = "sleep_hours",
            HandlerId = "sleep_hours",
            DisplayName = "Заставить персонажа поспать",
            Description = "Запускает штатное затемнение и пробуждение, сдвигает время и применяет обычные последствия сна.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_sleep_hours.script",
            ParameterDefinitions =
            [
                Number("hours", "Продолжительность сна, часов", ParameterKind.Integer, 1m, 24m, "6")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["hours"] = "6" }
        },
        new()
        {
            Id = "change_money",
            HandlerId = "change_money",
            DisplayName = "Изменить деньги",
            Description = "Положительная сумма добавляет игровые деньги, отрицательная — отнимает.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_change_money.script",
            ParameterDefinitions =
            [
                Number("amount", "Сумма, ₽ (+/−)", ParameterKind.Integer, -1_000_000m, 1_000_000m, "1000")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["amount"] = "1000" }
        },
        new()
        {
            Id = "get_drunk",
            HandlerId = "get_drunk",
            DisplayName = "Наложить эффект опьянения",
            Description = "Включает штатный эффект опьянения на выбранное время без предметов.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_get_drunk.script",
            ParameterDefinitions =
            [
                Number("duration_seconds", "Длительность опьянения, сек.", ParameterKind.Integer, 5m, 300m, "30")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["duration_seconds"] = "30" }
        },
        new()
        {
            Id = "drop_outfit",
            HandlerId = "drop_outfit",
            DisplayName = "Бросить экипировку",
            Description = "Снимает броню, шлем или оба предмета и оставляет их на земле.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_drop_outfit.script",
            ParameterDefinitions =
            [
                Choice("target", "Что снять", "outfit", "outfit", "helmet", "both")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["target"] = "outfit" }
        },
        new()
        {
            Id = "damage_equipment",
            HandlerId = "damage_equipment",
            DisplayName = "Повредить экипировку",
            Description = "Уменьшает текущее состояние брони, шлема или обоих предметов на выбранный процент.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_damage_equipment.script",
            ParameterDefinitions =
            [
                Choice("target", "Что повредить", "outfit", "outfit", "helmet", "both"),
                Number("percent", "Уменьшить состояние на, %", ParameterKind.Percent, 1m, 100m, "25")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["target"] = "outfit",
                ["percent"] = "25"
            }
        },
        new()
        {
            Id = "drop_random_items",
            HandlerId = "drop_random_items",
            DisplayName = "Выбросить случайный предмет",
            Description = "Выбрасывает указанное число случайных безопасных предметов из любых типов и стаков; если их меньше — выбрасывает все доступные.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_drop_random_items.script",
            ParameterDefinitions =
            [
                Number("count", "Количество предметов", ParameterKind.Integer, 1m, 10m, "1")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["count"] = "1" }
        },
        new()
        {
            Id = "teleport_random",
            HandlerId = "teleport_random",
            DisplayName = "Случайный телепорт",
            Description = "Перемещает персонажа на безопасную вершину текущей локации.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_teleport_random.script",
            ParameterDefinitions =
            [
                Number("minimum_meters", "Минимум, м", ParameterKind.Integer, 10m, 2_000m, "25"),
                Number("maximum_meters", "Максимум, м", ParameterKind.Integer, 10m, 2_000m, "100")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["minimum_meters"] = "25",
                ["maximum_meters"] = "100"
            }
        },
        new()
        {
            Id = "spawn_vehicle",
            HandlerId = "spawn_vehicle",
            DisplayName = "Заспавнить транспорт",
            Description = "Создаёт одну машину на ближайшем свободном месте: сначала в пределах 10 м перед персонажем, затем ищет подходящую площадку до 100 м. Бензин: от 1 до 10 литров. У legacy-транспорта сохраняются штатные расход и заправка.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_vehicle.script",
            ParameterDefinitions =
            [
                Choice("vehicle", "Транспорт", "veh_dcp_niva",
                    "veh_dcp_niva", "veh_dcp_niva_green", "veh_dcp_zaz968_2", "veh_dcp_lada_2101",
                    "veh_dcp_lada_dead", "veh_dcp_uaz_van", "veh_dcp_uaz_van_broken", "veh_dcp_uaz_broken",
                    "veh_dcp_gaz24", "veh_dcp_moskvich_412", "veh_dcp_raf", "veh_dcp_moskvich_2715",
                    "veh_dcp_uaz", "veh_dcp_zil", "veh_dcp_zil131", "veh_dcp_mercedes_w123",
                    "veh_dcp_lada_2107", "veh_dcp_lada_2108", "veh_dcp_niva_2329", "veh_dcp_brdm",
                    "veh_dcp_btr", "veh_dcp_kamaz_army_tent", "veh_dcp_kamaz_army", "veh_btr",
                    "veh_kamaz", "veh_niva_g", "veh_niva_w", "veh_tr13",
                    "veh_uaz", "veh_zaz"),
                Number("fuel_level", "Бензин, литры (1–10)", ParameterKind.Integer, 1m, 10m, "5")
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["vehicle"] = "veh_dcp_niva", ["fuel_level"] = "5" }
        },
        new()
        {
            Id = "explode_nearest_vehicle",
            HandlerId = "explode_nearest_vehicle",
            DisplayName = "Взорвать ближайший транспорт",
            Description = "Взрывает одну ближайшую несюжетную машину в заданном радиусе до 2000 м. Взрыв может ранить персонажа и уничтожить вещи в багажнике.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_vehicle.script",
            ParameterDefinitions = [Number("radius_meters", "Радиус поиска, м", ParameterKind.Integer, 1m, 2_000m, "5")],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["radius_meters"] = "5" }
        },
        new()
        {
            Id = DonationDispatcher.RandomActiveHandlerId,
            HandlerId = DonationDispatcher.RandomActiveHandlerId,
            DisplayName = "Рандомный по активным пресетам",
            Description = "При срабатывании выбирает один из остальных доступных активных вариантов.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_random_active.script",
        },
        new()
        {
            Id = DonationDispatcher.RandomAllPresetsHandlerId,
            HandlerId = DonationDispatcher.RandomAllPresetsHandlerId,
            DisplayName = "Рандом по ВСЕМ пресетам",
            Description = "Выбирает эффект из всего каталога и создаёт безопасные случайные параметры; выдача предметов исключена.",
            ScriptRelativePath = AddonScriptRoot + "pf_donation_action_random_all_presets.script",
        }
    ]);

    public static bool IsRandomAllEligiblePreset(ActionDefinition preset) =>
        !string.Equals(preset.HandlerId, "spawn_items", StringComparison.Ordinal) &&
        // Vehicle effects remain an explicit choice; do not silently widen existing random rules.
        !string.Equals(preset.HandlerId, "spawn_vehicle", StringComparison.Ordinal) &&
        !string.Equals(preset.HandlerId, "explode_nearest_vehicle", StringComparison.Ordinal) &&
        !string.Equals(preset.HandlerId, DonationDispatcher.RandomActiveHandlerId, StringComparison.Ordinal) &&
        !string.Equals(preset.HandlerId, DonationDispatcher.RandomAllPresetsHandlerId, StringComparison.Ordinal) &&
        preset.ParameterDefinitions.All(definition =>
            definition.Kind is not ParameterKind.ItemChoice and not ParameterKind.MultiChoice);

    private static List<ActionDefinition> ConfigureRandomAllPresetSelection(List<ActionDefinition> catalog)
    {
        RefreshRandomAllPresetSelection(catalog);
        return catalog;
    }

    public static void RefreshRandomAllPresetSelection(IList<ActionDefinition> catalog)
    {
        var randomAll = catalog.FirstOrDefault(action =>
            string.Equals(action.HandlerId, DonationDispatcher.RandomAllPresetsHandlerId, StringComparison.Ordinal));
        if (randomAll is null)
        {
            return;
        }

        var eligible = catalog.Where(IsRandomAllEligiblePreset).ToList();
        var options = eligible.Select(action => action.HandlerId).ToList();
        var defaultValue = string.Join(',', options);
        var definition = new ParameterDefinition
        {
            Key = RandomAllSelectionParameterKey,
            DisplayName = "Участвуют в случайном выборе",
            Kind = ParameterKind.MultiChoice,
            Options = options,
            OptionLabels = eligible.ToDictionary(
                action => action.HandlerId,
                action => action.DisplayName,
                StringComparer.OrdinalIgnoreCase),
            DefaultValue = defaultValue
        };
        randomAll.ParameterDefinitions.RemoveAll(existing =>
            string.Equals(existing.Key, RandomAllSelectionParameterKey, StringComparison.OrdinalIgnoreCase));
        randomAll.ParameterDefinitions.Add(definition);
        randomAll.Parameters[RandomAllSelectionParameterKey] = defaultValue;
    }

    private static ActionDefinition Activate(ActionDefinition preset, params TriggerRule[] triggers)
    {
        var instance = preset.Clone();
        instance.Id = ActionDefinition.NewInstanceId();
        instance.IsActive = true;
        instance.Triggers = [.. triggers];
        return instance;
    }

    private static TriggerRule Exact(decimal amount) => new()
    {
        Comparator = TriggerComparator.Exact,
        Amount = amount,
        Currency = "RUB"
    };

    private static ParameterDefinition Number(string key, string name, ParameterKind kind, decimal min, decimal max, string defaultValue) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = kind,
        Minimum = min,
        Maximum = max,
        DefaultValue = defaultValue
    };

    private static ParameterDefinition Choice(string key, string name, string defaultValue, params string[] options) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = ParameterKind.Choice,
        Options = [.. options],
        DefaultValue = defaultValue
    };

    private static ParameterDefinition Item(string key, string name, string defaultValue, params string[] options) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = ParameterKind.ItemChoice,
        Options = [.. options],
        DefaultValue = defaultValue
    };

    private static ParameterDefinition GroupedItem(
        string key,
        string name,
        string defaultValue,
        params (string Group, string[] Options)[] groups) => new()
    {
        Key = key,
        DisplayName = name,
        Kind = ParameterKind.ItemChoice,
        Options = groups.SelectMany(group => group.Options).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        OptionGroups = groups.ToDictionary(
            group => group.Group,
            group => group.Options.ToList(),
            StringComparer.OrdinalIgnoreCase),
        DefaultValue = defaultValue
    };
}

public sealed class CurrencyCatalog
{
    private static readonly string[] Defaults = ["RUB", "USD", "EUR", "UAH", "KZT", "BYN", "BRL", "TRY"];
    private readonly HashSet<string> _currencies = new(Defaults, StringComparer.Ordinal);

    public ReadOnlyCollection<string> All => _currencies.OrderBy(value => value, StringComparer.Ordinal).ToList().AsReadOnly();

    public bool AddObserved(string currency) => _currencies.Add(Money.NormalizeCurrency(currency));
}
