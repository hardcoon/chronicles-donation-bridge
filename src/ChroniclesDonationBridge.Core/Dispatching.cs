using System.Collections.Concurrent;
using System.Globalization;

namespace ChroniclesDonationBridge.Core;

public interface IProcessedDonationStore
{
    Task<ProcessedDonationRecord?> FindAsync(string donationId, CancellationToken cancellationToken = default);
    Task<ProcessedDonationRecord?> FindCommandAsync(string donationId, string commandId, CancellationToken cancellationToken = default);
    Task RecordAsync(ProcessedDonationRecord record, CancellationToken cancellationToken = default);
}

public interface IPendingDispatchStore
{
    Task<IReadOnlyList<PendingDispatchSnapshot>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<PendingDispatchSnapshot> pending, CancellationToken cancellationToken = default);
}

public interface IEventHistorySink
{
    Task AppendAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default);
}

public interface IGameCommandTransport
{
    bool IsConnected { get; }
    bool IsGameReady { get; }
    Task SendAsync(GameCommand command, CancellationToken cancellationToken = default);
}

public sealed class InMemoryProcessedDonationStore : IProcessedDonationStore
{
    private readonly Dictionary<string, ProcessedDonationRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessedDonationRecord> _commands = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProcessedDonationRecord?> FindAsync(string donationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _records.TryGetValue(donationId, out var record) ? record : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProcessedDonationRecord?> FindCommandAsync(
        string donationId,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _commands.TryGetValue(CommandKey(donationId, commandId), out var record) ? record : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(ProcessedDonationRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _records[record.DonationId] = record;
            if (!string.IsNullOrWhiteSpace(record.CommandId))
            {
                _commands[CommandKey(record.DonationId, record.CommandId)] = record;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CommandKey(string donationId, string commandId) => donationId + "\0" + commandId;
}

public sealed class InMemoryHistorySink : IEventHistorySink
{
    private readonly List<EventHistoryEntry> _entries = [];
    public IReadOnlyList<EventHistoryEntry> Entries => _entries;

    public Task AppendAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Add(entry);
        return Task.CompletedTask;
    }
}

public sealed class PendingDispatch
{
    public required DonationEvent Donation { get; init; }
    public required RuleMatch Match { get; init; }
    public required GameCommand Command { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public QueueMode QueueMode { get; init; }
}

public sealed class PendingDispatchSnapshot
{
    public DonationEvent Donation { get; set; } = new();
    public ActionDefinition Action { get; set; } = new();
    public TriggerRule Trigger { get; set; } = new();
    public ActionDefinition? SourceAction { get; set; }
    public string? HistoryActionId { get; set; }
    public GameCommand Command { get; set; } = new();
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public QueueMode QueueMode { get; set; }
    public bool InFlight { get; set; }
}

public sealed record QueueTimingSnapshot(
    string? ActionName,
    int WaitingCount,
    bool AwaitingResult,
    bool WaitingForGame,
    int SecondsUntilNext);

public sealed record QueueCancellationSummary(
    IReadOnlyList<EventHistoryEntry> CancelledEntries,
    int SuppressedInFlightRetries)
{
    public int AffectedCount => CancelledEntries.Count + SuppressedInFlightRetries;
}

public sealed class DonationDispatcher
{
    public const string RandomActiveHandlerId = "random_active";
    public const string RandomAllPresetsHandlerId = "random_all_presets";
    private readonly IProcessedDonationStore _processed;
    private readonly IEventHistorySink _history;
    private readonly IPendingDispatchStore? _pendingStore;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<int, int> _randomIndex;
    private readonly Queue<PendingDispatch> _queue = [];
    private readonly ConcurrentDictionary<string, byte> _queuedDonationIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queuedCommandIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingDispatch> _sentCommands = new(StringComparer.Ordinal);
    private readonly HashSet<string> _suppressDeferredRetryCommandIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastActionExecution = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastGlobalExecution = DateTimeOffset.MinValue;

    public DonationDispatcher(
        IProcessedDonationStore processed,
        IEventHistorySink history,
        Func<DateTimeOffset>? utcNow = null,
        Func<int, int>? randomIndex = null,
        IPendingDispatchStore? pendingStore = null)
    {
        _processed = processed;
        _history = history;
        _pendingStore = pendingStore;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _randomIndex = randomIndex ?? Random.Shared.Next;
    }

    public int Count => _queue.Count;
    public TimeSpan GlobalCooldown { get; set; } = TimeSpan.FromSeconds(2);
    public bool IsQueued(string donationId) =>
        !string.IsNullOrWhiteSpace(donationId) && _queuedDonationIds.ContainsKey(donationId);
    public bool IsQueued(string donationId, string commandId) =>
        !string.IsNullOrWhiteSpace(donationId) && !string.IsNullOrWhiteSpace(commandId) &&
        _queuedDonationIds.ContainsKey(donationId) && _queuedCommandIds.ContainsKey(commandId);

    public async Task<int> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingStore is null) return 0;
        var snapshots = await _pendingStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var restored = 0;
            foreach (var snapshot in snapshots)
            {
                if (string.IsNullOrWhiteSpace(snapshot.Donation.Id) ||
                    string.IsNullOrWhiteSpace(snapshot.Command.CommandId) ||
                    !string.Equals(snapshot.Donation.Id, snapshot.Command.DonationId, StringComparison.Ordinal))
                {
                    continue;
                }

                var ledger = await _processed.FindCommandAsync(
                    snapshot.Donation.Id, snapshot.Command.CommandId, cancellationToken).ConfigureAwait(false);
                if (snapshot.InFlight)
                {
                    if (ledger is { State: DispatchState.Sent })
                    {
                        await RecordTerminalAsync(snapshot.Donation, new RuleMatch
                        {
                            Action = snapshot.Action,
                            Trigger = snapshot.Trigger,
                            SourceAction = snapshot.SourceAction,
                            HistoryActionId = snapshot.HistoryActionId
                        }, DispatchState.Uncertain,
                            "Программа была перезапущена после начала отправки; автоматический повтор запрещён",
                            cancellationToken, snapshot.Command.CommandId,
                            canRetryManually: snapshot.SourceAction?.HandlerId != RandomAllPresetsHandlerId)
                            .ConfigureAwait(false);
                        continue;
                    }
                    if (ledger is not { State: DispatchState.Queued }) continue;
                }
                if (ledger is not { State: DispatchState.Queued } || snapshot.Action.Validate().Count > 0)
                {
                    continue;
                }

                var pending = new PendingDispatch
                {
                    Donation = snapshot.Donation,
                    Match = new RuleMatch
                    {
                        Action = snapshot.Action,
                        Trigger = snapshot.Trigger,
                        SourceAction = snapshot.SourceAction,
                        HistoryActionId = snapshot.HistoryActionId
                    },
                    Command = snapshot.Command,
                    EnqueuedAt = snapshot.EnqueuedAt,
                    ExpiresAt = null,
                    QueueMode = QueueMode.WaitIndefinitely
                };
                _queue.Enqueue(pending);
                _queuedDonationIds.TryAdd(pending.Donation.Id, 0);
                _queuedCommandIds.TryAdd(pending.Command.CommandId, 0);
                restored++;
            }
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return restored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QueueTimingSnapshot> GetTimingAsync(
        bool gameConnected,
        bool gameReady,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sentCommands.Values.FirstOrDefault() is { } inFlight)
            {
                return new QueueTimingSnapshot(
                    inFlight.Match.Action.DisplayName,
                    _queue.Count,
                    AwaitingResult: true,
                    WaitingForGame: false,
                    SecondsUntilNext: 0);
            }

            if (!_queue.TryPeek(out var pending))
            {
                return new QueueTimingSnapshot(null, 0, false, false, 0);
            }

            if (!gameConnected || !gameReady)
            {
                return new QueueTimingSnapshot(pending.Match.Action.DisplayName, _queue.Count, false, true, 0);
            }

            var now = _utcNow();
            var globalEligibleAt = _lastGlobalExecution + GlobalCooldown;
            var earliest = _queue
                .Select(candidate => (Pending: candidate, EligibleAt: ActionEligibleAt(candidate, now)))
                .OrderBy(candidate => candidate.EligibleAt)
                .First();
            var eligibleAt = Max(earliest.EligibleAt, globalEligibleAt);
            var seconds = Math.Max(0, (int)Math.Ceiling((eligibleAt - now).TotalSeconds));
            return new QueueTimingSnapshot(
                earliest.Pending.Match.Action.DisplayName, _queue.Count, false, false, seconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DateTimeOffset ActionEligibleAt(PendingDispatch pending, DateTimeOffset now)
    {
        var eligibleAt = now;
        if (_lastActionExecution.TryGetValue(pending.Match.Action.Id, out var lastAction))
        {
            eligibleAt = Max(eligibleAt, lastAction + TimeSpan.FromSeconds(
                Math.Clamp(pending.Match.Action.CooldownSeconds, 0, 86_400)));
        }
        if (pending.Match.SourceAction is { } source &&
            _lastActionExecution.TryGetValue(source.Id, out var lastSource))
        {
            eligibleAt = Max(eligibleAt, lastSource + TimeSpan.FromSeconds(
                Math.Clamp(source.CooldownSeconds, 0, 86_400)));
        }
        return eligibleAt;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    public async Task<DispatchState> EnqueueAsync(
        DonationEvent donation,
        IEnumerable<ActionDefinition> actions,
        QueuePolicy policy,
        CancellationToken cancellationToken = default,
        IEnumerable<ActionDefinition>? availablePresets = null)
    {
        ArgumentNullException.ThrowIfNull(donation);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(policy);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _processed.FindAsync(donation.Id, cancellationToken).ConfigureAwait(false);
            if ((existing is not null && existing.State != DispatchState.Queued) || _queuedDonationIds.ContainsKey(donation.Id))
            {
                await AppendAsync(donation, null, DispatchState.Duplicate, "Событие уже обработано", cancellationToken).ConfigureAwait(false);
                return DispatchState.Duplicate;
            }

            var availableActions = actions.ToList();
            var matches = RuleMatcher.MatchAll(donation, availableActions);
            if (matches.Count == 0)
            {
                await RecordTerminalAsync(donation, null, DispatchState.NoRule, "Подходящее правило не найдено", cancellationToken).ConfigureAwait(false);
                return DispatchState.NoRule;
            }

            var resolvedMatches = new List<RuleMatch>();
            foreach (var match in matches)
            {
                var resolved = ResolveBridgeMetaAction(match, availableActions, availablePresets);
                if (resolved is null)
                {
                    await AppendAsync(donation, match, DispatchState.Rejected,
                        MetaActionUnavailableDetail(match.Action), cancellationToken,
                        canRetryManually: CanRetryUnavailableMetaActionManually(match.Action)).ConfigureAwait(false);
                    continue;
                }
                var actionErrors = resolved.Action.Validate();
                if (actionErrors.Count > 0)
                {
                    await AppendAsync(donation, resolved, DispatchState.Rejected,
                        "Настройки действия недопустимы: " + string.Join("; ", actionErrors),
                        cancellationToken, canRetryManually: true).ConfigureAwait(false);
                    continue;
                }
                resolvedMatches.Add(resolved);
            }
            if (resolvedMatches.Count == 0)
            {
                await _processed.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = donation.Id,
                    State = DispatchState.Rejected,
                    RecordedAt = _utcNow()
                }, cancellationToken).ConfigureAwait(false);
                return DispatchState.Rejected;
            }
            var finalState = DispatchState.Rejected;
            foreach (var resolved in resolvedMatches)
            {
                var state = await EnqueueMatchUnderLockAsync(donation, resolved, policy, cancellationToken).ConfigureAwait(false);
                if (state == DispatchState.Queued) finalState = DispatchState.Queued;
            }
            return finalState;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DispatchState> EnqueueActionAsync(
        DonationEvent syntheticEvent,
        ActionDefinition action,
        QueuePolicy policy,
        CancellationToken cancellationToken = default,
        IEnumerable<ActionDefinition>? availableActions = null,
        IEnumerable<ActionDefinition>? availablePresets = null)
    {
        ArgumentNullException.ThrowIfNull(syntheticEvent);
        ArgumentNullException.ThrowIfNull(action);
        var trigger = action.Triggers.FirstOrDefault(rule => rule.Enabled) ?? new TriggerRule
        {
            Comparator = TriggerComparator.Exact,
            Amount = syntheticEvent.Amount,
            Currency = syntheticEvent.Currency
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _processed.FindAsync(syntheticEvent.Id, cancellationToken).ConfigureAwait(false);
            if ((existing is not null && existing.State != DispatchState.Queued) || _queuedDonationIds.ContainsKey(syntheticEvent.Id))
            {
                return DispatchState.Duplicate;
            }
            var match = new RuleMatch { Action = action, Trigger = trigger };
            var resolvedMatch = ResolveBridgeMetaAction(
                match,
                availableActions ?? [action],
                availablePresets);
            if (resolvedMatch is null)
            {
                await RecordTerminalAsync(
                    syntheticEvent,
                    match,
                    DispatchState.Rejected,
                    MetaActionUnavailableDetail(match.Action),
                    cancellationToken,
                    canRetryManually: CanRetryUnavailableMetaActionManually(match.Action)).ConfigureAwait(false);
                return DispatchState.Rejected;
            }
            return await EnqueueMatchUnderLockAsync(syntheticEvent, resolvedMatch, policy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RuleMatch? ResolveBridgeMetaAction(
        RuleMatch match,
        IEnumerable<ActionDefinition> availableActions,
        IEnumerable<ActionDefinition>? availablePresets)
    {
        if (string.Equals(match.Action.HandlerId, RandomActiveHandlerId, StringComparison.Ordinal))
        {
            var now = _utcNow();
            var candidates = availableActions
                .Where(action => action.IsActive)
                .Where(action => !IsBridgeMetaHandler(action.HandlerId))
                .Where(action => action.Validate().Count == 0)
                .Where(action => !_lastActionExecution.TryGetValue(action.Id, out var lastExecution) ||
                    now - lastExecution >= TimeSpan.FromSeconds(Math.Clamp(action.CooldownSeconds, 0, 86_400)))
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            return new RuleMatch
            {
                Action = candidates[NextIndex(candidates.Count)],
                Trigger = match.Trigger,
                SourceAction = match.Action
            };
        }

        if (!string.Equals(match.Action.HandlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal))
        {
            return match;
        }

        var includedPresetHandlers = SelectedRandomAllHandlers(match.Action);
        if (includedPresetHandlers is null)
        {
            return null;
        }

        var currentTime = _utcNow();
        var presetCandidates = (availablePresets ?? DefaultActionCatalog.CreatePresets())
            .Where(DefaultActionCatalog.IsRandomAllEligiblePreset)
            .Where(preset => includedPresetHandlers.Contains(preset.HandlerId))
            .Where(preset => preset.Validate().Count == 0)
            .Where(preset =>
            {
                var cooldownKey = RandomAllCooldownKey(preset.HandlerId);
                return !_lastActionExecution.TryGetValue(cooldownKey, out var lastExecution) ||
                    currentTime - lastExecution >= TimeSpan.FromSeconds(Math.Clamp(preset.CooldownSeconds, 0, 86_400));
            })
            .ToList();
        if (presetCandidates.Count == 0)
        {
            return null;
        }

        var randomized = CreateRandomizedPreset(
            presetCandidates[NextIndex(presetCandidates.Count)]);
        if (randomized is null)
        {
            return null;
        }

        return new RuleMatch
        {
            Action = randomized,
            Trigger = match.Trigger,
            SourceAction = match.Action,
            HistoryActionId = match.Action.Id
        };
    }

    private ActionDefinition? CreateRandomizedPreset(ActionDefinition preset)
    {
        var action = preset.Clone();
        action.Id = RandomAllCooldownKey(action.HandlerId);
        action.IsActive = true;
        action.Triggers.Clear();
        action.Parameters.Clear();
        foreach (var definition in action.ParameterDefinitions)
        {
            var generated = GenerateRandomParameter(action.HandlerId, definition);
            if (generated is null)
            {
                return null;
            }
            action.Parameters[definition.Key] = generated;
        }

        OrderGeneratedRanges(action);
        if (SpawnGroupBundleCodec.Supports(action.HandlerId))
        {
            var variantKey = SpawnGroupBundleCodec.VariantParameterKey(action.HandlerId);
            if (!action.Parameters.TryGetValue(variantKey, out var variant) ||
                !action.Parameters.TryGetValue("strength", out var strength) ||
                !action.Parameters.TryGetValue("count", out var countText) ||
                !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return null;
            }
            action.SpawnGroups =
            [
                new SpawnGroupEntry { VariantId = variant, Strength = strength, Count = count }
            ];
        }
        return action.Validate().Count == 0 ? action : null;
    }

    private string? GenerateRandomParameter(string handlerId, ParameterDefinition definition)
    {
        if (definition.Kind is ParameterKind.ItemChoice or ParameterKind.MultiChoice)
        {
            return null;
        }
        if (definition.Kind == ParameterKind.Choice)
        {
            return definition.Options.Count == 0
                ? null
                : definition.Options[NextIndex(definition.Options.Count)];
        }

        if (definition.Minimum is not { } minimum || definition.Maximum is not { } maximum || minimum > maximum)
        {
            return null;
        }

        (minimum, maximum) = RandomGenerationBounds(handlerId, definition.Key, minimum, maximum);
        decimal generated;
        if (definition.Kind is ParameterKind.Integer or ParameterKind.Percent)
        {
            var lower = decimal.ToInt32(decimal.Ceiling(minimum));
            var upper = decimal.ToInt32(decimal.Floor(maximum));
            if (lower > upper)
            {
                return null;
            }
            generated = lower + NextIndex(checked(upper - lower + 1));
        }
        else
        {
            const int steps = 1000;
            generated = minimum + (maximum - minimum) * NextIndex(steps + 1) / steps;
        }

        if (generated == 0m && ShouldAvoidZero(handlerId, definition.Key))
        {
            generated = maximum >= 1m ? 1m : minimum <= -1m ? -1m : generated;
        }
        return generated.ToString(CultureInfo.InvariantCulture);
    }

    private static void OrderGeneratedRanges(ActionDefinition action)
    {
        foreach (var minimumDefinition in action.ParameterDefinitions.Where(definition =>
                     definition.Key.StartsWith("minimum_", StringComparison.OrdinalIgnoreCase)))
        {
            var maximumKey = "maximum_" + minimumDefinition.Key["minimum_".Length..];
            if (!action.Parameters.TryGetValue(minimumDefinition.Key, out var minimumText) ||
                !action.Parameters.TryGetValue(maximumKey, out var maximumText) ||
                !decimal.TryParse(minimumText, NumberStyles.Number, CultureInfo.InvariantCulture, out var minimum) ||
                !decimal.TryParse(maximumText, NumberStyles.Number, CultureInfo.InvariantCulture, out var maximum) ||
                minimum <= maximum)
            {
                continue;
            }
            action.Parameters[minimumDefinition.Key] = maximumText;
            action.Parameters[maximumKey] = minimumText;
        }
    }

    private static HashSet<string>? SelectedRandomAllHandlers(ActionDefinition action)
    {
        var definition = action.ParameterDefinitions.FirstOrDefault(candidate =>
            candidate.Kind == ParameterKind.MultiChoice &&
            string.Equals(candidate.Key, DefaultActionCatalog.RandomAllSelectionParameterKey,
                StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return null;
        }
        var value = action.Parameters.TryGetValue(definition.Key, out var saved)
            ? saved
            : definition.DefaultValue;
        if (definition.Validate(value) is not null)
        {
            return null;
        }
        return definition.SelectedOptions(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static (decimal Minimum, decimal Maximum) RandomGenerationBounds(
        string handlerId,
        string parameterKey,
        decimal minimum,
        decimal maximum)
    {
        var softBounds = (handlerId, parameterKey) switch
        {
            ("change_money", "amount") => (-50_000m, 50_000m),
            ("change_health", "percent") => (-50m, 50m),
            ("add_radiation", "percent") => (-35m, 35m),
            ("change_needs", "hunger_percent") => (-50m, 50m),
            ("change_needs", "thirst_percent") => (-50m, 50m),
            _ => (minimum, maximum)
        };
        return (Math.Max(minimum, softBounds.Item1), Math.Min(maximum, softBounds.Item2));
    }

    private int NextIndex(int count)
    {
        if (count <= 1)
        {
            return 0;
        }
        return Math.Clamp(_randomIndex(count), 0, count - 1);
    }

    private static bool IsBridgeMetaHandler(string handlerId) =>
        string.Equals(handlerId, RandomActiveHandlerId, StringComparison.Ordinal) ||
        string.Equals(handlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal);

    private static bool ShouldAvoidZero(string handlerId, string parameterKey) =>
        string.Equals(handlerId, "add_radiation", StringComparison.Ordinal) ||
        string.Equals(handlerId, "change_health", StringComparison.Ordinal) ||
        string.Equals(handlerId, "change_money", StringComparison.Ordinal) ||
        (string.Equals(handlerId, "change_needs", StringComparison.Ordinal) &&
            (string.Equals(parameterKey, "hunger_percent", StringComparison.Ordinal) ||
             string.Equals(parameterKey, "thirst_percent", StringComparison.Ordinal)));

    private static string RandomAllCooldownKey(string handlerId) => "random-all." + handlerId;

    private static string MetaActionUnavailableDetail(ActionDefinition action) =>
        string.Equals(action.HandlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal)
            ? "Для пресета «Рандом по ВСЕМ пресетам» нет доступных безопасных действий"
            : "Для пресета «Рандомный по активным пресетам» нет доступных активных действий";

    private static bool CanRetryUnavailableMetaActionManually(ActionDefinition action) =>
        !string.Equals(action.HandlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal);

    private static bool IsRandomAllResolution(RuleMatch match) =>
        string.Equals(match.SourceAction?.HandlerId, RandomAllPresetsHandlerId, StringComparison.Ordinal);

    private async Task<DispatchState> EnqueueMatchUnderLockAsync(
        DonationEvent donation,
        RuleMatch match,
        QueuePolicy policy,
        CancellationToken cancellationToken)
    {
            var actionErrors = match.Action.Validate();
            if (actionErrors.Count > 0)
            {
                await RecordTerminalAsync(
                    donation,
                    match,
                    DispatchState.Rejected,
                    "Настройки действия недопустимы: " + string.Join("; ", actionErrors),
                    cancellationToken,
                    canRetryManually: true).ConfigureAwait(false);
                return DispatchState.Rejected;
            }

            var now = _utcNow();
            var command = new GameCommand
            {
                DonationId = donation.Id,
                ActionId = match.Action.HandlerId,
                AmountMinor = donation.AmountMinor,
                Currency = Money.NormalizeCurrency(donation.Currency),
                Parameters = new Dictionary<string, string>(match.Action.EffectiveParameters(), StringComparer.OrdinalIgnoreCase),
                CreatedAt = now
            };

            _queue.Enqueue(new PendingDispatch
            {
                Donation = donation,
                Match = match,
                Command = command,
                EnqueuedAt = now,
                ExpiresAt = null,
                QueueMode = QueueMode.WaitIndefinitely
            });
            _queuedDonationIds.TryAdd(donation.Id, 0);
            _queuedCommandIds.TryAdd(command.CommandId, 0);
            await _processed.RecordAsync(new ProcessedDonationRecord
            {
                DonationId = donation.Id,
                CommandId = command.CommandId,
                State = DispatchState.Queued,
                RecordedAt = now
            }, cancellationToken).ConfigureAwait(false);
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            await AppendAsync(donation, match, DispatchState.Queued, "Добавлено в очередь", cancellationToken, command.CommandId).ConfigureAwait(false);
            return DispatchState.Queued;
    }

    public async Task<EventHistoryEntry?> CancelQueuedAsync(
        string donationId,
        CancellationToken cancellationToken = default)
        => await CancelQueuedUnderLockAsync(donationId, null, cancellationToken).ConfigureAwait(false);

    public async Task<EventHistoryEntry?> CancelQueuedAsync(
        EventHistoryEntry queuedEntry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queuedEntry);
        if (queuedEntry.State != DispatchState.Queued)
        {
            return null;
        }

        return await CancelQueuedUnderLockAsync(
            queuedEntry.DonationId,
            queuedEntry,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueueCancellationSummary> CancelAllQueuedAsync(
        IEnumerable<EventHistoryEntry>? persistedEntries = null,
        CancellationToken cancellationToken = default)
    {
        var persisted = (persistedEntries ?? [])
            .Where(entry => entry.State == DispatchState.Queued &&
                !string.IsNullOrWhiteSpace(entry.DonationId) &&
                !string.IsNullOrWhiteSpace(entry.CommandId))
            .GroupBy(entry => entry.CommandId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cancelledEntries = new List<EventHistoryEntry>();
            var cancelledCommandIds = new HashSet<string>(StringComparer.Ordinal);
            var suppressedInFlightRetries = 0;

            foreach (var commandId in _sentCommands.Keys)
            {
                if (_suppressDeferredRetryCommandIds.Add(commandId))
                {
                    suppressedInFlightRetries++;
                }
            }

            // Keep the dispatcher gate for the whole operation so the drain loop
            // cannot send the next command between individual cancellations.
            while (_queue.TryPeek(out var cancelled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _processed.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = cancelled.Donation.Id,
                    CommandId = cancelled.Command.CommandId,
                    State = DispatchState.Cancelled,
                    RecordedAt = _utcNow()
                }, cancellationToken).ConfigureAwait(false);

                _queue.Dequeue();
                _queuedCommandIds.TryRemove(cancelled.Command.CommandId, out _);
                ReleaseDonationIfFinished(cancelled.Donation.Id);
                cancelledCommandIds.Add(cancelled.Command.CommandId);
                cancelledEntries.Add(await AppendAsync(
                    cancelled.Donation,
                    cancelled.Match,
                    DispatchState.Cancelled,
                    "Очередь очищена пользователем",
                    cancellationToken,
                    cancelled.Command.CommandId,
                    canRetryManually: true).ConfigureAwait(false));
            }

            foreach (var queuedEntry in persisted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cancelledCommandIds.Contains(queuedEntry.CommandId))
                {
                    continue;
                }

                var cancelled = await CancelPersistedQueuedUnderLockAsync(
                    queuedEntry,
                    cancellationToken).ConfigureAwait(false);
                if (cancelled is not null)
                {
                    cancelledEntries.Add(cancelled);
                }
            }

            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return new QueueCancellationSummary(cancelledEntries, suppressedInFlightRetries);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EventHistoryEntry?> CancelQueuedUnderLockAsync(
        string donationId,
        EventHistoryEntry? persistedEntry,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(donationId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cancelled = _queue.FirstOrDefault(pending =>
                string.Equals(pending.Donation.Id, donationId, StringComparison.Ordinal) &&
                (persistedEntry is null || string.Equals(pending.Command.CommandId, persistedEntry.CommandId, StringComparison.Ordinal)));
            if (cancelled is null)
            {
                return persistedEntry is null
                    ? null
                    : await CancelPersistedQueuedUnderLockAsync(persistedEntry, cancellationToken).ConfigureAwait(false);
            }

            // Make the terminal ledger durable before mutating the in-memory
            // queue. A crash at any later point cannot resurrect the donation.
            await _processed.RecordAsync(new ProcessedDonationRecord
            {
                DonationId = cancelled.Donation.Id,
                CommandId = cancelled.Command.CommandId,
                State = DispatchState.Cancelled,
                RecordedAt = _utcNow()
            }, cancellationToken).ConfigureAwait(false);

            var queuedCount = _queue.Count;
            for (var index = 0; index < queuedCount; index++)
            {
                var pending = _queue.Dequeue();
                if (ReferenceEquals(pending, cancelled))
                {
                    continue;
                }

                _queue.Enqueue(pending);
            }

            _queuedCommandIds.TryRemove(cancelled.Command.CommandId, out _);
            ReleaseDonationIfFinished(cancelled.Donation.Id);
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return await AppendAsync(
                cancelled.Donation,
                cancelled.Match,
                DispatchState.Cancelled,
                "Удалено из очереди пользователем",
                cancellationToken,
                cancelled.Command.CommandId,
                canRetryManually: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EventHistoryEntry?> CancelPersistedQueuedUnderLockAsync(
        EventHistoryEntry queuedEntry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queuedEntry.DonationId) ||
            string.IsNullOrWhiteSpace(queuedEntry.CommandId))
        {
            return null;
        }

        var current = await _processed.FindCommandAsync(
            queuedEntry.DonationId, queuedEntry.CommandId, cancellationToken).ConfigureAwait(false);
        if (current is not { State: DispatchState.Queued } ||
            string.IsNullOrWhiteSpace(current.CommandId) ||
            !string.Equals(current.CommandId, queuedEntry.CommandId, StringComparison.Ordinal))
        {
            return null;
        }

        // A restored queue row is not reconstructed as an executable command:
        // history does not contain a trustworthy snapshot of all action settings.
        // Persisting the terminal state under the dispatcher gate also prevents a
        // concurrent DonationAlerts recovery from enqueueing the same event later.
        await _processed.RecordAsync(new ProcessedDonationRecord
        {
            DonationId = queuedEntry.DonationId,
            CommandId = current.CommandId,
            State = DispatchState.Cancelled,
            RecordedAt = _utcNow()
        }, cancellationToken).ConfigureAwait(false);

        var cancelled = new EventHistoryEntry
        {
            Timestamp = _utcNow(),
            DonationCreatedAt = queuedEntry.DonationCreatedAt,
            DonationId = queuedEntry.DonationId,
            CommandId = current.CommandId,
            Donor = queuedEntry.Donor,
            Amount = queuedEntry.Amount,
            Currency = queuedEntry.Currency,
            ActionId = queuedEntry.ActionId,
            HandlerId = queuedEntry.HandlerId,
            ActionName = queuedEntry.ActionName,
            State = DispatchState.Cancelled,
            Detail = "Удалено из очереди пользователем",
            CanRetryManually = true
        };
        await _history.AppendAsync(cancelled, cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    public async Task<IReadOnlyList<EventHistoryEntry>> DrainAsync(
        IGameCommandTransport transport,
        QueuePolicy policy,
        CancellationToken cancellationToken = default)
    {
        var emitted = new List<EventHistoryEntry>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sentCommands.Count > 0)
            {
                return emitted;
            }
            var candidatesRemaining = _queue.Count;
            var queueOrderChanged = false;
            while (candidatesRemaining-- > 0 && _queue.TryPeek(out var pending))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _utcNow();
                if (!transport.IsConnected || !transport.IsGameReady)
                {
                    break;
                }

                if (now - _lastGlobalExecution < GlobalCooldown)
                {
                    break;
                }

                var actionCooldown = TimeSpan.FromSeconds(Math.Clamp(pending.Match.Action.CooldownSeconds, 0, 86_400));
                var sourceAction = pending.Match.SourceAction;
                var actionCooldownActive =
                    _lastActionExecution.TryGetValue(pending.Match.Action.Id, out var lastAction) &&
                    now - lastAction < actionCooldown;
                var sourceCooldownActive = sourceAction is not null &&
                    _lastActionExecution.TryGetValue(sourceAction.Id, out var lastSourceAction) &&
                    now - lastSourceAction < TimeSpan.FromSeconds(Math.Clamp(sourceAction.CooldownSeconds, 0, 86_400));
                if (actionCooldownActive || sourceCooldownActive)
                {
                    _queue.Enqueue(_queue.Dequeue());
                    queueOrderChanged = true;
                    continue;
                }

                _queue.Dequeue();
                _queuedCommandIds.TryRemove(pending.Command.CommandId, out _);

                // Persist SENT before touching the pipe. A crash or broken ACK can never cause an automatic duplicate.
                await _processed.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = pending.Donation.Id,
                    CommandId = pending.Command.CommandId,
                    State = DispatchState.Sent,
                    RecordedAt = now
                }, cancellationToken).ConfigureAwait(false);

                try
                {
                    _sentCommands[pending.Command.CommandId] = pending;
                    await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    await transport.SendAsync(pending.Command, cancellationToken).ConfigureAwait(false);
                    _lastGlobalExecution = now;
                    _lastActionExecution[pending.Match.Action.Id] = now;
                    if (sourceAction is not null) _lastActionExecution[sourceAction.Id] = now;
                    emitted.Add(await AppendAsync(pending.Donation, pending.Match, DispatchState.Sent, "Команда передана игре", cancellationToken, pending.Command.CommandId).ConfigureAwait(false));
                    break;
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    _sentCommands.Remove(pending.Command.CommandId);
                    ReleaseDonationIfFinished(pending.Donation.Id);
                    var detail = "Соединение потеряно после начала отправки; автоматический повтор запрещён";
                    await _processed.RecordAsync(new ProcessedDonationRecord
                    {
                        DonationId = pending.Donation.Id,
                        CommandId = pending.Command.CommandId,
                        State = DispatchState.Uncertain,
                        RecordedAt = _utcNow()
                    }, cancellationToken).ConfigureAwait(false);
                    await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    emitted.Add(await AppendAsync(
                        pending.Donation,
                        pending.Match,
                        DispatchState.Uncertain,
                        detail,
                        cancellationToken,
                        pending.Command.CommandId,
                        canRetryManually: !IsRandomAllResolution(pending.Match)).ConfigureAwait(false));
                }
            }
            if (queueOrderChanged)
            {
                await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        return emitted;
    }

    public async Task<EventHistoryEntry?> CompleteAsync(GameExecutionResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sentCommands.Remove(result.CommandId, out var pending))
            {
                return null;
            }

            var suppressDeferredRetry = _suppressDeferredRetryCommandIds.Remove(result.CommandId);
            var state = result.Status switch
            {
                GameResultStatus.Executed => DispatchState.Executed,
                GameResultStatus.Deferred when suppressDeferredRetry => DispatchState.Cancelled,
                GameResultStatus.Deferred => DispatchState.Queued,
                GameResultStatus.Rejected => DispatchState.Rejected,
                GameResultStatus.Uncertain => DispatchState.Uncertain,
                _ => DispatchState.Uncertain
            };
            var willRetry = result.Status == GameResultStatus.Deferred && !suppressDeferredRetry;
            var randomAllResolution = IsRandomAllResolution(pending.Match);
            var retryCommand = willRetry ? CreateDeferredRetryCommand(pending.Command) : null;
            await _processed.RecordAsync(new ProcessedDonationRecord
            {
                DonationId = pending.Donation.Id,
                CommandId = result.CommandId,
                State = willRetry ? DispatchState.Deferred : state,
                RecordedAt = _utcNow()
            }, cancellationToken).ConfigureAwait(false);
            var detail = suppressDeferredRetry && result.Status == GameResultStatus.Deferred
                ? "Очередь очищена пользователем; отложенный эффект не будет повторён" +
                    (string.IsNullOrWhiteSpace(result.Reason) ? string.Empty : $": {result.Reason}")
                : willRetry
                    ? "Временно не выполнено; эффект перенесён в конец очереди" +
                        (string.IsNullOrWhiteSpace(result.Reason) ? string.Empty : $": {result.Reason}")
                    : string.IsNullOrWhiteSpace(result.Reason) ? state.ToString() : result.Reason;
            var history = await AppendAsync(
                pending.Donation,
                pending.Match,
                state,
                detail,
                cancellationToken,
                retryCommand?.CommandId ?? result.CommandId,
                canRetryManually: !randomAllResolution && !willRetry &&
                    state is DispatchState.Deferred or DispatchState.Rejected or DispatchState.Uncertain).ConfigureAwait(false);

            if (retryCommand is not null)
            {
                _queue.Enqueue(new PendingDispatch
                {
                    Donation = pending.Donation,
                    Match = pending.Match,
                    Command = retryCommand,
                    EnqueuedAt = pending.EnqueuedAt,
                    ExpiresAt = null,
                    QueueMode = QueueMode.WaitIndefinitely
                });
                _queuedDonationIds.TryAdd(pending.Donation.Id, 0);
                _queuedCommandIds.TryAdd(retryCommand.CommandId, 0);
                await _processed.RecordAsync(new ProcessedDonationRecord
                {
                    DonationId = pending.Donation.Id,
                    CommandId = retryCommand.CommandId,
                    State = DispatchState.Queued,
                    RecordedAt = _utcNow()
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ReleaseDonationIfFinished(pending.Donation.Id);
            }
            await PersistQueueUnderLockAsync(cancellationToken).ConfigureAwait(false);

            return history;
        }
        finally
        {
            _gate.Release();
        }
    }

    private GameCommand CreateDeferredRetryCommand(GameCommand source) => new()
    {
        DonationId = source.DonationId,
        ActionId = source.ActionId,
        AmountMinor = source.AmountMinor,
        Currency = source.Currency,
        Parameters = new Dictionary<string, string>(source.Parameters, StringComparer.OrdinalIgnoreCase),
        CreatedAt = _utcNow()
    };

    private async Task<EventHistoryEntry> RecordTerminalAsync(
        DonationEvent donation,
        RuleMatch? match,
        DispatchState state,
        string detail,
        CancellationToken cancellationToken,
        string commandId = "",
        bool? canRetryManually = null)
    {
        await _processed.RecordAsync(new ProcessedDonationRecord
        {
            DonationId = donation.Id,
            CommandId = commandId,
            State = state,
            RecordedAt = _utcNow()
        }, cancellationToken).ConfigureAwait(false);
        return await AppendAsync(donation, match, state, detail, cancellationToken, commandId, canRetryManually).ConfigureAwait(false);
    }

    private void ReleaseDonationIfFinished(string donationId)
    {
        if (_queue.Any(pending => string.Equals(pending.Donation.Id, donationId, StringComparison.Ordinal)) ||
            _sentCommands.Values.Any(pending => string.Equals(pending.Donation.Id, donationId, StringComparison.Ordinal)))
        {
            return;
        }
        _queuedDonationIds.TryRemove(donationId, out _);
    }

    private Task PersistQueueUnderLockAsync(CancellationToken cancellationToken)
    {
        if (_pendingStore is null) return Task.CompletedTask;
        var snapshots = _sentCommands.Values.Select(pending => ToSnapshot(pending, true))
            .Concat(_queue.Select(pending => ToSnapshot(pending, false)))
            .ToList();
        return _pendingStore.SaveAsync(snapshots, cancellationToken);
    }

    private static PendingDispatchSnapshot ToSnapshot(PendingDispatch pending, bool inFlight) => new()
    {
        Donation = new DonationEvent
        {
            Id = pending.Donation.Id,
            Username = pending.Donation.Username,
            Message = pending.Donation.Message,
            Amount = pending.Donation.Amount,
            Currency = pending.Donation.Currency,
            CreatedAt = pending.Donation.CreatedAt,
            ReceivedAt = pending.Donation.ReceivedAt
        },
        Action = pending.Match.Action.Clone(),
        Trigger = pending.Match.Trigger.Clone(),
        SourceAction = pending.Match.SourceAction?.Clone(),
        HistoryActionId = pending.Match.HistoryActionId,
        Command = new GameCommand
        {
            CommandId = pending.Command.CommandId,
            DonationId = pending.Command.DonationId,
            ActionId = pending.Command.ActionId,
            AmountMinor = pending.Command.AmountMinor,
            Currency = pending.Command.Currency,
            Parameters = new Dictionary<string, string>(pending.Command.Parameters, StringComparer.OrdinalIgnoreCase),
            CreatedAt = pending.Command.CreatedAt
        },
        EnqueuedAt = pending.EnqueuedAt,
        ExpiresAt = null,
        QueueMode = QueueMode.WaitIndefinitely,
        InFlight = inFlight
    };

    private async Task<EventHistoryEntry> AppendAsync(
        DonationEvent donation,
        RuleMatch? match,
        DispatchState state,
        string detail,
        CancellationToken cancellationToken,
        string commandId = "",
        bool? canRetryManually = null)
    {
        var entry = new EventHistoryEntry
        {
            Timestamp = _utcNow(),
            DonationCreatedAt = donation.CreatedAt,
            DonationId = donation.Id,
            CommandId = commandId,
            Donor = donation.Username,
            Amount = donation.Amount,
            Currency = donation.Currency,
            ActionId = match?.HistoryActionId ?? match?.Action.Id ?? string.Empty,
            HandlerId = match?.Action.HandlerId ?? string.Empty,
            ActionName = match?.Action.DisplayName ?? string.Empty,
            State = state,
            Detail = detail,
            CanRetryManually = canRetryManually ?? state is DispatchState.Rejected or DispatchState.Uncertain or DispatchState.Skipped or DispatchState.Expired
        };
        await _history.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }
}
