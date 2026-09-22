using System.Text.Json;
using System.Xml.Linq;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.App.ViewModels;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.GameIpc;
using ChroniclesDonationBridge.Persistence;

namespace ChroniclesDonationBridge.Tests;

internal static class EditorPersistenceTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ActionDefinition Action(string handler)
    {
        var action = DefaultActionCatalog.CreatePresets().Single(item => item.HandlerId == handler).CloneAsNewInstance();
        action.Triggers.Add(new TriggerRule { Amount = 10m });
        return action;
    }

    public static async Task LiveModelsSurviveSavesAsync()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ChroniclesBridge-Editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var action = Action("spawn_mutants");
            var settings = new AppSettings { Actions = [action] };
            var originalActions = settings.Actions;
            var originalParameters = action.Parameters;
            var originalTrigger = action.Triggers[0];
            var editor = new ActionViewModel(action);
            var store = new JsonSettingsStore(new AppPaths(temporary));
            await store.SaveAsync(settings); // Same path used by donation / ACK handling.
            Assert(ReferenceEquals(settings.Actions, originalActions) && ReferenceEquals(settings.Actions[0], action) &&
                ReferenceEquals(action.Parameters, originalParameters) && ReferenceEquals(action.Triggers[0], originalTrigger),
                "Save replaced the live editor models");
            editor.SpawnGroupRows.Single().CountText = "7";
            editor.CooldownText = "31";
            Assert(editor.TryCommitPendingEdits(out var error), error);
            await store.SaveAsync(settings);
            var reloaded = (await store.LoadAsync()).Actions.Single();
            Assert(reloaded.Parameters["count"] == "7" && reloaded.CooldownSeconds == 31, "Edits after a background save were lost");
            editor.CooldownText = string.Empty;
            editor.SpawnGroupRows.Single().CountText = string.Empty;
            await store.SaveAsync(settings);
            Assert(!editor.TryCommitPendingEdits(out _), "Empty numeric drafts silently reused old values");
            reloaded = (await store.LoadAsync()).Actions.Single();
            Assert(reloaded.Parameters["count"] == "7" && reloaded.CooldownSeconds == 31, "Autosave persisted an incomplete numeric draft");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public static Task SnapshotIsIndependentAsync()
    {
        var settings = new AppSettings { Actions = [Action("spawn_items")] };
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (property.PropertyType == typeof(string)) property.SetValue(settings, "test-" + property.Name);
            if (property.PropertyType == typeof(bool)) property.SetValue(settings, !(bool)property.GetValue(settings)!);
        }
        settings.DonationAlertsUserId = 12345;
        settings.LastDonationCreatedAt = DateTimeOffset.UtcNow;
        settings.GlobalCooldownSeconds = 17;
        settings.QueuePolicy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely, MaximumQueuedEvents = 123, WaitMinutes = 18 };
        var snapshot = SettingsNormalizer.CreatePersistenceSnapshot(settings);
        Assert(JsonSerializer.Serialize(settings) == JsonSerializer.Serialize(snapshot), "Snapshot omitted a settings field");
        snapshot.Actions[0].Parameters["count"] = "8";
        snapshot.Actions[0].Triggers[0].Amount = 42;
        snapshot.ObservedCurrencies.Add("XYZ");
        snapshot.QueuePolicy.WaitMinutes = 12;
        Assert(settings.Actions[0].Parameters["count"] != "8" && settings.Actions[0].Triggers[0].Amount == 10 &&
            !settings.ObservedCurrencies.Contains("XYZ") && settings.QueuePolicy.WaitMinutes == 18, "Snapshot shared mutable collections");
        return Task.CompletedTask;
    }

    public static async Task ItemBundlesAndUserPresetsAsync()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ChroniclesBridge-Presets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var itemPreset = Action("spawn_items");
            itemPreset.IsActive = false;
            itemPreset.ItemSpawns =
            [
                new ItemSpawnEntry { ItemId = "medkit", Count = 2 },
                new ItemSpawnEntry { ItemId = "bread", Count = 3 }
            ];
            itemPreset.DisplayName = "Набор медикаментов";
            itemPreset.Triggers.Clear();
            var settings = new AppSettings { UserPresets = [itemPreset], GlobalCooldownSeconds = 2 };
            var store = new JsonSettingsStore(new AppPaths(temporary));
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            var restored = loaded.UserPresets.Single();
            Assert(!restored.IsActive && restored.Triggers.Count == 0 &&
                restored.DisplayName == "Набор медикаментов" && restored.HasCustomDisplayName,
                "Priceless user preset identity or custom name was lost");
            Assert(loaded.GlobalCooldownSeconds == 2,
                "An existing global effect pause was replaced by the first-run default");
            Assert(restored.ItemSpawns.Count == 2 && restored.ItemSpawns[0].Count == 2 &&
                restored.ItemSpawns[1].ItemId == "bread", "Multi-item rows were not persisted");

            var legacy = Action("spawn_items");
            legacy.ItemSpawns.Clear();
            legacy.Parameters["item"] = "bread";
            legacy.Parameters["count"] = "4";
            var migrated = SettingsNormalizer.Normalize(new AppSettings
            {
                SchemaVersion = 2,
                Actions = [legacy]
            });
            Assert(migrated.Actions.Single().ItemSpawns is [{ ItemId: "bread", Count: 4 }],
                "Legacy single-item settings did not migrate to a one-row bundle");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public static Task BatchedMultiChoiceAsync()
    {
        var action = Action("random_all_presets");
        var editor = new ActionViewModel(action);
        var parameter = editor.Parameters.Single(p => p.IsMultiChoice);
        var expected = parameter.MultiChoiceOptions.Take(2).Select(p => p.Value).ToHashSet();
        foreach (var option in parameter.MultiChoiceOptions) option.IsSelected = expected.Contains(option.Value);
        editor.CooldownText = "25";
        Assert(editor.TryCommitPendingEdits(out var error), error);
        Assert(parameter.Definition.SelectedOptions(action.Parameters[parameter.Definition.Key]).ToHashSet().SetEquals(expected),
            "One save lost some checkbox selections");
        Assert(action.CooldownSeconds == 25, "Multi-field save lost the cooldown");
        return Task.CompletedTask;
    }

    public static async Task SpawnGroupBundlesAsync()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ChroniclesBridge-Groups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mutantAction = Action("spawn_mutants");
            var editor = new ActionViewModel(mutantAction);
            Assert(editor.IsSpawnGroupBundle && editor.Parameters.Count == 0 && editor.SpawnGroupRows.Count == 1,
                "Mutant editor did not switch from legacy fields to group rows");
            editor.AddSpawnGroupCommand.Execute(null);
            editor.SpawnGroupRows[1].VariantId = "chimera";
            editor.SpawnGroupRows[1].Strength = "strong";
            editor.SpawnGroupRows[1].CountText = "2";
            Assert(editor.TryCommitPendingEdits(out var error), error);
            Assert(mutantAction.SpawnGroups.Count == 2 && mutantAction.SpawnGroups[1].VariantId == "chimera" &&
                mutantAction.Parameters["species"] == "dog", "Mutant group rows or legacy first-row mirror were lost");

            var npcAction = Action("spawn_npcs");
            var npcEditor = new ActionViewModel(npcAction);
            npcEditor.AddSpawnGroupCommand.Execute(null);
            npcEditor.SpawnGroupRows[1].VariantId = "duty";
            npcEditor.SpawnGroupRows[1].CountText = "4";
            Assert(npcEditor.TryCommitPendingEdits(out error), error);

            var settings = new AppSettings { Actions = [mutantAction, npcAction] };
            var store = new JsonSettingsStore(new AppPaths(temporary));
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert(loaded.Actions.Single(action => action.HandlerId == "spawn_mutants").SpawnGroups.Count == 2 &&
                loaded.Actions.Single(action => action.HandlerId == "spawn_npcs").SpawnGroups[1].Count == 4,
                "NPC/mutant group rows were not persisted");

            var legacy = Action("spawn_npcs");
            legacy.SpawnGroups.Clear();
            legacy.Parameters["faction"] = "freedom";
            legacy.Parameters["strength"] = "weak";
            legacy.Parameters["count"] = "5";
            var migrated = SettingsNormalizer.Normalize(new AppSettings
            {
                SchemaVersion = 3,
                Actions = [legacy]
            });
            Assert(migrated.Actions.Single().SpawnGroups is
                [{ VariantId: "freedom", Strength: "weak", Count: 5 }],
                "Legacy single NPC settings did not migrate to a one-row group list");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public static Task NumericDraftsAsync()
    {
        var action = Action("add_radiation");
        var editor = new ActionViewModel(action);
        var percent = editor.Parameters.Single();
        var committed = action.Parameters[percent.Definition.Key];
        percent.Value = "-";
        Assert(!editor.TryCommitPendingEdits(out _) && action.Parameters[percent.Definition.Key] == committed,
            "Incomplete number reached the live action");
        percent.Value = "0,25";
        Assert(editor.TryCommitPendingEdits(out var error), error);
        Assert(action.Parameters[percent.Definition.Key] == "0.25", "Decimal comma was interpreted as a thousands separator");
        editor.CooldownText = "1.5";
        Assert(!editor.TryCommitPendingEdits(out _), "Fractional cooldown was accepted");
        editor.CooldownText = string.Empty;
        var rebuilt = new ActionViewModel(action);
        rebuilt.RestorePendingEditsFrom(editor);
        Assert(rebuilt.CooldownText == string.Empty && !rebuilt.TryCommitPendingEdits(out _), "Refresh silently reset an invalid draft");
        return Task.CompletedTask;
    }

    public static Task UiContractsAsync()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.contract.xaml"));
        var attributes = xaml.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Value).ToList();
        Assert(attributes.Count(value => value.Contains("Binding CooldownText,")) == 1 &&
            !attributes.Any(value => value.Contains("Binding CooldownSeconds,")), "The shared popup editor must bind the text draft");
        Assert(attributes.Contains("ConfigureAction_Click") && !xaml.Descendants().Any(element => element.Name.LocalName == "Expander"),
            "Cards must use the shared popup editor rather than inline expanders");
        Assert(attributes.Any(value => value == "{Binding RefreshEventsCommand}") &&
            attributes.Any(value => value == "{Binding RefreshEventsButtonText}"), "History recovery button is missing");
        Assert(attributes.Any(value => value == "{Binding ClearQueueCommand}") &&
            xaml.Descendants().Any(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "ClearQueueButton")),
            "Clear queue button is missing");
        Assert(attributes.Any(value => value.Contains("RenameUserPresetCommand", StringComparison.Ordinal)) &&
            attributes.Any(value => value == "Переименовать пользовательский пресет"),
            "User preset pencil rename action is missing");
        Assert(GameStatusPresentation.Connection(new GameConnectionSnapshot(true, false, "game_suspended", "0.2.0", 123))
            .Contains("очередь ожидает"), "Pause status is not understandable");
        return Task.CompletedTask;
    }

}
