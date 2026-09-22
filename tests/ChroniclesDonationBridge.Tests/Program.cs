using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.DonationAlerts;
using ChroniclesDonationBridge.GameIpc;
using ChroniclesDonationBridge.Persistence;
using System.Text.Json;
using System.IO.Pipes;
using System.Net;
using System.Text;

namespace ChroniclesDonationBridge.Tests;

internal static class Program
{
    private static Task TestSingleInstanceAsync()
    {
        var name = @"Local\ChroniclesBridge-Test-" + Guid.NewGuid().ToString("N");
        using (var first = ChroniclesDonationBridge.App.Services.SingleInstanceGate.TryAcquire(name))
        {
            True(first is not null, "first instance acquired");
            using var second = ChroniclesDonationBridge.App.Services.SingleInstanceGate.TryAcquire(name);
            True(second is null, "second instance blocked independent of executable path");
        }
        using var afterExit = ChroniclesDonationBridge.App.Services.SingleInstanceGate.TryAcquire(name);
        True(afterExit is not null, "gate released after exit");
        return Task.CompletedTask;
    }

    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("default catalog", TestDefaultCatalogAsync),
            ("Chronicles vehicle presets and safe random exclusion", VehiclePresetTests.CatalogAsync),
            ("fuel subgroup preserves item allowlist and hidden materials", VehiclePresetTests.FuelCategoryAsync),
            ("independent category filters and grid density", UiParityTests.CategoriesAsync),
            ("view preferences snapshot and migration", UiParityTests.ViewPreferencesAsync),
            ("popup draft cancellation and atomic validation", UiParityTests.PopupDraftAsync),
            ("currency conversion and price replacement", UiParityTests.PriceMathAsync),
            ("CBR nominal parser and safe XML", UiParityTests.ParseRatesAsync),
            ("price preview invalidation and offline errors", UiParityTests.PricePreviewAsync),
            ("rate request is bounded and anonymous", UiParityTests.RateHttpAsync),
            ("file presets require matching installed scripts", FilePresetArchitectureTests.LoadAndInstallVisibilityAsync),
            ("IX-Ray script load lists stay synchronized", FilePresetArchitectureTests.ScriptLoadListsStaySynchronizedAsync),
            ("malformed and duplicate file presets are isolated", FilePresetArchitectureTests.InvalidFilesAreIsolatedAsync),
            ("add-on installer updates managed files and preserves unknown files", FilePresetArchitectureTests.AddonInstallerAsync),
            ("missing file preset source keeps saved user preset", FilePresetArchitectureTests.MissingSourceKeepsSavedPresetAsync),
            ("single instance blocks duplicate and releases gate", TestSingleInstanceAsync),
            ("settings saves keep live editor references", EditorPersistenceTests.LiveModelsSurviveSavesAsync),
            ("settings snapshot copies all fields independently", EditorPersistenceTests.SnapshotIsIndependentAsync),
            ("item bundles and user presets persist independently", EditorPersistenceTests.ItemBundlesAndUserPresetsAsync),
            ("NPC and mutant group rows persist and migrate", EditorPersistenceTests.SpawnGroupBundlesAsync),
            ("editor commits multiple checkboxes and fields", EditorPersistenceTests.BatchedMultiChoiceAsync),
            ("editor validates numeric drafts and decimal comma", EditorPersistenceTests.NumericDraftsAsync),
            ("history button and numeric WPF binding contracts", EditorPersistenceTests.UiContractsAsync),
            ("manifest is authoritative and preserves random selection", TestManifestCatalogAndMultiChoiceMigrationAsync),
            ("active action price ordering", TestActionPriceOrderingAsync),
            ("preset instances are independent", TestPresetInstancesAsync),
            ("legacy settings migrate to instances", TestLegacySettingsMigrationAsync),
            ("legacy handlers are not collapsed", TestLegacyHandlersNotCollapsedAsync),
            ("schema v2 actions are active by membership", TestSchemaV2MembershipAsync),
            ("schema v2 requires handler id", TestSchemaV2RequiresHandlerAsync),
            ("duplicate trigger ids are repaired", TestDuplicateTriggerIdsRepairedAsync),
            ("same handler instances dispatch independently", TestSameHandlerInstancesAsync),
            ("exact priority", TestExactPriorityAsync),
            ("highest threshold", TestHighestThresholdAsync),
            ("strict currency", TestStrictCurrencyAsync),
            ("decimal money", TestMoneyAsync),
            ("duplicate trigger validation", TestDuplicateTriggerAsync),
            ("same winning price queues every action", TestSamePriceDispatchAsync),
            ("next-effect queue snapshot follows real cooldown", TestQueueSnapshotAsync),
            ("history shows latest status per command", TestEventHistoryProjectionAsync),
            ("action cooldown cannot block queue", TestActionCooldownFairnessAsync),
            ("pending queue survives application restart", TestPendingQueueRestoreAsync),
            ("multi-item bundle validation and wire payload", TestItemBundleAsync),
            ("multi-group NPC and mutant wire payloads", TestSpawnGroupBundleAsync),
            ("skip policy executes when ready", TestSkipReadyAsync),
            ("legacy skip policy preserves unavailable", TestSkipUnavailableAsync),
            ("legacy queue TTL is migrated", TestQueueTtlAsync),
            ("uncertain is at-most-once", TestUncertainAsync),
            ("wire percent encoding", TestWireEncodingAsync),
            ("wire size limit", TestWireLimitAsync),
            ("first run has no backlog", TestFirstRunRecoveryAsync),
            ("recovery TTL", TestRecoveryTtlAsync),
            ("recovery ignores advanced terminal checkpoint", TestRecoveryContinuityAsync),
            ("Centrifugo subscription response", TestSubscriptionResponseAsync),
            ("JSON settings persistence", TestJsonSettingsAsync),
            ("DPAPI secret roundtrip", TestDpapiAsync)
            ,("secret log redaction", TestLogRedactionAsync)
            ,("OAuth refresh preserves token", TestRefreshTokenAsync)
            ,("OAuth loopback and state", TestOAuthLoopbackAsync)
            ,("DonationAlerts API rate limiter", TestDonationAlertsApiRateLimiterAsync)
            ,("DonationAlerts REST requests use limiter", TestDonationAlertsRestUsesRateLimiterAsync)
            ,("IPC waits safely without game", TestIpcIdleWithoutGameAsync)
            ,("IPC app-first and result", TestIpcAppFirstAsync)
            ,("IPC game-first and ACK loss", TestIpcGameFirstAsync)
            ,("IPC pause and Alt+Tab preserve pending command", IpcRecoveryTests.PauseAndResumeAsync)
            ,("IPC delayed ACK remains valid", IpcRecoveryTests.LateAckAsync)
            ,("IPC read-only recovery waits for persistence", IpcRecoveryTests.HealthyRecoveryAndPersistenceAsync)
            ,("IPC reconnect queries same session", IpcRecoveryTests.ReconnectSameSessionAsync)
            ,("IPC changed Lua session cannot replay", IpcRecoveryTests.ChangedSessionAsync)
            ,("IPC process exit resolves uncertain", IpcRecoveryTests.ProcessExitAsync)
            ,("IPC missing cached result fails closed", IpcRecoveryTests.UnknownResultAsync)
            ,("unanswered command has a terminal watchdog", TestCommandResultWatchdogAsync)
            ,("IPC session handshake validation", IpcRecoveryTests.SessionProtocolAsync)
            ,("queue order survives lost ACK and reconnect", IpcRecoveryTests.QueueRoundTripAsync)
            ,("deferred retry uses a fresh attempt and keeps queue", TestDeferredRetryAsync)
            ,("queued recovery and sequential dispatch", TestQueuedRecoveryAsync)
            ,("remove selected queued event", TestQueueCancellationAsync)
            ,("clear every queued event", TestClearQueueAsync)
            ,("clear queue suppresses deferred in-flight retry", TestClearSuppressesDeferredRetryAsync)
            ,("queue removal persists across restart", TestQueueCancellationPersistenceAsync)
            ,("interrupted send history recovery", TestInterruptedSendHistoryRecoveryAsync)
            ,("invalid action is rejected before IPC", TestInvalidActionRejectedAsync)
            ,("ordered preset ranges are validated", TestOrderedPresetRangesAsync)
            ,("random preset resolves to an active action", TestRandomPresetAsync)
            ,("random all presets resolves safely", TestRandomAllPresetsAsync)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
            }
        }
        Console.WriteLine($"RESULT {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestDefaultCatalogAsync()
    {
        True(string.IsNullOrEmpty(new AppSettings().ExpectedAccountCode), "public install is not locked to one streamer");
        True(new AppSettings().LimitDonationAlertsApiRequests, "DonationAlerts API limiter is enabled by default");
        Equal(5, new AppSettings().GlobalCooldownSeconds, "first-run global effect pause");
        var catalog = DefaultActionCatalog.Create();
        Equal(3, catalog.Count, "active default count");
        SequenceEqual(new[] { "add_radiation", "drop_active_weapon", "spawn_mutants" }, catalog.Select(x => x.HandlerId), "active default handlers");
        Equal(3, catalog.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count(), "active instance ids");
        Equal(1m, catalog.Single(x => x.HandlerId == "add_radiation").Triggers.Single().Amount, "radiation price");
        Equal(2m, catalog.Single(x => x.HandlerId == "drop_active_weapon").Triggers.Single().Amount, "weapon price");
        var dogs = catalog.Single(x => x.HandlerId == "spawn_mutants");
        Equal(3m, dogs.Triggers.Single().Amount, "dogs price");
        Equal("dog", dogs.Parameters["species"], "dogs species");
        Equal("3", dogs.Parameters["count"], "dogs count");

        var presets = DefaultActionCatalog.CreatePresets();
        Equal(23, presets.Count, "unique preset count");
        Equal(presets.Count, presets.Select(x => x.HandlerId).Distinct(StringComparer.Ordinal).Count(), "preset handlers unique");
        True(presets.All(x => !x.IsActive && x.Triggers.Count == 0), "presets are not active instances");
        True(presets.All(x => x.Id == x.HandlerId), "preset ids are stable");
        True(presets.All(x => x.HandlerId != "spawn_dogs_3"), "legacy dog preset removed");
        True(presets.All(x => x.CooldownSeconds == 1), "every new preset starts with a one-second cooldown");
        var hostileSquad = presets.Single(x => x.HandlerId == "spawn_random_hostile_squad");
        var randomMutantPack = presets.Single(x => x.HandlerId == "spawn_random_mutant_pack");
        foreach (var randomSpawn in new[] { hostileSquad, randomMutantPack })
        {
            Equal(2, randomSpawn.ParameterDefinitions.Count, $"{randomSpawn.HandlerId} range parameter count");
            var minimumCount = randomSpawn.ParameterDefinitions.Single(x => x.Key == "minimum_count");
            var maximumCount = randomSpawn.ParameterDefinitions.Single(x => x.Key == "maximum_count");
            Equal(ParameterKind.Integer, minimumCount.Kind, $"{randomSpawn.HandlerId} minimum kind");
            Equal(1m, minimumCount.Minimum, $"{randomSpawn.HandlerId} minimum lower bound");
            Equal(99m, minimumCount.Maximum, $"{randomSpawn.HandlerId} minimum upper bound");
            Equal(ParameterKind.Integer, maximumCount.Kind, $"{randomSpawn.HandlerId} maximum kind");
            Equal(1m, maximumCount.Minimum, $"{randomSpawn.HandlerId} maximum lower bound");
            Equal(99m, maximumCount.Maximum, $"{randomSpawn.HandlerId} maximum upper bound");
            Equal("1", randomSpawn.Parameters["minimum_count"], $"{randomSpawn.HandlerId} default minimum");
            Equal("10", randomSpawn.Parameters["maximum_count"], $"{randomSpawn.HandlerId} default maximum");
        }
        var randomActive = presets.Single(x => x.HandlerId == DonationDispatcher.RandomActiveHandlerId);
        var randomAll = presets.Single(x => x.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId);
        Equal("Рандомный по активным пресетам", randomActive.DisplayName, "active random preset name");
        Equal("Рандом по ВСЕМ пресетам", randomAll.DisplayName, "all-presets random name");
        Equal(1, randomAll.ParameterDefinitions.Count, "all-presets random has one compact selection parameter");
        var randomAllSelection = randomAll.ParameterDefinitions.Single();
        Equal(ParameterKind.MultiChoice, randomAllSelection.Kind, "all-presets selection kind");
        Equal(DefaultActionCatalog.RandomAllSelectionParameterKey, randomAllSelection.Key, "all-presets selection key");
        Equal(18, randomAllSelection.Options.Count, "all safe concrete presets are shown as checkboxes");
        Equal(18, randomAllSelection.SelectedOptions(randomAllSelection.DefaultValue).Count, "all safe presets are enabled by default");
        True(randomAllSelection.Options.All(option => randomAllSelection.OptionLabels.TryGetValue(option, out var label) &&
                !string.IsNullOrWhiteSpace(label) && !string.Equals(label, option, StringComparison.Ordinal)),
            "all random-all checkboxes have localized labels");
        True(!randomAllSelection.Options.Contains("spawn_items", StringComparer.Ordinal) &&
                !randomAllSelection.Options.Contains(DonationDispatcher.RandomActiveHandlerId, StringComparer.Ordinal) &&
                !randomAllSelection.Options.Contains(DonationDispatcher.RandomAllPresetsHandlerId, StringComparer.Ordinal),
            "items and meta presets never appear in random-all selection");
        True(randomAllSelection.Validate(string.Empty) is null, "all random-all checkboxes may be disabled safely");
        True(randomAllSelection.Validate("spawn_items") is not null, "unsafe random-all selection is rejected");
        True(randomAllSelection.Validate("change_health,change_health") is not null, "duplicate random-all selection is rejected");
        var randomAllClone = randomAll.CloneAsNewInstance();
        randomAllClone.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = "change_health,teleport_random";
        var independentRandomAllClone = randomAll.CloneAsNewInstance();
        Equal(randomAllSelection.DefaultValue,
            independentRandomAllClone.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey],
            "random-all checkbox state is independent between active instances");
        var mutantSpecies = presets.Single(x => x.HandlerId == "spawn_mutants").ParameterDefinitions.Single(x => x.Key == "species").Options;
        True(mutantSpecies.Contains("chimera"), "chimera preset option");
        True(mutantSpecies.Contains("boar") && mutantSpecies.Contains("flesh") && mutantSpecies.Contains("tushkano"), "common mutant preset options");
        True(mutantSpecies.Contains("pseudodog") && mutantSpecies.Contains("psy_dog") && mutantSpecies.Contains("pseudogiant"), "advanced mutant preset options");
        True(mutantSpecies.Contains("burer") && mutantSpecies.Contains("controller") && mutantSpecies.Contains("poltergeist"), "special mutant preset options");
        var itemParameter = presets.Single(x => x.HandlerId == "spawn_items").ParameterDefinitions.Single(x => x.Key == "item");
        True(itemParameter.OptionGroups.ContainsKey("medicine") && itemParameter.OptionGroups["medicine"].Contains("medkit"), "item preset has medicine category");
        True(itemParameter.OptionGroups.ContainsKey("food") && itemParameter.OptionGroups.ContainsKey("ammo_explosives"), "item preset has fallback categories");
        var itemClone = itemParameter.Clone();
        itemClone.OptionGroups["medicine"].Clear();
        True(itemParameter.OptionGroups["medicine"].Count > 0, "item categories are deep-cloned");
        itemParameter.OptionLabels["medkit"] = "аптечка";
        var labeledItemClone = itemParameter.Clone();
        labeledItemClone.OptionLabels["medkit"] = "изменено";
        Equal("аптечка", itemParameter.OptionLabels["medkit"], "item labels are deep-cloned");
        var anomaly = presets.Single(x => x.HandlerId == "spawn_anomaly");
        var anomalyDistance = anomaly.ParameterDefinitions.Single(x => x.Key == "distance_meters");
        Equal(0m, anomalyDistance.Minimum, "anomaly distance minimum");
        Equal(100m, anomalyDistance.Maximum, "anomaly distance maximum");
        Equal("20", anomaly.Parameters["distance_meters"], "anomaly distance default");
        var drunk = presets.Single(x => x.HandlerId == "get_drunk");
        var drunkDuration = drunk.ParameterDefinitions.Single(x => x.Key == "duration_seconds");
        Equal(5m, drunkDuration.Minimum, "drunk duration minimum");
        Equal(300m, drunkDuration.Maximum, "drunk duration maximum");
        Equal("30", drunk.Parameters["duration_seconds"], "drunk duration default");
        True(!drunk.Parameters.ContainsKey("portions"), "drunk effect does not model inventory portions");
        var godMode = presets.Single(x => x.HandlerId == "god_mode");
        var godModeDuration = godMode.ParameterDefinitions.Single(x => x.Key == "duration_seconds");
        Equal(ParameterKind.Integer, godModeDuration.Kind, "god mode duration kind");
        Equal(5m, godModeDuration.Minimum, "god mode duration minimum");
        Equal(600m, godModeDuration.Maximum, "god mode duration maximum");
        Equal("30", godMode.Parameters["duration_seconds"], "god mode duration default");
        True(godModeDuration.Validate("4") is not null && godModeDuration.Validate("601") is not null &&
            godModeDuration.Validate("30.5") is not null, "god mode duration bounds enforced");
        True(randomAllSelection.Options.Contains("god_mode", StringComparer.Ordinal),
            "god mode is enabled in random-all by default");
        var needs = presets.Single(x => x.HandlerId == "change_needs");
        Equal(2, needs.ParameterDefinitions.Count, "needs parameter count");
        var hunger = needs.ParameterDefinitions.Single(x => x.Key == "hunger_percent");
        var thirst = needs.ParameterDefinitions.Single(x => x.Key == "thirst_percent");
        Equal(ParameterKind.Percent, hunger.Kind, "hunger parameter kind");
        Equal(-100m, hunger.Minimum, "hunger minimum");
        Equal(100m, hunger.Maximum, "hunger maximum");
        Equal(ParameterKind.Percent, thirst.Kind, "thirst parameter kind");
        Equal(-100m, thirst.Minimum, "thirst minimum");
        Equal(100m, thirst.Maximum, "thirst maximum");
        Equal("25", needs.Parameters["hunger_percent"], "hunger default");
        Equal("25", needs.Parameters["thirst_percent"], "thirst default");
        True(hunger.Validate("0") is null && thirst.Validate("0") is null, "zero leaves either need unchanged");
        True(hunger.Validate("-101") is not null && thirst.Validate("101") is not null, "need bounds enforced");
        var emission = presets.Single(x => x.HandlerId == "trigger_emission");
        Equal(0, emission.ParameterDefinitions.Count, "emission has no parameters");
        Equal(1, emission.CooldownSeconds, "emission default cooldown");
        var sleep = presets.Single(x => x.HandlerId == "sleep_hours");
        var sleepHours = sleep.ParameterDefinitions.Single(x => x.Key == "hours");
        Equal(ParameterKind.Integer, sleepHours.Kind, "sleep parameter kind");
        Equal(1m, sleepHours.Minimum, "sleep minimum");
        Equal(24m, sleepHours.Maximum, "sleep maximum");
        Equal("6", sleep.Parameters["hours"], "sleep default");
        True(sleepHours.Validate("1.5") is not null, "sleep accepts whole hours only");
        var damageEquipment = presets.Single(x => x.HandlerId == "damage_equipment");
        Equal(2, damageEquipment.ParameterDefinitions.Count, "equipment damage parameter count");
        var damageTarget = damageEquipment.ParameterDefinitions.Single(x => x.Key == "target");
        Equal(ParameterKind.Choice, damageTarget.Kind, "equipment damage target kind");
        SequenceEqual(new[] { "outfit", "helmet", "both" }, damageTarget.Options,
            "equipment damage target options");
        Equal("outfit", damageEquipment.Parameters["target"], "equipment damage default target");
        var damagePercent = damageEquipment.ParameterDefinitions.Single(x => x.Key == "percent");
        Equal(ParameterKind.Percent, damagePercent.Kind, "equipment damage percent kind");
        Equal(1m, damagePercent.Minimum, "equipment damage percent minimum");
        Equal(100m, damagePercent.Maximum, "equipment damage percent maximum");
        Equal("25", damageEquipment.Parameters["percent"], "equipment damage default percent");
        True(damagePercent.Validate("0") is not null && damagePercent.Validate("101") is not null,
            "equipment damage percent bounds enforced");
        True(randomAllSelection.Options.Contains("damage_equipment", StringComparer.Ordinal),
            "equipment damage is enabled in random-all by default");
        SequenceEqual(
            new[] { "god_mode", "get_drunk", "drop_outfit", "damage_equipment", "drop_random_items", "teleport_random", "random_active", "random_all_presets" },
            presets.Where(action => new[] { "god_mode", "get_drunk", "drop_outfit", "damage_equipment", "drop_random_items", "teleport_random", "random_active", "random_all_presets" }
                .Contains(action.HandlerId, StringComparer.Ordinal))
                .Select(action => action.HandlerId),
            "new chaos presets");
        SequenceEqual(
            new[] { "spawn_random_hostile_squad", "spawn_random_mutant_pack" },
            presets.Where(action => new[] { "spawn_random_hostile_squad", "spawn_random_mutant_pack" }
                    .Contains(action.HandlerId, StringComparer.Ordinal))
                .Select(action => action.HandlerId),
            "random spawn presets");
        SequenceEqual(
            new[] { "change_needs", "trigger_emission", "sleep_hours" },
            presets.Where(action => new[] { "change_needs", "trigger_emission", "sleep_hours" }
                    .Contains(action.HandlerId, StringComparer.Ordinal))
                .Select(action => action.HandlerId),
            "needs and world presets");
        return Task.CompletedTask;
    }

    private static Task TestManifestCatalogAndMultiChoiceMigrationAsync()
    {
        var catalog = DefaultActionCatalog.CreatePresets();
        catalog.Add(new ActionDefinition
        {
            Id = "spawn_dogs_3",
            HandlerId = "spawn_dogs_3",
            DisplayName = "Старый совместимый спавн"
        });
        var declaredVisibleHandlers = new HashSet<string>(StringComparer.Ordinal)
        {
            "change_health",
            "teleport_random",
            DonationDispatcher.RandomAllPresetsHandlerId
        };

        CatalogReconciliation.RetainDeclaredActions(catalog, declaredVisibleHandlers);
        DefaultActionCatalog.RefreshRandomAllPresetSelection(catalog);
        SequenceEqual(
            new[] { "change_health", "teleport_random", DonationDispatcher.RandomAllPresetsHandlerId },
            catalog.Select(action => action.HandlerId),
            "compiled presets absent from a readable manifest are removed");
        True(!catalog.Any(action => action.HandlerId == "spawn_dogs_3"),
            "legacy executable compatibility action stays hidden from the editor");

        var selectionDefinition = catalog
            .Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
            .ParameterDefinitions.Single(definition =>
                definition.Key == DefaultActionCatalog.RandomAllSelectionParameterKey);
        SequenceEqual(
            new[] { "change_health", "teleport_random" },
            selectionDefinition.Options,
            "random-all checkboxes contain only concrete handlers declared by the installed addon");

        var partiallyStale = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultActionCatalog.RandomAllSelectionParameterKey] = "change_health,spawn_mutants"
        };
        Equal("change_health",
            CatalogReconciliation.ResolveSavedParameterValue(selectionDefinition, partiallyStale),
            "removed option is dropped without enabling a previously disabled option");
        var intentionallyEmpty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultActionCatalog.RandomAllSelectionParameterKey] = string.Empty
        };
        Equal(string.Empty,
            CatalogReconciliation.ResolveSavedParameterValue(selectionDefinition, intentionallyEmpty),
            "an intentionally empty random-all selection remains empty");
        Equal(selectionDefinition.DefaultValue,
            CatalogReconciliation.ResolveSavedParameterValue(
                selectionDefinition,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            "a newly introduced selection still receives the safe default");
        return Task.CompletedTask;
    }

    private static async Task TestActionPriceOrderingAsync()
    {
        static ActionDefinition Action(string id, params (decimal Amount, string Currency)[] prices) => new()
        {
            Id = id,
            HandlerId = id,
            DisplayName = id,
            IsActive = true,
            Triggers = prices.Select(price => new TriggerRule
            {
                Amount = price.Amount,
                Currency = price.Currency
            }).ToList()
        };

        var actions = new List<ActionDefinition>
        {
            Action("without_price"),
            Action("ten", (10m, "KZT")),
            Action("five_first", (5m, "USD")),
            Action("minimum_of_many", (50m, "RUB"), (2m, "EUR")),
            Action("disabled_cheaper", (1m, "RUB"), (7m, "RUB")),
            Action("five_second", (5m, "RUB"))
        };
        actions.Single(action => action.Id == "disabled_cheaper").Triggers[0].Enabled = false;
        var originalOrder = actions.Select(action => action.Id).ToList();

        var sorted = ActionPriceOrdering.SortByMinimumAssignedAmount(actions);

        SequenceEqual(
            new[] { "minimum_of_many", "five_first", "five_second", "disabled_cheaper", "ten", "without_price" },
            sorted.Select(action => action.Id),
            "actions sorted by raw minimum enabled amount with stable ties and missing prices last");
        SequenceEqual(originalOrder, actions.Select(action => action.Id), "price ordering does not mutate the source list");

        var directory = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsStore(new AppPaths(directory));
            await store.SaveAsync(new AppSettings { Actions = sorted });
            var loaded = await store.LoadAsync();
            SequenceEqual(
                sorted.Select(action => action.Id),
                loaded.Actions.Select(action => action.Id),
                "sorted action order persisted in settings");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static Task TestPresetInstancesAsync()
    {
        var preset = DefaultActionCatalog.CreatePresets().Single(x => x.HandlerId == "spawn_mutants");
        preset.Triggers.Add(new TriggerRule { Amount = 10m, Currency = "RUB" });
        var first = preset.CloneAsNewInstance();
        var second = preset.CloneAsNewInstance();
        True(first.Id != second.Id && first.Id != preset.Id && second.Id != preset.Id, "each activation gets a unique id");
        True(first.Triggers.Single().Id != second.Triggers.Single().Id, "trigger ids are regenerated per instance");
        first.Parameters["species"] = "chimera";
        first.Triggers[0].Amount = 20m;
        Equal("dog", second.Parameters["species"], "parameters are deep-cloned");
        Equal(10m, second.Triggers[0].Amount, "triggers are deep-cloned");
        return Task.CompletedTask;
    }

    private static Task TestLegacySettingsMigrationAsync()
    {
        var dogsTrigger = new TriggerRule { Id = "legacy-trigger", Amount = 42m, Currency = "RUB" };
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            Actions =
            [
                new ActionDefinition
                {
                    Id = "spawn_dogs_3",
                    IsActive = true,
                    CooldownSeconds = 17,
                    Parameters = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["species"] = "dog",
                        ["strength"] = "medium",
                        ["count"] = "3"
                    },
                    Triggers = [dogsTrigger]
                },
                new ActionDefinition { Id = "spawn_mutants", IsActive = false }
            ]
        };

        var migrated = SettingsNormalizer.Normalize(settings);
        Equal(SettingsNormalizer.CurrentSchemaVersion, migrated.SchemaVersion, "settings schema");
        Equal(1, migrated.Actions.Count, "inactive legacy presets are not instances");
        var dogs = migrated.Actions.Single();
        Equal("spawn_dogs_3", dogs.Id, "legacy instance id preserved");
        Equal("spawn_mutants", dogs.HandlerId, "legacy dogs use universal handler");
        Equal(17, dogs.CooldownSeconds, "cooldown preserved");
        Equal("dog", dogs.Parameters["species"], "parameters preserved");
        Equal("3", dogs.Parameters["count"], "count preserved");
        Equal("legacy-trigger", dogs.Triggers.Single().Id, "trigger identity preserved");
        Equal(42m, dogs.Triggers.Single().Amount, "trigger amount preserved");
        return Task.CompletedTask;
    }

    private static Task TestLegacyHandlersNotCollapsedAsync()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            Actions =
            [
                new ActionDefinition
                {
                    Id = "spawn_dogs_3",
                    IsActive = true,
                    CooldownSeconds = 30,
                    Parameters = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["species"] = "dog", ["strength"] = "medium", ["count"] = "3"
                    },
                    Triggers = [new TriggerRule { Amount = 30m, Currency = "RUB" }]
                },
                new ActionDefinition
                {
                    Id = "spawn_mutants",
                    IsActive = true,
                    CooldownSeconds = 30,
                    Parameters = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["species"] = "chimera", ["strength"] = "medium", ["count"] = "1"
                    },
                    Triggers = [new TriggerRule { Amount = 40m, Currency = "RUB" }]
                }
            ]
        };

        var migrated = SettingsNormalizer.Normalize(settings);
        Equal(2, migrated.Actions.Count, "both active legacy configurations survive");
        True(migrated.Actions.All(action => action.HandlerId == "spawn_mutants"), "both use universal handler");
        Equal(2, migrated.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count(), "instance ids remain unique");
        True(migrated.Actions.Any(action => action.Parameters["species"] == "dog" && action.Triggers.Single().Amount == 30m), "dogs preserved");
        True(migrated.Actions.Any(action => action.Parameters["species"] == "chimera" && action.Triggers.Single().Amount == 40m), "chimera preserved");
        return Task.CompletedTask;
    }

    private static Task TestSchemaV2MembershipAsync()
    {
        var instance = DefaultActionCatalog.CreatePresets().Single(action => action.HandlerId == "change_health").CloneAsNewInstance();
        instance.IsActive = false; // The legacy flag is ignored in the active-only v2 collection.
        instance.Triggers = [new TriggerRule { Amount = 50m, Currency = "RUB" }];
        var normalized = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = [instance]
        });
        Equal(1, normalized.Actions.Count, "v2 collection membership is authoritative");
        True(normalized.Actions.Single().IsActive, "v2 instance normalized active");
        Equal(instance.Id, normalized.Actions.Single().Id, "v2 instance id preserved");
        return Task.CompletedTask;
    }

    private static Task TestSchemaV2RequiresHandlerAsync()
    {
        var instance = DefaultActionCatalog.CreatePresets()
            .Single(action => action.HandlerId == "change_health")
            .CloneAsNewInstance();
        instance.HandlerId = string.Empty;
        Throws<InvalidDataException>(() => SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = [instance]
        }), "v2 must not infer a Lua handler from the instance id");
        return Task.CompletedTask;
    }

    private static Task TestDuplicateTriggerIdsRepairedAsync()
    {
        var preset = DefaultActionCatalog.CreatePresets().Single(action => action.HandlerId == "change_health");
        var first = preset.CloneAsNewInstance();
        var second = preset.CloneAsNewInstance();
        first.Triggers = [new TriggerRule { Id = "duplicate", Amount = 10m, Currency = "RUB" }];
        second.Triggers = [new TriggerRule { Id = "duplicate", Amount = 20m, Currency = "RUB" }];

        var normalized = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            Actions = [first, second]
        });

        Equal(2, normalized.Actions.SelectMany(action => action.Triggers)
            .Select(trigger => trigger.Id).Distinct(StringComparer.Ordinal).Count(),
            "trigger ids must be unique after normalization");
        return Task.CompletedTask;
    }

    private static async Task TestSameHandlerInstancesAsync()
    {
        var preset = DefaultActionCatalog.CreatePresets().Single(x => x.HandlerId == "spawn_mutants");
        var dogs = preset.CloneAsNewInstance();
        dogs.Parameters["species"] = "dog";
        dogs.SpawnGroups = [new SpawnGroupEntry { VariantId = "dog", Strength = "medium", Count = 3 }];
        dogs.Triggers = [new TriggerRule { Amount = 10m, Currency = "RUB" }];
        var chimera = preset.CloneAsNewInstance();
        chimera.Parameters["species"] = "chimera";
        chimera.SpawnGroups = [new SpawnGroupEntry { VariantId = "chimera", Strength = "medium", Count = 3 }];
        chimera.Triggers = [new TriggerRule { Amount = 20m, Currency = "RUB" }];
        Equal(0, CatalogValidation.Validate([dogs, chimera]).Count, "duplicate handlers are allowed");

        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history) { GlobalCooldown = TimeSpan.Zero };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.EnqueueAsync(Donation(20m), [dogs, chimera], new QueuePolicy());
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        var command = transport.Commands.Single();
        Equal("spawn_mutants", command.ActionId, "wire uses handler id");
        Equal("chimera*medium*3", command.Parameters[SpawnGroupBundleCodec.ParameterKey],
            "wire uses matched instance group parameters");
        Equal(chimera.Id, history.Entries.Last().ActionId, "history keeps instance id");
        Equal("spawn_mutants", history.Entries.Last().HandlerId, "history keeps handler id");
    }

    private static Task TestExactPriorityAsync()
    {
        var actions = DefaultActionCatalog.Create();
        var exact = actions[0];
        exact.Triggers = [new TriggerRule { Comparator = TriggerComparator.Exact, Amount = 100m, Currency = "RUB" }];
        var greater = actions[1];
        greater.Triggers = [new TriggerRule { Comparator = TriggerComparator.GreaterOrEqual, Amount = 90m, Currency = "RUB" }];
        var matches = RuleMatcher.MatchAll(Donation(100m), [greater, exact]);
        Equal(1, matches.Count, "an exact match must suppress every threshold match");
        Equal(exact.Id, matches[0].Action.Id, "exact must win");
        return Task.CompletedTask;
    }

    private static Task TestHighestThresholdAsync()
    {
        var actions = DefaultActionCatalog.Create();
        actions[0].Triggers = [new TriggerRule { Comparator = TriggerComparator.GreaterOrEqual, Amount = 10m, Currency = "RUB" }];
        actions[1].Triggers = [new TriggerRule { Comparator = TriggerComparator.GreaterOrEqual, Amount = 50m, Currency = "RUB" }];
        actions[2].Triggers = [new TriggerRule { Comparator = TriggerComparator.GreaterOrEqual, Amount = 50m, Currency = "RUB" }];
        var matches = RuleMatcher.MatchAll(Donation(100m), actions.Take(3));
        Equal(2, matches.Count, "only actions tied at the highest >= threshold may run together");
        True(matches.All(match => match.Trigger.Amount == 50m), "a lower >= threshold leaked into the winning group");
        SequenceEqual(new[] { actions[1].Id, actions[2].Id }, matches.Select(match => match.Action.Id),
            "highest >= threshold ties preserve active-list order");
        return Task.CompletedTask;
    }

    private static Task TestStrictCurrencyAsync()
    {
        var action = DefaultActionCatalog.Create()[0];
        action.Triggers[0].Amount = 1m;
        action.Triggers[0].Currency = "USD";
        Null(RuleMatcher.Match(Donation(1m, "RUB"), [action]), "no FX conversion");
        return Task.CompletedTask;
    }

    private static Task TestMoneyAsync()
    {
        Equal(101L, Money.ToMinor(1.005m, "RUB"), "away-from-zero rounding");
        Equal(1234L, Money.ToMinor(12.34m, "USD"), "USD minor");
        Equal(12.34m, Money.FromMinor(1234, "EUR"), "EUR from minor");
        return Task.CompletedTask;
    }

    private static Task TestDuplicateTriggerAsync()
    {
        var actions = DefaultActionCatalog.Create().Take(2).ToList();
        actions[0].Triggers = [new TriggerRule { Amount = 10m, Currency = "RUB" }];
        actions[1].Triggers = [new TriggerRule { Amount = 10m, Currency = "RUB" }];
        Equal(0, CatalogValidation.Validate(actions).Count, "the same price is allowed for separate active actions");
        actions[0].Triggers.Add(new TriggerRule { Amount = 10m, Currency = "RUB" });
        True(actions[0].Validate().Any(message => message.Contains("повтор", StringComparison.OrdinalIgnoreCase)),
            "duplicate conditions inside one action remain invalid");
        return Task.CompletedTask;
    }

    private static async Task TestSamePriceDispatchAsync()
    {
        var actions = DefaultActionCatalog.Create().Take(2).ToList();
        foreach (var action in actions)
        {
            action.CooldownSeconds = 0;
            action.Triggers = [new TriggerRule { Amount = 25m, Currency = "RUB" }];
        }
        var matches = RuleMatcher.MatchAll(Donation(25m), actions);
        SequenceEqual(actions.Select(action => action.Id), matches.Select(match => match.Action.Id),
            "matching preserves active-list order");

        var donation = Donation(25m);
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history) { GlobalCooldown = TimeSpan.Zero };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        Equal(DispatchState.Queued, await dispatcher.EnqueueAsync(donation, actions, new QueuePolicy()),
            "same-price donation is accepted as one group");
        Equal(2, dispatcher.Count, "both actions are placed in the queue");
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(actions[0].HandlerId, transport.Commands[0].ActionId, "first active action is sent first");
        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = transport.Commands[0].CommandId,
            Status = GameResultStatus.Executed
        });
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(actions[1].HandlerId, transport.Commands[1].ActionId, "second active action follows after ACK");

        var cancelHistory = new InMemoryHistorySink();
        var cancelDispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), cancelHistory)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var cancelDonation = Donation(25m);
        await cancelDispatcher.EnqueueAsync(cancelDonation, actions, new QueuePolicy());
        var queued = cancelHistory.Entries.Where(entry => entry.State == DispatchState.Queued).ToList();
        True(await cancelDispatcher.CancelQueuedAsync(queued[0]) is { State: DispatchState.Cancelled },
            "one selected command can be removed from a same-donation group");
        Equal(1, cancelDispatcher.Count, "removing one same-price action keeps its sibling queued");
        var remainingTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await cancelDispatcher.DrainAsync(remainingTransport, new QueuePolicy());
        Equal(actions[1].HandlerId, remainingTransport.Commands.Single().ActionId,
            "the remaining same-price action still executes");
    }

    private static async Task TestQueueSnapshotAsync()
    {
        var now = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var actions = DefaultActionCatalog.Create().Take(2).ToList();
        foreach (var action in actions)
        {
            action.CooldownSeconds = 0;
            action.Triggers = [new TriggerRule { Amount = 25m, Currency = "RUB" }];
        }
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(),
            new InMemoryHistorySink(),
            () => now)
        {
            GlobalCooldown = TimeSpan.FromSeconds(5)
        };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.EnqueueAsync(Donation(25m), actions, new QueuePolicy());

        var queued = await dispatcher.GetTimingAsync(true, true);
        Equal(actions[0].DisplayName, queued.ActionName, "snapshot names the queue head");
        Equal(2, queued.WaitingCount, "snapshot reports queued actions");
        Equal(0, queued.SecondsUntilNext, "first action is immediately eligible");

        await dispatcher.DrainAsync(transport, new QueuePolicy());
        var inFlight = await dispatcher.GetTimingAsync(true, true);
        True(inFlight.AwaitingResult, "sent action is reported as in flight");
        Equal(actions[0].DisplayName, inFlight.ActionName, "in-flight action name is retained");

        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = transport.Commands[0].CommandId,
            Status = GameResultStatus.Executed
        });
        now = now.AddSeconds(2);
        var coolingDown = await dispatcher.GetTimingAsync(true, true);
        Equal(actions[1].DisplayName, coolingDown.ActionName, "snapshot advances to the next action");
        Equal(3, coolingDown.SecondsUntilNext, "snapshot uses the real global cooldown deadline");
    }

    private static Task TestEventHistoryProjectionAsync()
    {
        var created = DateTimeOffset.Parse("2026-09-22T12:00:00Z");
        var queued = new EventHistoryEntry
        {
            EventId = "event-queued", CommandId = "command-1", DonationId = "donation-1",
            ActionId = "action-1", DonationCreatedAt = created, Timestamp = created, State = DispatchState.Queued
        };
        var sent = new EventHistoryEntry
        {
            EventId = "event-sent", CommandId = "command-1", DonationId = "donation-1",
            ActionId = "action-1", DonationCreatedAt = created, Timestamp = created.AddSeconds(1), State = DispatchState.Sent
        };
        var executed = new EventHistoryEntry
        {
            EventId = "event-executed", CommandId = "command-2", DonationId = "donation-1",
            ActionId = "action-1", DonationCreatedAt = created, Timestamp = created.AddSeconds(2), State = DispatchState.Executed
        };
        var visible = EventHistoryProjection.LatestPerCommand([queued, sent, executed]);
        Equal(1, visible.Count, "one command occupies one history row");
        Equal(DispatchState.Executed, visible[0].State, "latest command state replaces earlier transitions");
        True(EventHistoryProjection.IsSameLogicalEvent(queued, executed),
            "deferred attempts remain one logical history row");
        return Task.CompletedTask;
    }

    private static async Task TestActionCooldownFairnessAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-22T12:10:00Z");
        var firstAction = DefaultActionCatalog.Create().First().Clone();
        firstAction.CooldownSeconds = 60;
        firstAction.Triggers = [new TriggerRule { Amount = 1m, Currency = "RUB" }];
        var secondAction = DefaultActionCatalog.Create().Skip(1).First().Clone();
        secondAction.CooldownSeconds = 0;
        secondAction.Triggers = [new TriggerRule { Amount = 2m, Currency = "RUB" }];
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => now)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };

        await dispatcher.EnqueueAsync(Donation(1m), [firstAction, secondAction], new QueuePolicy());
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = transport.Commands.Single().CommandId,
            Status = GameResultStatus.Executed
        });
        await dispatcher.EnqueueAsync(Donation(1m), [firstAction, secondAction], new QueuePolicy());
        await dispatcher.EnqueueAsync(Donation(2m), [firstAction, secondAction], new QueuePolicy());
        await dispatcher.DrainAsync(transport, new QueuePolicy());

        Equal(secondAction.HandlerId, transport.Commands[^1].ActionId,
            "cooling action rotates behind a runnable effect");
    }

    private static async Task TestPendingQueueRestoreAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "ChroniclesDonationBridge.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(directory);
            var processed = new JsonLineProcessedDonationStore(paths);
            var pendingStore = new JsonPendingDispatchStore(paths);
            var action = DefaultActionCatalog.Create().First().Clone();
            action.CooldownSeconds = 0;
            action.Triggers = [new TriggerRule { Amount = 1m, Currency = "RUB" }];
            var firstDonation = Donation(1m);
            var secondDonation = Donation(1m);
            var first = new DonationDispatcher(
                processed, new InMemoryHistorySink(), pendingStore: pendingStore)
            {
                GlobalCooldown = TimeSpan.Zero
            };
            await first.EnqueueAsync(firstDonation, [action], new QueuePolicy());
            await first.EnqueueAsync(secondDonation, [action], new QueuePolicy());

            var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
            await first.DrainAsync(transport, new QueuePolicy());
            Equal(firstDonation.Id, transport.Commands.Single().DonationId,
                "first command entered in-flight state");

            var restoredHistory = new InMemoryHistorySink();
            var restored = new DonationDispatcher(
                processed, restoredHistory, pendingStore: pendingStore)
            {
                GlobalCooldown = TimeSpan.Zero
            };
            Equal(1, await restored.RestoreAsync(),
                "waiting command survives restart in original order");
            True(restoredHistory.Entries.Any(entry =>
                    entry.DonationId == firstDonation.Id && entry.State == DispatchState.Uncertain),
                "interrupted in-flight command is never repeated automatically");
            await restored.DrainAsync(transport, new QueuePolicy());
            Equal(secondDonation.Id, transport.Commands[^1].DonationId,
                "next durable command continues after interrupted send");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static Task TestItemBundleAsync()
    {
        var action = DefaultActionCatalog.CreatePresets().Single(item => item.HandlerId == "spawn_items").Clone();
        action.ItemSpawns =
        [
            new ItemSpawnEntry { ItemId = "medkit", Count = 2 },
            new ItemSpawnEntry { ItemId = "bread", Count = 3 }
        ];
        Equal(0, action.Validate().Count, "a mixed item bundle is valid");
        var parameters = action.EffectiveParameters();
        Equal("medkit*2;bread*3", parameters[ItemBundleCodec.ParameterKey], "all rows use one bounded parameter");
        True(!parameters.ContainsKey("item") && !parameters.ContainsKey("count"), "legacy fields are not sent with bundle");
        action.ItemSpawns[1].Count = 51;
        True(action.Validate().Any(error => error.Contains("50", StringComparison.Ordinal)), "each row remains quantity-bounded");
        action.ItemSpawns.Clear();
        True(action.Validate().Any(error => error.Contains("предмет", StringComparison.OrdinalIgnoreCase)), "empty bundle is rejected");
        return Task.CompletedTask;
    }

    private static Task TestSpawnGroupBundleAsync()
    {
        var mutants = DefaultActionCatalog.CreatePresets().Single(item => item.HandlerId == "spawn_mutants").Clone();
        mutants.SpawnGroups =
        [
            new SpawnGroupEntry { VariantId = "dog", Strength = "weak", Count = 3 },
            new SpawnGroupEntry { VariantId = "chimera", Strength = "strong", Count = 1 }
        ];
        Equal(0, mutants.Validate().Count, "a mixed mutant group list is valid");
        var parameters = mutants.EffectiveParameters();
        Equal("dog*weak*3;chimera*strong*1", parameters[SpawnGroupBundleCodec.ParameterKey],
            "all mutant rows use one bounded parameter");
        True(!parameters.ContainsKey("species") && !parameters.ContainsKey("strength") &&
            !parameters.ContainsKey("count"), "legacy mutant fields are not sent with a bundle");

        var npcs = DefaultActionCatalog.CreatePresets().Single(item => item.HandlerId == "spawn_npcs").Clone();
        npcs.SpawnGroups =
        [
            new SpawnGroupEntry { VariantId = "bandit", Strength = "medium", Count = 2 },
            new SpawnGroupEntry { VariantId = "duty", Strength = "strong", Count = 4 }
        ];
        Equal("bandit*medium*2;duty*strong*4",
            npcs.EffectiveParameters()[SpawnGroupBundleCodec.ParameterKey], "NPC group rows changed order");
        npcs.SpawnGroups[1].Count = 100;
        True(npcs.Validate().Any(error => error.Contains("99", StringComparison.Ordinal)),
            "each NPC row remains quantity-bounded");
        npcs.SpawnGroups.Clear();
        True(npcs.Validate().Any(error => error.Contains("групп", StringComparison.OrdinalIgnoreCase)),
            "empty NPC group list is rejected");
        return Task.CompletedTask;
    }

    private static async Task TestSkipReadyAsync()
    {
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history);
        await dispatcher.EnqueueAsync(Donation(1m), DefaultActionCatalog.Create(), new QueuePolicy { Mode = QueueMode.Skip });
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.DrainAsync(transport, new QueuePolicy { Mode = QueueMode.Skip });
        Equal(1, transport.Commands.Count, "ready skip policy must send");
    }

    private static async Task TestSkipUnavailableAsync()
    {
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history);
        await dispatcher.EnqueueAsync(Donation(1m), DefaultActionCatalog.Create(), new QueuePolicy { Mode = QueueMode.Skip });
        await dispatcher.DrainAsync(new FakeTransport(), new QueuePolicy { Mode = QueueMode.Skip });
        Equal(1, dispatcher.Count, "legacy skip mode is migrated to guaranteed waiting");
        True(history.Entries.Any(x => x.State == DispatchState.Queued), "unavailable donation remains queued");
        True(history.Entries.All(x => x.State != DispatchState.Skipped), "unavailable donation is never discarded");
    }

    private static async Task TestQueueTtlAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history, () => now);
        var policy = new QueuePolicy { Mode = QueueMode.WaitForDuration, WaitMinutes = 1 };
        await dispatcher.EnqueueAsync(Donation(1m), DefaultActionCatalog.Create(), policy);
        now = now.AddMinutes(2);
        await dispatcher.DrainAsync(new FakeTransport(), policy);
        Equal(1, dispatcher.Count, "legacy TTL cannot expire an accepted donation");
        True(history.Entries.All(x => x.State != DispatchState.Expired), "accepted donation never expires");
    }

    private static async Task TestUncertainAsync()
    {
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history);
        var donation = Donation(1m);
        await dispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy());
        await dispatcher.DrainAsync(new FakeTransport { IsConnected = true, IsGameReady = true, ThrowOnSend = true }, new QueuePolicy());
        Equal(DispatchState.Uncertain, (await processed.FindAsync(donation.Id))?.State, "uncertain ledger");
        Equal(DispatchState.Duplicate, await dispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy()), "no auto duplicate");
    }

    private static Task TestWireEncodingAsync()
    {
        var command = new GameCommand
        {
            CommandId = "cmd 1",
            DonationId = "донат;=1",
            ActionId = "spawn_items",
            AmountMinor = 100,
            Currency = "RUB",
            Parameters = new() { ["item"] = "medkit;special=1", ["note"] = "ёж" }
        };
        var fields = PipeProtocol.Command(command).Split('\t');
        Equal("донат;=1", PipeProtocol.PercentDecode(fields[2]), "outer regular field");
        var structured = PipeProtocol.PercentDecode(fields[6]);
        var parsed = PipeProtocol.ParseStructuredParameters(structured);
        Equal("medkit;special=1", parsed["item"], "nested separator-safe parameter");
        Equal("ёж", parsed["note"], "UTF-8 parameter");
        return Task.CompletedTask;
    }

    private static Task TestWireLimitAsync()
    {
        var command = new GameCommand
        {
            CommandId = "c",
            DonationId = "d",
            ActionId = "spawn_items",
            Currency = "RUB",
            Parameters = new() { ["item"] = new string('x', PipeProtocol.MaximumLineBytes) }
        };
        Throws<InvalidDataException>(() => PipeProtocol.Command(command), "oversized command");
        return Task.CompletedTask;
    }

    private static Task TestFirstRunRecoveryAsync()
    {
        var selected = DonationRecoveryPlanner.Select(new RecoveryCheckpoint(), [Donation(1m)], new QueuePolicy(), DateTimeOffset.UtcNow);
        Equal(0, selected.Count, "first run no backlog");
        return Task.CompletedTask;
    }

    private static Task TestRecoveryTtlAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        var recent = Donation(1m); recent.Id = "recent"; recent.CreatedAt = now.AddMinutes(-2);
        var old = Donation(1m); old.Id = "old"; old.CreatedAt = now.AddMinutes(-20);
        var selected = DonationRecoveryPlanner.Select(
            new RecoveryCheckpoint { Initialized = true, LastCreatedAt = now.AddHours(-1), LastDonationId = "checkpoint" },
            [old, recent],
            new QueuePolicy { Mode = QueueMode.WaitForDuration, WaitMinutes = 5 }, now);
        SequenceEqual(new[] { "recent" }, selected.Select(x => x.Id), "TTL recovery");
        return Task.CompletedTask;
    }

    private static Task TestRecoveryContinuityAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
        var queuedOlder = Donation(1m); queuedOlder.Id = "queued-older"; queuedOlder.CreatedAt = now.AddMinutes(-4);
        var terminalNewer = Donation(99m); terminalNewer.Id = "terminal-newer"; terminalNewer.CreatedAt = now.AddMinutes(-1);
        var selected = DonationRecoveryPlanner.Select(
            new RecoveryCheckpoint
            {
                Initialized = true,
                LastDonationId = terminalNewer.Id,
                LastCreatedAt = terminalNewer.CreatedAt
            },
            [terminalNewer, queuedOlder],
            new QueuePolicy { Mode = QueueMode.WaitForDuration, WaitMinutes = 5 },
            now);
        SequenceEqual(new[] { "queued-older", "terminal-newer" }, selected.Select(item => item.Id), "ledger, not checkpoint, decides terminal state");
        return Task.CompletedTask;
    }

    private static Task TestSubscriptionResponseAsync()
    {
        const string channel = "$alerts:donation_42";
        using var arrayDocument = JsonDocument.Parse("{\"channels\":[{\"channel\":\"$alerts:donation_42\",\"token\":\"array-token\"}]}");
        Equal("array-token", DonationAlertsRestClient.ParseCentrifugeSubscriptionToken(arrayDocument.RootElement, channel), "official array response");
        using var legacyDocument = JsonDocument.Parse("{\"channels\":{\"$alerts:donation_42\":{\"token\":\"object-token\"}}}");
        Equal("object-token", DonationAlertsRestClient.ParseCentrifugeSubscriptionToken(legacyDocument.RootElement, channel), "defensive object response");
        return Task.CompletedTask;
    }

    private static async Task TestJsonSettingsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.Tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(directory);
        try
        {
            var store = new JsonSettingsStore(paths);
            var settings = new AppSettings
            {
                GamePath = "C:\\Game",
                Armed = true,
                MinimizeToTrayOnClose = false,
                UseLightTheme = true,
                LimitDonationAlertsApiRequests = false
            };
            var instanceId = settings.Actions[0].Id;
            var handlerId = settings.Actions[0].HandlerId;
            settings.Actions[0].Triggers.Add(new TriggerRule { Amount = 42m, Currency = "USD" });
            var randomAll = DefaultActionCatalog.CreatePresets()
                .Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
                .CloneAsNewInstance();
            randomAll.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] =
                "change_health,teleport_random";
            settings.Actions.Add(randomAll);
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Equal("C:\\Game", loaded.GamePath, "game path");
            True(loaded.Armed, "armed");
            True(!loaded.MinimizeToTrayOnClose, "tray close setting persisted");
            True(loaded.UseLightTheme, "light theme setting persisted");
            True(!loaded.LimitDonationAlertsApiRequests, "API limiter setting persisted");
            Equal(instanceId, loaded.Actions[0].Id, "instance id persisted");
            Equal(handlerId, loaded.Actions[0].HandlerId, "handler id persisted");
            True(loaded.Actions[0].Triggers.Any(x => x.Amount == 42m && x.Currency == "USD"), "trigger persisted");
            Equal(
                "change_health,teleport_random",
                loaded.Actions.Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
                    .Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey],
                "random-all checkbox selection persisted as an ordinary action parameter");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestDpapiAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.Tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(directory);
        try
        {
            var store = new DpapiSecretStore(paths);
            await store.SaveAsync(new SecretBundle { ClientSecret = "secret", AccessToken = "access", RefreshToken = "refresh" });
            var loaded = await store.LoadAsync();
            Equal("secret", loaded.ClientSecret, "DPAPI secret");
            var raw = await File.ReadAllTextAsync(paths.SecretsFile);
            True(!raw.Contains("secret", StringComparison.Ordinal), "secret not plaintext");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestLogRedactionAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.Tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(directory);
        try
        {
            var logger = new SafeFileLogger(paths);
            await logger.WriteAsync("ERROR", "client_secret=alpha access_token:\"beta\" refresh_token=gamma Authorization: Bearer delta-token-123456 eyJabcdefghijklmnop.qwertyuiop");
            var text = await File.ReadAllTextAsync(paths.LogFile);
            foreach (var secret in new[] { "alpha", "beta", "gamma", "delta-token-123456", "eyJabcdefghijklmnop" })
            {
                True(!text.Contains(secret, StringComparison.Ordinal), "log leaked " + secret);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestRefreshTokenAsync()
    {
        using var http = new HttpClient(new DelegateHandler(_ => JsonResponse("{\"access_token\":\"new-access\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")));
        var client = new DonationAlertsRestClient(http, PassThroughRateLimiter.Instance);
        var tokens = await client.RefreshTokenAsync("id", "secret", "old-refresh");
        Equal("old-refresh", tokens.RefreshToken, "refresh endpoint may omit refresh_token");

        using var authHttp = new HttpClient(new DelegateHandler(_ => JsonResponse("{\"access_token\":\"access\",\"expires_in\":3600}")));
        var authClient = new DonationAlertsRestClient(authHttp, PassThroughRateLimiter.Instance);
        await ThrowsAsync<InvalidDataException>(() => authClient.ExchangeAuthorizationCodeAsync("id", "secret", "code"), "initial grant must require refresh_token");
    }

    private static async Task TestOAuthLoopbackAsync()
    {
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal) == true)
            {
                return JsonResponse("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            }
            return JsonResponse("{\"data\":{\"id\":42,\"code\":\"test_channel\",\"name\":\"Tester\",\"socket_connection_token\":\"socket\"}}");
        }));
        var flow = new LoopbackOAuthFlow(new DonationAlertsRestClient(http, PassThroughRateLimiter.Instance));
        Task? callbackTask = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await flow.AuthorizeAsync("client", "secret", authorizationUri =>
        {
            var state = QueryValue(authorizationUri.Query, "state");
            callbackTask = Task.Run(async () =>
            {
                using var callbackClient = new HttpClient();
                _ = await callbackClient.GetStringAsync(DonationAlertsEndpoints.RedirectUri + "?code=test-code&state=" + Uri.EscapeDataString(state), timeout.Token);
            }, timeout.Token);
        }, timeout.Token);
        if (callbackTask is not null) await callbackTask;
        Equal("test_channel", result.Profile.Code, "profile code");
        Equal("refresh", result.Tokens.RefreshToken, "authorization tokens");

        await ThrowsAsync<InvalidDataException>(() => flow.AuthorizeAsync("client", "secret", _authorizationUri =>
        {
            _ = Task.Run(async () =>
            {
                using var callbackClient = new HttpClient();
                _ = await callbackClient.GetStringAsync(DonationAlertsEndpoints.RedirectUri + "?code=test-code&state=wrong", timeout.Token);
            }, timeout.Token);
        }, timeout.Token), "mismatched state must fail");
    }

    private static async Task TestDonationAlertsApiRateLimiterAsync()
    {
        Equal(60, DonationAlertsApiRateLimiter.DefaultMaximumRequests, "default rolling-window request limit");
        Equal(TimeSpan.FromMinutes(1), DonationAlertsApiRateLimiter.DefaultWindow, "default rolling window");
        Equal(TimeSpan.FromSeconds(1), DonationAlertsApiRateLimiter.DefaultMinimumInterval, "default request spacing");

        var enabled = true;
        var now = DateTimeOffset.Parse("2026-09-04T00:00:00Z");
        var delays = new List<TimeSpan>();
        using (var limiter = new DonationAlertsApiRateLimiter(
            () => enabled,
            maximumRequests: 60,
            window: TimeSpan.FromMinutes(1),
            minimumInterval: TimeSpan.FromSeconds(1),
            utcNow: () => now,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                now += duration;
                return Task.CompletedTask;
            }))
        {
            await limiter.WaitAsync();
            await limiter.WaitAsync();
            Equal(1, delays.Count, "second request is delayed");
            Equal(TimeSpan.FromSeconds(1), delays[0], "safe one-request-per-second spacing");

            enabled = false;
            await limiter.WaitAsync();
            Equal(1, delays.Count, "disabled limiter does not delay HTTP requests");
        }

        now = DateTimeOffset.Parse("2026-09-04T01:00:00Z");
        delays.Clear();
        using var rollingLimiter = new DonationAlertsApiRateLimiter(
            () => true,
            maximumRequests: 2,
            window: TimeSpan.FromMinutes(1),
            minimumInterval: TimeSpan.Zero,
            utcNow: () => now,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                now += duration;
                return Task.CompletedTask;
            });
        await rollingLimiter.WaitAsync();
        await rollingLimiter.WaitAsync();
        await rollingLimiter.WaitAsync();
        Equal(1, delays.Count, "rolling-window overflow is delayed");
        Equal(TimeSpan.FromMinutes(1), delays[0], "rolling-window limit waits for the oldest request to expire");
    }

    private static async Task TestDonationAlertsRestUsesRateLimiterAsync()
    {
        var limiter = new CountingRateLimiter();
        using var http = new HttpClient(new DelegateHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/oauth/token" => JsonResponse("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"),
            "/api/v1/user/oauth" => JsonResponse("{\"data\":{\"id\":42,\"code\":\"test_channel\",\"name\":\"Tester\",\"socket_connection_token\":\"socket\"}}"),
            "/api/v1/alerts/donations" => JsonResponse("{\"data\":[],\"meta\":{\"current_page\":1,\"last_page\":1}}"),
            "/api/v1/centrifuge/subscribe" => JsonResponse("{\"data\":{\"channels\":[{\"channel\":\"$alerts:donation_42\",\"token\":\"subscription\"}]}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new DonationAlertsRestClient(http, limiter);

        await client.ExchangeAuthorizationCodeAsync("id", "secret", "code");
        await client.GetProfileAsync("access");
        await client.GetDonationsAsync("access");
        await client.CreateCentrifugeSubscriptionAsync("access", 42, "client");

        Equal(4, limiter.WaitCount, "every outbound DonationAlerts HTTP request is rate-limited");
    }

    private static async Task TestIpcAppFirstAsync()
    {
        var pipeName = CreateTestPipeName();
        await using var server = new NamedPipeGameServer("1.0.0", pipeName);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<GameExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConnectionChanged += state => { if (state.Ready) ready.TrySetResult(true); };
        server.ResultReceived += value => result.TrySetResult(value);
        server.Start();

        using var client = await ConnectPipeAsync(pipeName);
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { NewLine = "\n", AutoFlush = true };
        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t1234");
        True((await reader.ReadLineAsync())?.StartsWith("WELCOME\t1\t", StringComparison.Ordinal) == true, "welcome");
        await writer.WriteLineAsync("STATUS\tready\tok");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await server.SendAsync(new GameCommand { CommandId = "cmd", DonationId = "don", ActionId = "add_radiation", AmountMinor = 100, Currency = "RUB", Parameters = new() { ["percent"] = "25" } });
        True((await reader.ReadLineAsync())?.StartsWith("COMMAND\tcmd\tdon\tadd_radiation", StringComparison.Ordinal) == true, "command line");
        await writer.WriteLineAsync("RESULT\tcmd\texecuted\tok");
        Equal(GameResultStatus.Executed, (await result.Task.WaitAsync(TimeSpan.FromSeconds(3))).Status, "result status");
    }

    private static async Task TestIpcIdleWithoutGameAsync()
    {
        await using var server = new NamedPipeGameServer("1.0.0", CreateTestPipeName());
        server.Start();
        await Task.Delay(300);
        True(!server.IsConnected, "server must remain idle while the game is absent");
        True(!server.IsGameReady, "absent game cannot be reported ready");
    }

    private static async Task TestCommandResultWatchdogAsync()
    {
        var pipeName = CreateTestPipeName();
        await using var server = new NamedPipeGameServer(
            "1.0.0",
            pipeName,
            pingInterval: TimeSpan.FromMilliseconds(20),
            heartbeatSilence: TimeSpan.FromSeconds(2),
            resultTimeout: TimeSpan.FromMilliseconds(50),
            processIsAlive: _ => true,
            terminalResultTimeout: TimeSpan.FromMilliseconds(150));
        var result = new TaskCompletionSource<GameExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ResultReceived += value => result.TrySetResult(value);
        server.Start();

        using var client = await ConnectPipeAsync(pipeName);
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true)
        {
            NewLine = "\n",
            AutoFlush = true
        };
        await writer.WriteLineAsync("HELLO\t1\t1.0.0\t1234");
        _ = await reader.ReadLineAsync();
        await writer.WriteLineAsync("STATUS\tready\tok");
        var readyDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!server.IsGameReady && DateTimeOffset.UtcNow < readyDeadline)
        {
            await Task.Delay(10);
        }
        True(server.IsGameReady, "watchdog test game becomes ready");

        await server.SendAsync(new GameCommand
        {
            CommandId = "watchdog-command",
            DonationId = "watchdog-donation",
            ActionId = "add_radiation",
            AmountMinor = 100,
            Currency = "RUB",
            Parameters = new() { ["percent"] = "25" }
        });
        var terminal = await result.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(GameResultStatus.Uncertain, terminal.Status,
            "missing result becomes terminal instead of waiting forever");
        True(terminal.Reason.Contains("автоматический повтор запрещён", StringComparison.Ordinal),
            "watchdog preserves at-most-once semantics");
        True(!server.IsAwaitingResult("watchdog-command"),
            "watchdog releases the in-flight command");
    }

    private static async Task TestIpcGameFirstAsync()
    {
        var pipeName = CreateTestPipeName();
        await using var server = new NamedPipeGameServer("1.0.0", pipeName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connectTask = client.ConnectAsync(cancellation.Token);
        await Task.Delay(100, cancellation.Token);
        server.Start();
        await connectTask;
        using (client)
        {
            var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true);
            var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { NewLine = "\n", AutoFlush = true };
            await writer.WriteLineAsync("HELLO\t1\t1.0.0\t4321");
            _ = await reader.ReadLineAsync(cancellation.Token);
            await writer.WriteLineAsync("STATUS\tready\tok");
            await Task.Delay(100, cancellation.Token);
            var uncertain = new TaskCompletionSource<GameExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ResultReceived += value => { if (value.CommandId == "lost") uncertain.TrySetResult(value); };
            await server.SendAsync(new GameCommand { CommandId = "lost", DonationId = "don2", ActionId = "drop_active_weapon", AmountMinor = 200, Currency = "RUB" }, cancellation.Token);
            _ = await reader.ReadLineAsync(cancellation.Token);
            writer.Dispose();
            reader.Dispose();
            client.Close();
            Equal(GameResultStatus.Uncertain, (await uncertain.Task.WaitAsync(TimeSpan.FromSeconds(4))).Status, "ACK loss becomes uncertain");
        }
    }

    private static async Task TestDeferredRetryAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T00:00:00Z");
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history, () => now) { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy { Mode = QueueMode.WaitForDuration, WaitMinutes = 1 };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.EnqueueAsync(Donation(1m), DefaultActionCatalog.Create(), policy);
        await dispatcher.DrainAsync(transport, policy);
        var first = transport.Commands.Single();
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = first.CommandId, Status = GameResultStatus.Deferred, Reason = "dialog" });
        Equal(1, dispatcher.Count, "deferred must requeue for wait policy");
        now = now.AddSeconds(6);
        await dispatcher.DrainAsync(transport, policy);
        Equal(2, transport.Commands.Count, "deferred retry sent later");
        True(first.CommandId != transport.Commands[1].CommandId, "deferred retry must use a fresh command id");
        Equal(first.DonationId, transport.Commands[1].DonationId, "retry keeps the original donation id");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = transport.Commands[1].CommandId, Status = GameResultStatus.Deferred, Reason = "still loading" });
        now = now.AddMinutes(2);
        await dispatcher.DrainAsync(transport, policy);
        Equal(3, transport.Commands.Count, "deferred command remains executable after legacy TTL");
        True(history.Entries.All(item => item.State != DispatchState.Expired), "deferred command never expires");

        var skipDispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), new InMemoryHistorySink(), () => now) { GlobalCooldown = TimeSpan.Zero };
        var skipTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        var skipPolicy = new QueuePolicy { Mode = QueueMode.Skip };
        await skipDispatcher.EnqueueAsync(Donation(1m), DefaultActionCatalog.Create(), skipPolicy);
        await skipDispatcher.DrainAsync(skipTransport, skipPolicy);
        await skipDispatcher.CompleteAsync(new GameExecutionResult { CommandId = skipTransport.Commands.Single().CommandId, Status = GameResultStatus.Deferred, Reason = "busy" });
        Equal(1, skipDispatcher.Count, "legacy skip policy also preserves deferred commands");
    }

    private static async Task TestQueuedRecoveryAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T01:00:00Z");
        var processed = new InMemoryProcessedDonationStore();
        var donation = Donation(1m);
        var firstDispatcher = new DonationDispatcher(processed, new InMemoryHistorySink(), () => now);
        await firstDispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy());
        Equal(DispatchState.Queued, (await processed.FindAsync(donation.Id))?.State, "queued state persisted");

        var restoredDispatcher = new DonationDispatcher(processed, new InMemoryHistorySink(), () => now) { GlobalCooldown = TimeSpan.Zero };
        Equal(DispatchState.Queued, await restoredDispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy()), "queued ledger can be recovered after restart");
        var secondDonation = Donation(2m);
        await restoredDispatcher.EnqueueAsync(secondDonation, DefaultActionCatalog.Create(), new QueuePolicy());
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await restoredDispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(1, transport.Commands.Count, "only one command may be in flight");
        now = now.AddMinutes(1);
        await restoredDispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(1, transport.Commands.Count, "second command waits for result");
        await restoredDispatcher.CompleteAsync(new GameExecutionResult { CommandId = transport.Commands[0].CommandId, Status = GameResultStatus.Executed });
        await restoredDispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(2, transport.Commands.Count, "second command sends after first result");
    }

    private static async Task TestQueueCancellationAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T01:30:00Z");
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history, () => now) { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };
        var actions = DefaultActionCatalog.Create();
        foreach (var action in actions) action.CooldownSeconds = 0;

        var first = Donation(1m);
        var second = Donation(2m);
        var third = Donation(3m);
        await dispatcher.EnqueueAsync(first, actions, policy);
        await dispatcher.EnqueueAsync(second, actions, policy);
        await dispatcher.EnqueueAsync(third, actions, policy);
        Equal(3, dispatcher.Count, "three events queued");

        var secondQueued = history.Entries.Single(entry => entry.DonationId == second.Id && entry.State == DispatchState.Queued);
        var cancelled = await dispatcher.CancelQueuedAsync(secondQueued);
        True(cancelled is not null, "selected queued event cancelled");
        Equal(2, dispatcher.Count, "only one event removed");
        True(!dispatcher.IsQueued(second.Id), "cancelled donation no longer queued");
        Equal(DispatchState.Cancelled, (await processed.FindAsync(second.Id))?.State, "cancelled state persisted in ledger");
        Equal(secondQueued.CommandId, cancelled!.CommandId, "cancel history keeps command id");
        Equal(secondQueued.ActionId, cancelled.ActionId, "cancel history keeps action id");
        True(cancelled.CanRetryManually, "cancelled event can only be retried manually");
        True(cancelled.Detail.Contains("Удалено из очереди", StringComparison.Ordinal), "cancel reason visible in history");
        Null(await dispatcher.CancelQueuedAsync("missing-donation"), "unknown cancellation is a no-op");

        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.DrainAsync(transport, policy);
        Equal(first.Id, transport.Commands.Single().DonationId, "first event keeps FIFO position");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = transport.Commands[0].CommandId, Status = GameResultStatus.Executed });
        await dispatcher.DrainAsync(transport, policy);
        Equal(third.Id, transport.Commands[1].DonationId, "third event follows first after middle removal");
        True(transport.Commands.All(command => command.DonationId != second.Id), "cancelled event never reaches IPC");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = transport.Commands[1].CommandId, Status = GameResultStatus.Executed });

        var restored = new DonationDispatcher(processed, new InMemoryHistorySink(), () => now);
        Equal(DispatchState.Duplicate, await restored.EnqueueAsync(second, actions, policy), "cancelled donation cannot auto-recover");

        var sent = Donation(1m);
        await dispatcher.EnqueueAsync(sent, actions, policy);
        await dispatcher.DrainAsync(transport, policy);
        var sentCommand = transport.Commands[^1];
        Equal(sent.Id, sentCommand.DonationId, "sent test command dispatched");
        Null(await dispatcher.CancelQueuedAsync(sent.Id), "sent event cannot be cancelled");
        Equal(DispatchState.Sent, (await processed.FindAsync(sent.Id))?.State, "failed cancellation leaves sent ledger intact");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = sentCommand.CommandId, Status = GameResultStatus.Executed });

        var deferred = Donation(2m);
        await dispatcher.EnqueueAsync(deferred, actions, policy);
        await dispatcher.DrainAsync(transport, policy);
        var deferredCommand = transport.Commands[^1];
        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = deferredCommand.CommandId,
            Status = GameResultStatus.Deferred,
            Reason = "dialog"
        });
        True(dispatcher.IsQueued(deferred.Id), "deferred event returned to queue");
        True(await dispatcher.CancelQueuedAsync(deferred.Id) is not null, "deferred retry can be removed");
        Equal(DispatchState.Cancelled, (await processed.FindAsync(deferred.Id))?.State, "deferred removal is terminal");
    }

    private static async Task TestClearQueueAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T01:45:00Z");
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history, () => now) { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };
        var actions = DefaultActionCatalog.Create();
        foreach (var action in actions) action.CooldownSeconds = 0;

        var donations = new[] { Donation(1m), Donation(2m), Donation(3m) };
        foreach (var donation in donations)
        {
            await dispatcher.EnqueueAsync(donation, actions, policy);
        }

        var queuedHistory = history.Entries.Where(entry => entry.State == DispatchState.Queued).ToList();
        var cancelled = await dispatcher.CancelAllQueuedAsync(queuedHistory);
        Equal(3, cancelled.CancelledEntries.Count, "all waiting commands cancelled together");
        Equal(0, dispatcher.Count, "queue is empty after clear");
        True(cancelled.CancelledEntries.All(entry => entry.State == DispatchState.Cancelled &&
            entry.Detail.Contains("Очередь очищена", StringComparison.Ordinal)),
            "bulk cancellation is visible in history");
        foreach (var donation in donations)
        {
            True(!dispatcher.IsQueued(donation.Id), "cleared donation no longer queued");
            Equal(DispatchState.Cancelled, (await processed.FindAsync(donation.Id))?.State,
                "cleared donation is terminal in ledger");
        }

        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.DrainAsync(transport, policy);
        Equal(0, transport.Commands.Count, "cleared commands never reach IPC");
    }

    private static async Task TestClearSuppressesDeferredRetryAsync()
    {
        var processed = new InMemoryProcessedDonationStore();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(processed, history)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var action = DefaultActionCatalog.Create().First().Clone();
        action.CooldownSeconds = 0;
        action.Triggers = [new TriggerRule { Amount = 1m, Currency = "RUB" }];
        var donation = Donation(1m);
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        await dispatcher.EnqueueAsync(donation, [action], new QueuePolicy());
        await dispatcher.DrainAsync(transport, new QueuePolicy());

        var cleared = await dispatcher.CancelAllQueuedAsync(history.Entries);
        Equal(1, cleared.SuppressedInFlightRetries,
            "clear records that an in-flight deferred result must not return");
        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = transport.Commands.Single().CommandId,
            Status = GameResultStatus.Deferred,
            Reason = "game busy"
        });

        Equal(0, dispatcher.Count, "deferred response after clear does not recreate queue item");
        Equal(DispatchState.Cancelled, (await processed.FindCommandAsync(
            donation.Id, transport.Commands.Single().CommandId))?.State,
            "suppressed deferred command becomes terminal cancelled");
    }

    private static async Task TestQueueCancellationPersistenceAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.Tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(directory);
        try
        {
            var donation = Donation(1m);
            var processed = new JsonLineProcessedDonationStore(paths);
            var history = new JsonLineHistoryStore(paths);
            var firstDispatcher = new DonationDispatcher(processed, history);
            await firstDispatcher.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy());
            var queuedEntry = (await history.LoadRecentAsync()).Single(entry =>
                entry.DonationId == donation.Id && entry.State == DispatchState.Queued);

            var reloadedProcessed = new JsonLineProcessedDonationStore(paths);
            var restoredDispatcher = new DonationDispatcher(reloadedProcessed, history);
            True(!restoredDispatcher.IsQueued(donation.Id), "restored dispatcher starts without an executable in-memory command");
            var cancelled = await restoredDispatcher.CancelQueuedAsync(queuedEntry);
            True(cancelled is not null, "persisted queue event can be cancelled after restart");
            Equal(queuedEntry.CommandId, cancelled!.CommandId, "restored cancellation keeps the persisted command id");
            Equal(queuedEntry.ActionId, cancelled.ActionId, "restored cancellation keeps the action instance id");
            Equal(DispatchState.Cancelled, (await reloadedProcessed.FindAsync(donation.Id))?.State, "cancelled ledger reloads");
            var reloadedHistory = await new JsonLineHistoryStore(paths).LoadRecentAsync();
            True(reloadedHistory.Any(entry => entry.DonationId == donation.Id &&
                entry.State == DispatchState.Cancelled &&
                entry.Detail.Contains("Удалено из очереди", StringComparison.Ordinal)), "cancel history reloads");

            var restored = new DonationDispatcher(reloadedProcessed, new InMemoryHistorySink());
            Equal(DispatchState.Duplicate,
                await restored.EnqueueAsync(donation, DefaultActionCatalog.Create(), new QueuePolicy()),
                "cancelled persistent event does not recover after restart");
            Null(await restored.CancelQueuedAsync(queuedEntry), "cancelled ledger rejects a stale queued history row");

            var sentDonation = Donation(1m);
            var liveHistory = new InMemoryHistorySink();
            var liveDispatcher = new DonationDispatcher(reloadedProcessed, liveHistory) { GlobalCooldown = TimeSpan.Zero };
            var actions = DefaultActionCatalog.Create();
            foreach (var action in actions) action.CooldownSeconds = 0;
            await liveDispatcher.EnqueueAsync(sentDonation, actions, new QueuePolicy());
            var sentQueuedEntry = liveHistory.Entries.Single(entry =>
                entry.DonationId == sentDonation.Id && entry.State == DispatchState.Queued);
            var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
            await liveDispatcher.DrainAsync(transport, new QueuePolicy());

            var afterSendRestart = new DonationDispatcher(reloadedProcessed, new InMemoryHistorySink());
            Null(await afterSendRestart.CancelQueuedAsync(sentQueuedEntry), "sent ledger rejects a stale queued history row");
            Equal(DispatchState.Sent, (await reloadedProcessed.FindAsync(sentDonation.Id))?.State, "failed restored cancellation leaves sent state intact");

            await liveDispatcher.CompleteAsync(new GameExecutionResult
            {
                CommandId = transport.Commands.Single(command => command.DonationId == sentDonation.Id).CommandId,
                Status = GameResultStatus.Executed
            });
            var afterCompletionRestart = new DonationDispatcher(reloadedProcessed, new InMemoryHistorySink());
            Null(await afterCompletionRestart.CancelQueuedAsync(sentQueuedEntry), "terminal ledger rejects a stale queued history row");
            Equal(DispatchState.Executed, (await reloadedProcessed.FindAsync(sentDonation.Id))?.State, "failed restored cancellation leaves terminal state intact");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task TestInterruptedSendHistoryRecoveryAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var historyStore = new JsonLineHistoryStore(new AppPaths(directory));
            var directAction = DefaultActionCatalog.Create()
                .Single(action => action.HandlerId == "add_radiation");
            var donation = Donation(1m);
            donation.Id = "recovered-donation";
            donation.Username = "Точный донатер";
            var sentRecord = new ProcessedDonationRecord
            {
                DonationId = donation.Id,
                CommandId = "recovered-command",
                State = DispatchState.Sent
            };
            await historyStore.AppendAsync(new EventHistoryEntry
            {
                DonationId = donation.Id,
                CommandId = "another-command",
                ActionId = directAction.Id,
                HandlerId = directAction.HandlerId,
                ActionName = "Неверный снимок",
                State = DispatchState.Sent
            });
            await historyStore.AppendAsync(new EventHistoryEntry
            {
                DonationId = donation.Id,
                CommandId = sentRecord.CommandId,
                ActionId = directAction.Id,
                HandlerId = directAction.HandlerId,
                ActionName = directAction.DisplayName,
                State = DispatchState.Queued
            });
            await historyStore.AppendAsync(new EventHistoryEntry
            {
                DonationId = "another-donation",
                CommandId = sentRecord.CommandId,
                ActionId = directAction.Id,
                HandlerId = directAction.HandlerId,
                ActionName = "Тоже неверный снимок",
                State = DispatchState.Sent
            });

            var snapshot = await historyStore.FindCommandSnapshotAsync(donation.Id, sentRecord.CommandId);
            True(snapshot is not null, "exact donation and command history snapshot is found");
            Equal(directAction.DisplayName, snapshot!.ActionName, "snapshot lookup does not mix donation or command ids");

            var timestamp = DateTimeOffset.Parse("2026-09-04T12:00:00Z");
            var recovered = HistoryRecovery.CreateInterruptedSendEntry(
                donation, sentRecord, snapshot, directAction, timestamp);
            Equal(directAction.Id, recovered.ActionId, "recovered uncertain history keeps action instance id");
            Equal(directAction.HandlerId, recovered.HandlerId, "recovered uncertain history keeps handler id");
            Equal(directAction.DisplayName, recovered.ActionName, "recovered uncertain history keeps display name");
            Equal(donation.Username, recovered.Donor, "recovered uncertain history keeps current donation identity");
            True(recovered.CanRetryManually, "ordinary exact action snapshot permits an explicit manual retry");

            var randomAll = DefaultActionCatalog.CreatePresets()
                .Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId)
                .CloneAsNewInstance();
            var randomSnapshot = new EventHistoryEntry
            {
                DonationId = donation.Id,
                CommandId = sentRecord.CommandId,
                ActionId = randomAll.Id,
                HandlerId = "change_health",
                ActionName = "Изменить здоровье",
                State = DispatchState.Sent
            };
            var recoveredRandom = HistoryRecovery.CreateInterruptedSendEntry(
                donation, sentRecord, randomSnapshot, randomAll, timestamp);
            Equal(randomAll.Id, recoveredRandom.ActionId, "random-all recovery keeps its source instance id");
            Equal("change_health", recoveredRandom.HandlerId, "random-all recovery identifies the frozen concrete handler");
            True(!recoveredRandom.CanRetryManually, "random-all uncertainty cannot reroll through manual retry");

            var withoutSnapshot = HistoryRecovery.CreateInterruptedSendEntry(
                donation, sentRecord, null, null, timestamp);
            Equal(string.Empty, withoutSnapshot.ActionId, "missing snapshot does not invent an action id");
            True(!withoutSnapshot.CanRetryManually, "missing snapshot fails closed for manual retry");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task TestInvalidActionRejectedAsync()
    {
        var action = DefaultActionCatalog.Create().Single(item => item.HandlerId == "add_radiation");
        action.Parameters["percent"] = "not-a-number";
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history) { GlobalCooldown = TimeSpan.Zero };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        var state = await dispatcher.EnqueueActionAsync(Donation(1m), action, new QueuePolicy());
        Equal(DispatchState.Rejected, state, "invalid action rejected");
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(0, transport.Commands.Count, "invalid parameters never reach IPC");
        True(history.Entries.Any(item => item.State == DispatchState.Rejected && item.CanRetryManually), "invalid action is terminal and visible");
    }

    private static Task TestOrderedPresetRangesAsync()
    {
        var presets = DefaultActionCatalog.CreatePresets();
        foreach (var handlerId in new[] { "spawn_random_hostile_squad", "spawn_random_mutant_pack" })
        {
            var invalid = presets.Single(action => action.HandlerId == handlerId).Clone();
            invalid.Parameters["minimum_count"] = "10";
            invalid.Parameters["maximum_count"] = "1";
            True(invalid.Validate().Any(error =>
                    error.Contains("минимум", StringComparison.OrdinalIgnoreCase) &&
                    error.Contains("максимум", StringComparison.OrdinalIgnoreCase)),
                $"{handlerId} rejects reversed count range");

            invalid.Parameters["minimum_count"] = "5";
            invalid.Parameters["maximum_count"] = "5";
            Equal(0, invalid.Validate().Count, $"{handlerId} accepts an equal one-value range");
        }

        var teleport = presets.Single(action => action.HandlerId == "teleport_random").Clone();
        teleport.Parameters["minimum_meters"] = "100";
        teleport.Parameters["maximum_meters"] = "25";
        True(teleport.Validate().Any(error =>
                error.Contains("минимум", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("максимум", StringComparison.OrdinalIgnoreCase)),
            "teleport rejects reversed distance range before IPC");
        return Task.CompletedTask;
    }

    private static async Task TestRandomPresetAsync()
    {
        var now = DateTimeOffset.Parse("2026-09-04T02:00:00Z");
        var presets = DefaultActionCatalog.CreatePresets();
        var random = presets.Single(action => action.HandlerId == DonationDispatcher.RandomActiveHandlerId).CloneAsNewInstance();
        random.Triggers = [new TriggerRule { Amount = 77m, Currency = "RUB" }];
        random.CooldownSeconds = 30;
        var radiation = presets.Single(action => action.HandlerId == "add_radiation").CloneAsNewInstance();
        radiation.Triggers.Clear();
        radiation.CooldownSeconds = 0;
        var randomAll = presets.Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId).CloneAsNewInstance();
        randomAll.Triggers.Clear();
        var actions = new List<ActionDefinition> { random, randomAll, radiation };
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(),
            history,
            () => now,
            _ => 0)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };

        Equal(DispatchState.Queued, await dispatcher.EnqueueAsync(Donation(77m), actions, new QueuePolicy()), "random event queued");
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        var command = transport.Commands.Single();
        Equal("add_radiation", command.ActionId, "meta-action is never sent to Lua");
        Equal("25", command.Parameters["percent"], "selected action parameters preserved");
        True(history.Entries.Any(entry => entry.ActionName == radiation.DisplayName), "history names the selected action");

        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = command.CommandId,
            Status = GameResultStatus.Executed
        });
        now = now.AddSeconds(5);
        Equal(DispatchState.Queued, await dispatcher.EnqueueAsync(Donation(77m), actions, new QueuePolicy()), "second random event queued");
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(1, transport.Commands.Count, "random preset respects its own pause");
        now = now.AddSeconds(26);
        await dispatcher.DrainAsync(transport, new QueuePolicy());
        Equal(2, transport.Commands.Count, "random preset runs after its own pause");

        var emptyHistory = new InMemoryHistorySink();
        var emptyDispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), emptyHistory, () => now, _ => 0);
        var rejected = await emptyDispatcher.EnqueueAsync(Donation(77m), [random], new QueuePolicy());
        Equal(DispatchState.Rejected, rejected, "random without an eligible target is rejected safely");
        True(emptyHistory.Entries.Any(entry => entry.State == DispatchState.Rejected), "empty random result is visible in history");
    }

    private static async Task TestRandomAllPresetsAsync()
    {
        var presets = DefaultActionCatalog.CreatePresets();
        var randomAll = presets.Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId).CloneAsNewInstance();
        randomAll.Triggers = [new TriggerRule { Amount = 88m, Currency = "RUB" }];
        randomAll.CooldownSeconds = 0;
        randomAll.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = "teleport_random";
        var teleport = presets.Single(action => action.HandlerId == "teleport_random").Clone();
        teleport.CooldownSeconds = 0;
        var disabledHealth = presets.Single(action => action.HandlerId == "change_health").Clone();
        disabledHealth.CooldownSeconds = 0;
        var futureItemChoice = new ActionDefinition
        {
            Id = "future_inventory",
            HandlerId = "future_inventory",
            DisplayName = "future inventory action",
            ParameterDefinitions =
            [
                new ParameterDefinition
                {
                    Key = "item",
                    DisplayName = "item",
                    Kind = ParameterKind.ItemChoice,
                    Options = ["medkit"],
                    DefaultValue = "medkit"
                }
            ],
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["item"] = "medkit" }
        };
        var excludedAndTeleport = new[]
        {
            presets.Single(action => action.HandlerId == "spawn_items"),
            presets.Single(action => action.HandlerId == DonationDispatcher.RandomActiveHandlerId),
            presets.Single(action => action.HandlerId == DonationDispatcher.RandomAllPresetsHandlerId),
            futureItemChoice,
            disabledHealth,
            teleport
        };
        var randomCalls = 0;
        int ReversedRange(int count)
        {
            randomCalls++;
            return randomCalls switch
            {
                1 => count - 1,
                2 => 0,
                _ => throw new InvalidOperationException("random-all parameters were generated more than once")
            };
        }
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(),
            history,
            randomIndex: ReversedRange)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var transport = new FakeTransport { IsConnected = true, IsGameReady = true };
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };

        Equal(
            DispatchState.Queued,
            await dispatcher.EnqueueActionAsync(
                Donation(88m),
                randomAll,
                policy,
                availableActions: [randomAll],
                availablePresets: excludedAndTeleport),
            "random all event queued");
        await dispatcher.DrainAsync(transport, policy);
        var firstCommand = transport.Commands.Single();
        Equal("teleport_random", firstCommand.ActionId, "random all excludes item and meta presets");
        Equal("10", firstCommand.Parameters["minimum_meters"], "random all orders generated minimum");
        Equal("2000", firstCommand.Parameters["maximum_meters"], "random all orders generated maximum");
        Equal(2, randomCalls, "random parameters generated exactly once before first send");
        var queuedHistory = history.Entries.Single(entry => entry.State == DispatchState.Queued);
        Equal(randomAll.Id, queuedHistory.ActionId, "history keeps the source random-all instance");
        Equal("teleport_random", queuedHistory.HandlerId, "history identifies the selected concrete handler");

        await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = firstCommand.CommandId,
            Status = GameResultStatus.Deferred,
            Reason = "loading"
        });
        await dispatcher.DrainAsync(transport, policy);
        Equal(2, transport.Commands.Count, "deferred random-all command returns to queue");
        var retryCommand = transport.Commands[1];
        Equal(firstCommand.ActionId, retryCommand.ActionId, "deferred retry keeps selected handler");
        SequenceEqual(
            firstCommand.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            retryCommand.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            "deferred retry keeps generated parameters");
        Equal(2, randomCalls, "deferred retry does not reroll parameters");
        var uncertain = await dispatcher.CompleteAsync(new GameExecutionResult
        {
            CommandId = retryCommand.CommandId,
            Status = GameResultStatus.Uncertain,
            Reason = "ack_lost"
        });
        True(uncertain is { CanRetryManually: false }, "random-all uncertain result cannot reroll manually");

        var health = presets.Single(action => action.HandlerId == "change_health").Clone();
        health.CooldownSeconds = 0;
        randomAll.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = "change_health";
        var healthDispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(),
            new InMemoryHistorySink(),
            randomIndex: count => count / 2)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        var healthTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
        Equal(
            DispatchState.Queued,
            await healthDispatcher.EnqueueActionAsync(
                Donation(88m),
                randomAll,
                policy,
                availableActions: [randomAll],
                availablePresets: [health]),
            "random all health event queued");
        await healthDispatcher.DrainAsync(healthTransport, policy);
        Equal("1", healthTransport.Commands.Single().Parameters["percent"], "random all avoids a no-op health value");

        async Task<GameCommand> GenerateWithRandomAsync(
            ActionDefinition preset,
            Func<int, int> randomIndex)
        {
            var meta = randomAll.Clone();
            meta.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = preset.HandlerId;
            var boundaryDispatcher = new DonationDispatcher(
                new InMemoryProcessedDonationStore(),
                new InMemoryHistorySink(),
                randomIndex: randomIndex)
            {
                GlobalCooldown = TimeSpan.Zero
            };
            var boundaryTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
            Equal(
                DispatchState.Queued,
                await boundaryDispatcher.EnqueueActionAsync(
                    Donation(88m),
                    meta,
                    policy,
                    availableActions: [meta],
                    availablePresets: [preset]),
                $"random all queues soft-boundary preset {preset.HandlerId}");
            await boundaryDispatcher.DrainAsync(boundaryTransport, policy);
            return boundaryTransport.Commands.Single();
        }

        var softBounds = new[]
        {
            (Handler: "change_money", Key: "amount", Minimum: "-50000", Maximum: "50000"),
            (Handler: "change_health", Key: "percent", Minimum: "-50", Maximum: "50"),
            (Handler: "add_radiation", Key: "percent", Minimum: "-35", Maximum: "35"),
            (Handler: "change_needs", Key: "hunger_percent", Minimum: "-50", Maximum: "50"),
            (Handler: "change_needs", Key: "thirst_percent", Minimum: "-50", Maximum: "50")
        };
        foreach (var group in softBounds.GroupBy(bound => bound.Handler, StringComparer.Ordinal))
        {
            var preset = presets.Single(action => action.HandlerId == group.Key).Clone();
            preset.CooldownSeconds = 0;
            var lower = await GenerateWithRandomAsync(preset, _ => 0);
            var upper = await GenerateWithRandomAsync(preset, count => count - 1);
            var middle = await GenerateWithRandomAsync(preset, count => count / 2);
            foreach (var bound in group)
            {
                Equal(bound.Minimum, lower.Parameters[bound.Key], $"{bound.Handler}.{bound.Key} random soft minimum");
                Equal(bound.Maximum, upper.Parameters[bound.Key], $"{bound.Handler}.{bound.Key} random soft maximum");
                True(middle.Parameters[bound.Key] != "0", $"{bound.Handler}.{bound.Key} random zero is excluded");
            }
        }

        var eligiblePresets = presets.Where(DefaultActionCatalog.IsRandomAllEligiblePreset).ToList();
        Equal(18, eligiblePresets.Count, "random-all eligible preset count");
        foreach (var preset in eligiblePresets)
        {
            randomAll.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = preset.HandlerId;
            var candidate = preset.Clone();
            candidate.CooldownSeconds = 0;
            var candidateDispatcher = new DonationDispatcher(
                new InMemoryProcessedDonationStore(),
                new InMemoryHistorySink(),
                randomIndex: _ => 0)
            {
                GlobalCooldown = TimeSpan.Zero
            };
            var candidateTransport = new FakeTransport { IsConnected = true, IsGameReady = true };
            Equal(
                DispatchState.Queued,
                await candidateDispatcher.EnqueueActionAsync(
                    Donation(88m),
                    randomAll,
                    policy,
                    availableActions: [randomAll],
                    availablePresets: [candidate]),
                $"random all queues {candidate.HandlerId}");
            await candidateDispatcher.DrainAsync(candidateTransport, policy);
            var command = candidateTransport.Commands.Single();
            Equal(candidate.HandlerId, command.ActionId, $"random all sends concrete handler {candidate.HandlerId}");
            if (SpawnGroupBundleCodec.Supports(candidate.HandlerId))
            {
                True(command.Parameters.TryGetValue(SpawnGroupBundleCodec.ParameterKey, out var groups) &&
                    groups.Split(';', StringSplitOptions.RemoveEmptyEntries).Length == 1,
                    $"random all generated one valid {candidate.HandlerId} group");
                continue;
            }
            foreach (var definition in candidate.ParameterDefinitions)
            {
                True(command.Parameters.TryGetValue(definition.Key, out var value),
                    $"random all generated {candidate.HandlerId}.{definition.Key}");
                True(definition.Validate(value) is null,
                    $"random all generated a valid value for {candidate.HandlerId}.{definition.Key}");
            }
        }

        var rejectedHistory = new InMemoryHistorySink();
        var rejectedDispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(),
            rejectedHistory);
        randomAll.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = string.Empty;
        Equal(
            DispatchState.Rejected,
            await rejectedDispatcher.EnqueueActionAsync(
                Donation(88m),
                randomAll,
                policy,
                availableActions: [randomAll],
                availablePresets: excludedAndTeleport[..^1]),
            "random all rejects when only item and meta presets remain");
        True(
            rejectedHistory.Entries.Single(entry => entry.State == DispatchState.Rejected).CanRetryManually == false,
            "unavailable random-all result cannot be manually rerolled");

        var sendFailureHistory = new InMemoryHistorySink();
        var sendFailureDispatcher = new DonationDispatcher(
            new InMemoryProcessedDonationStore(),
            sendFailureHistory,
            randomIndex: _ => 0)
        {
            GlobalCooldown = TimeSpan.Zero
        };
        randomAll.Parameters[DefaultActionCatalog.RandomAllSelectionParameterKey] = "change_health";
        Equal(
            DispatchState.Queued,
            await sendFailureDispatcher.EnqueueActionAsync(
                Donation(88m),
                randomAll,
                policy,
                availableActions: [randomAll],
                availablePresets: [health]),
            "random all send-failure event queued");
        await sendFailureDispatcher.DrainAsync(
            new FakeTransport { IsConnected = true, IsGameReady = true, ThrowOnSend = true },
            policy);
        True(
            sendFailureHistory.Entries.Single(entry => entry.State == DispatchState.Uncertain).CanRetryManually == false,
            "random-all broken-pipe uncertainty cannot be manually rerolled");
    }

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(cancellation.Token);
        return client;
    }

    private static string CreateTestPipeName()
        => $"{PipeProtocol.PipeName}.Tests.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private static string QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == key) return Uri.UnescapeDataString(parts[1]);
        }
        throw new Exception("missing query value " + key);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class PassThroughRateLimiter : IDonationAlertsApiRateLimiter
    {
        public static PassThroughRateLimiter Instance { get; } = new();
        public ValueTask WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingRateLimiter : IDonationAlertsApiRateLimiter
    {
        public int WaitCount { get; private set; }
        public ValueTask WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static DonationEvent Donation(decimal amount, string currency = "RUB") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Username = "tester",
        Amount = amount,
        Currency = currency,
        CreatedAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow
    };

    private sealed class FakeTransport : IGameCommandTransport
    {
        public bool IsConnected { get; init; }
        public bool IsGameReady { get; init; }
        public bool ThrowOnSend { get; init; }
        public List<GameCommand> Commands { get; } = [];
        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend) throw new IOException("simulated broken pipe");
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Null(object? value, string message) { if (value is not null) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, actual {actual}");
    }
    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual)) throw new Exception(message);
    }
    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception(message);
    }
    private static async Task ThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new Exception(message);
    }
}
