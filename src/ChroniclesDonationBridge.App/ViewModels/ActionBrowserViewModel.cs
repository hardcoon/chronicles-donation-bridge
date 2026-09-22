using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace ChroniclesDonationBridge.App.ViewModels;

public sealed record ActionCategory(string Id, string Label);

public sealed class ActionBrowserViewModel : ObservableObject
{
    public static IReadOnlyList<ActionCategory> Categories { get; } =
    [
        new("all", "Все"),
        new("npc_mutants", "NPC и мутанты"),
        new("player_state", "Состояния игрока"),
        new("inventory", "Инвентарь"),
        new("world", "Мировые события")
    ];

    private ActionCategory _selectedCategory = Categories[0];
    private int _columns = 2;
    private bool _automaticColumns = true;
    public event Action? PreferencesChanged;

    public ActionBrowserViewModel(ObservableCollection<ActionViewModel> actions)
    {
        // Do not use the shared default view: Active and Presets have independent filters.
        VisibleActions = new ListCollectionView(actions) { Filter = MatchesCategory };
    }

    public ICollectionView VisibleActions { get; }
    public ActionCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !Categories.Contains(value) || !SetProperty(ref _selectedCategory, value)) return;
            VisibleActions.Refresh();
            PreferencesChanged?.Invoke();
        }
    }
    public int Columns
    {
        get => _columns;
        set
        {
            if (!SetProperty(ref _columns, Math.Clamp(value, 1, 4))) return;
            RaisePropertyChanged(nameof(EffectiveColumns));
            RaisePropertyChanged(nameof(GridLabel));
            PreferencesChanged?.Invoke();
        }
    }
    public bool AutomaticColumns
    {
        get => _automaticColumns;
        set
        {
            if (!SetProperty(ref _automaticColumns, value)) return;
            RaisePropertyChanged(nameof(EffectiveColumns));
            RaisePropertyChanged(nameof(GridLabel));
            PreferencesChanged?.Invoke();
        }
    }
    public int EffectiveColumns => AutomaticColumns ? 0 : Columns;
    public string GridLabel => AutomaticColumns ? "Сетка: авто" : $"Сетка: {Columns}";
    public void SelectCategory(string id)
    {
        // Compatibility with settings written before the player category was
        // split into state and inventory.
        if (id == "player") id = "player_state";
        if (id == "simulation") id = "all";
        SelectedCategory = Categories.FirstOrDefault(category => category.Id == id) ?? Categories[0];
    }
    private bool MatchesCategory(object item) => item is ActionViewModel action &&
        (SelectedCategory.Id == "all" || EffectiveCategory(action.Model) == SelectedCategory.Id);

    public static string EffectiveCategory(ChroniclesDonationBridge.Core.ActionDefinition action) =>
        Categories.Any(category => category.Id != "all" && category.Id == action.CategoryId)
            ? action.CategoryId
            : CategoryFor(action.HandlerId);

    public static string CategoryFor(string handlerId) => handlerId switch
    {
        "spawn_npcs" or "spawn_mutants" or "spawn_random_hostile_squad" or
        "spawn_random_mutant_pack" => "npc_mutants",
        "spawn_anomaly" or "trigger_emission" or "spawn_vehicle" or
        "explode_nearest_vehicle" or "random_active" or "random_all_presets" => "world",
        "add_radiation" or "change_health" or "god_mode" or "change_needs" or
        "get_drunk" or "sleep_hours" or "teleport_random" => "player_state",
        "drop_active_weapon" or "spawn_items" or "drop_outfit" or "change_money" or
        "damage_equipment" or "drop_random_items" => "inventory",
        _ => "all"
    };
}
