using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.App.Services;

namespace ChroniclesDonationBridge.App.ViewModels;

public sealed record ItemOptionViewModel(string Value, string Label);

public sealed class MultiChoiceOptionViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _isSelected;

    public MultiChoiceOptionViewModel(string value, string label, bool isSelected, Action onChanged)
    {
        Value = value;
        Label = label;
        _isSelected = isSelected;
        _onChanged = onChanged;
    }

    public string Value { get; }
    public string Label { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            _onChanged();
        }
    }

    public void SetFromOwner(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        RaisePropertyChanged(nameof(IsSelected));
    }
}

public sealed class ItemSpawnRowViewModel : ObservableObject
{
    private readonly ParameterDefinition _itemDefinition;
    private readonly Action _onChanged;
    private string _selectedCategory;
    private string _itemId;
    private string _countText;

    public ItemSpawnRowViewModel(
        ParameterDefinition itemDefinition,
        ItemSpawnEntry entry,
        Action onChanged)
    {
        _itemDefinition = itemDefinition;
        _onChanged = onChanged;
        _itemId = entry.ItemId;
        _countText = entry.Count.ToString(CultureInfo.InvariantCulture);
        _selectedCategory = _itemDefinition.OptionGroups
            .FirstOrDefault(pair => pair.Value.Contains(_itemId, StringComparer.OrdinalIgnoreCase)).Key
            ?? _itemDefinition.OptionGroups.Keys.FirstOrDefault()
            ?? string.Empty;
    }

    public IReadOnlyList<string> Categories => _itemDefinition.OptionGroups.Keys.ToList();
    public IReadOnlyList<ItemOptionViewModel> FilteredOptionEntries =>
        (_itemDefinition.OptionGroups.TryGetValue(SelectedCategory, out var options) ? options : [])
        .Select(value => new ItemOptionViewModel(value, DisplayOption(value)))
        .ToList();
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value)) return;
            RaisePropertyChanged(nameof(FilteredOptionEntries));
            var available = _itemDefinition.OptionGroups.TryGetValue(value, out var options) ? options : [];
            if (!available.Contains(ItemId, StringComparer.OrdinalIgnoreCase) && available.FirstOrDefault() is { } first)
            {
                ItemId = first;
            }
        }
    }
    public string ItemId
    {
        get => _itemId;
        set
        {
            if (string.IsNullOrEmpty(value) || !SetProperty(ref _itemId, value)) return;
            var category = _itemDefinition.OptionGroups
                .FirstOrDefault(pair => pair.Value.Contains(value, StringComparer.OrdinalIgnoreCase)).Key;
            if (!string.IsNullOrEmpty(category) && !string.Equals(category, SelectedCategory, StringComparison.OrdinalIgnoreCase))
            {
                _selectedCategory = category;
                RaisePropertyChanged(nameof(SelectedCategory));
                RaisePropertyChanged(nameof(FilteredOptionEntries));
            }
            RaisePropertyChanged(nameof(DisplayItem));
            RaisePropertyChanged(nameof(ValidationMessage));
            _onChanged();
        }
    }
    public string CountText
    {
        get => _countText;
        set
        {
            if (!SetProperty(ref _countText, value)) return;
            RaisePropertyChanged(nameof(ValidationMessage));
            _onChanged();
        }
    }
    public string DisplayItem => DisplayOption(ItemId);
    public string ValidationMessage => TryBuild(out _, out var error) ? string.Empty : error;

    public bool TryBuild(out ItemSpawnEntry entry, out string error)
    {
        entry = new ItemSpawnEntry { ItemId = ItemId };
        error = _itemDefinition.Validate(ItemId) ?? string.Empty;
        if (error.Length > 0) return false;
        if (!int.TryParse(CountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count is < 1 or > 50)
        {
            error = "Количество должно быть целым числом от 1 до 50.";
            return false;
        }
        entry.Count = count;
        return true;
    }

    private string DisplayOption(string value) => _itemDefinition.OptionLabels.TryGetValue(value, out var label)
        ? label
        : FriendlyValueConverter.Display(value);
}

public sealed class SpawnGroupRowViewModel : ObservableObject
{
    private readonly ParameterDefinition _variantDefinition;
    private readonly ParameterDefinition _strengthDefinition;
    private readonly Action _onChanged;
    private string _variantId;
    private string _strength;
    private string _countText;

    public SpawnGroupRowViewModel(
        ParameterDefinition variantDefinition,
        ParameterDefinition strengthDefinition,
        SpawnGroupEntry entry,
        Action onChanged)
    {
        _variantDefinition = variantDefinition;
        _strengthDefinition = strengthDefinition;
        _onChanged = onChanged;
        _variantId = entry.VariantId;
        _strength = entry.Strength;
        _countText = entry.Count.ToString(CultureInfo.InvariantCulture);
    }

    public string VariantDisplayName => _variantDefinition.DisplayName;
    public IReadOnlyList<string> VariantOptions => _variantDefinition.Options;
    public IReadOnlyList<string> StrengthOptions => _strengthDefinition.Options;
    public string VariantId
    {
        get => _variantId;
        set
        {
            if (string.IsNullOrEmpty(value) || !SetProperty(ref _variantId, value)) return;
            RaisePropertyChanged(nameof(DisplayVariant));
            RaisePropertyChanged(nameof(ValidationMessage));
            _onChanged();
        }
    }
    public string Strength
    {
        get => _strength;
        set
        {
            if (string.IsNullOrEmpty(value) || !SetProperty(ref _strength, value)) return;
            RaisePropertyChanged(nameof(DisplayStrength));
            RaisePropertyChanged(nameof(ValidationMessage));
            _onChanged();
        }
    }
    public string CountText
    {
        get => _countText;
        set
        {
            if (!SetProperty(ref _countText, value)) return;
            RaisePropertyChanged(nameof(ValidationMessage));
            _onChanged();
        }
    }
    public string DisplayVariant => DisplayOption(_variantDefinition, VariantId);
    public string DisplayStrength => DisplayOption(_strengthDefinition, Strength);
    public string ValidationMessage => TryBuild(out _, out var error) ? string.Empty : error;

    public bool TryBuild(out SpawnGroupEntry entry, out string error)
    {
        entry = new SpawnGroupEntry { VariantId = VariantId, Strength = Strength };
        error = _variantDefinition.Validate(VariantId) ?? _strengthDefinition.Validate(Strength) ?? string.Empty;
        if (error.Length > 0) return false;
        if (!int.TryParse(CountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count is < 1 or > 99)
        {
            error = "Количество должно быть целым числом от 1 до 99.";
            return false;
        }
        entry.Count = count;
        return true;
    }

    private static string DisplayOption(ParameterDefinition definition, string value) =>
        definition.OptionLabels.TryGetValue(value, out var label)
            ? label
            : FriendlyValueConverter.Display(value);
}

public sealed class ParameterValueViewModel : ObservableObject
{
    private readonly ActionDefinition _action;
    private readonly Action _onChanged;
    private string _selectedCategory = string.Empty;
    private string _value;

    public ParameterValueViewModel(ActionDefinition action, ParameterDefinition definition, Action onChanged)
    {
        _action = action;
        _onChanged = onChanged;
        Definition = definition;
        if (!_action.Parameters.ContainsKey(definition.Key)) _action.Parameters[definition.Key] = definition.DefaultValue;
        _value = _action.Parameters[definition.Key];
        var selectedOptions = definition.SelectedOptions(Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        MultiChoiceOptions = new ObservableCollection<MultiChoiceOptionViewModel>(definition.Options.Select(option =>
            new MultiChoiceOptionViewModel(option, DisplayOption(option), selectedOptions.Contains(option), OnMultiChoiceChanged)));
        if (IsGroupedItemChoice)
        {
            _selectedCategory = Definition.OptionGroups
                .FirstOrDefault(pair => pair.Value.Contains(Value, StringComparer.OrdinalIgnoreCase)).Key
                ?? Definition.OptionGroups.Keys.FirstOrDefault()
                ?? string.Empty;
        }
    }

    public ParameterDefinition Definition { get; }
    public string DisplayName => Definition.DisplayName;
    public bool IsChoice => Definition.Kind is ParameterKind.Choice or ParameterKind.ItemChoice or ParameterKind.MultiChoice;
    public bool IsBoolean => Definition.Kind == ParameterKind.Boolean;
    public bool IsText => Definition.Kind == ParameterKind.Text;
    public bool IsNumeric => Definition.Kind is ParameterKind.Integer or ParameterKind.Decimal or ParameterKind.Percent;
    public bool IsFlatChoice =>
        (Definition.Kind is ParameterKind.Choice or ParameterKind.ItemChoice) && !IsGroupedItemChoice;
    public bool IsGroupedItemChoice => Definition.Kind == ParameterKind.ItemChoice && Definition.OptionGroups.Count > 0;
    public bool IsMultiChoice => Definition.Kind == ParameterKind.MultiChoice;
    public IReadOnlyList<string> Options => Definition.Options;
    public ObservableCollection<MultiChoiceOptionViewModel> MultiChoiceOptions { get; }
    public IReadOnlyList<string> Categories => Definition.OptionGroups.Keys.ToList();
    public IReadOnlyList<string> FilteredOptions =>
        Definition.OptionGroups.TryGetValue(SelectedCategory, out var options) ? options : [];
    public IReadOnlyList<ItemOptionViewModel> FilteredOptionEntries => FilteredOptions
        .Select(value => new ItemOptionViewModel(value, DisplayOption(value)))
        .ToList();
    public string DisplayValue => IsMultiChoice
        ? $"{MultiChoiceOptions.Count(option => option.IsSelected)} из {MultiChoiceOptions.Count}"
        : IsBoolean
            ? BooleanValue ? "Включено" : "Выключено"
        : DisplayOption(Value);
    public bool BooleanValue
    {
        get => Value is "true" or "1";
        set => Value = value ? "true" : "false";
    }
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value)) return;
            RaisePropertyChanged(nameof(FilteredOptions));
            RaisePropertyChanged(nameof(FilteredOptionEntries));
            if (!FilteredOptions.Contains(Value, StringComparer.OrdinalIgnoreCase) && FilteredOptions.FirstOrDefault() is { } first)
            {
                Value = first;
            }
        }
    }
    public string Value
    {
        get => _value;
        set
        {
            // WPF momentarily clears SelectedValue while a grouped ComboBox
            // replaces its ItemsSource. An empty transient selection must not
            // overwrite the raw section ID stored in the action model.
            if (IsChoice && !IsMultiChoice && string.IsNullOrEmpty(value)) return;
            if (value == Value) return;
            _value = value;
            if (IsMultiChoice)
            {
                var selected = Definition.SelectedOptions(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var option in MultiChoiceOptions)
                {
                    option.SetFromOwner(selected.Contains(option.Value));
                }
            }
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(BooleanValue));
            RaisePropertyChanged(nameof(DisplayValue));
            RaisePropertyChanged(nameof(ValidationMessage));
            if (IsGroupedItemChoice)
            {
                var category = Definition.OptionGroups
                    .FirstOrDefault(pair => pair.Value.Contains(value, StringComparer.OrdinalIgnoreCase)).Key;
                if (!string.IsNullOrEmpty(category) && !string.Equals(category, SelectedCategory, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedCategory = category;
                    RaisePropertyChanged(nameof(SelectedCategory));
                    RaisePropertyChanged(nameof(FilteredOptions));
                    RaisePropertyChanged(nameof(FilteredOptionEntries));
                }
            }
            _onChanged();
        }
    }
    public string ValidationMessage => TryGetCommittedValue(out _, out var error) ? string.Empty : error;

    public bool TryGetCommittedValue(out string value, out string error)
    {
        value = Value;
        if (IsBoolean)
        {
            value = BooleanValue ? "true" : "false";
        }
        else if (IsNumeric)
        {
            // A decimal comma is not a thousands separator in an effect editor.
            var normalized = value.Trim().Replace(',', '.');
            if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var number))
            {
                error = $"Параметр «{DisplayName}» должен быть числом.";
                return false;
            }
            value = number.ToString(CultureInfo.InvariantCulture);
        }
        error = Definition.Validate(value) ?? string.Empty;
        return error.Length == 0;
    }

    private void OnMultiChoiceChanged()
    {
        var selected = MultiChoiceOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Value = string.Join(',', Definition.Options.Where(selected.Contains));
    }

    private string DisplayOption(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return Definition.OptionLabels.TryGetValue(value, out var label)
            ? label
            : ChroniclesDonationBridge.App.FriendlyValueConverter.Display(value);
    }
}

public sealed class TriggerRuleViewModel : ObservableObject
{
    public TriggerRuleViewModel(ActionViewModel owner, TriggerRule model)
    {
        Owner = owner;
        Model = model;
    }

    public ActionViewModel Owner { get; }
    public TriggerRule Model { get; }
    public string Label => $"{(Model.Comparator == TriggerComparator.Exact ? "=" : "≥")} {Model.Amount:0.##} {Model.Currency}";
    public void Refresh() => RaisePropertyChanged(nameof(Label));
}

public sealed class ActionViewModel : ObservableObject
{
    private string _lastResult = "Ещё не запускалось";
    private string _cooldownText;
    private readonly RelayCommand _removeItemSpawnCommand;
    private readonly RelayCommand _removeSpawnGroupCommand;
    public ActionViewModel(
        ActionDefinition model,
        bool isPreset = false,
        bool isUserPreset = false,
        string availabilityMessage = "")
    {
        Model = model;
        IsUserPreset = isUserPreset;
        IsPreset = isPreset || isUserPreset;
        AvailabilityMessage = availabilityMessage;
        _cooldownText = model.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
        Parameters = new ObservableCollection<ParameterValueViewModel>(model.ParameterDefinitions
            .Where(definition => !IsBundledParameter(definition.Key))
            .Select(definition =>
            new ParameterValueViewModel(model, definition, OnParameterChanged)));
        ItemSpawnRows = [];
        if (IsItemBundle && ItemDefinition is { } itemDefinition)
        {
            var entries = model.ItemSpawns.Count > 0
                ? model.ItemSpawns
                : [LegacyItemEntry(model)];
            foreach (var entry in entries)
            {
                ItemSpawnRows.Add(new ItemSpawnRowViewModel(itemDefinition, entry, OnItemBundleChanged));
            }
        }
        AddItemSpawnCommand = new RelayCommand(AddItemSpawn, () => IsItemBundle);
        _removeItemSpawnCommand = new RelayCommand(
            parameter => RemoveItemSpawn((ItemSpawnRowViewModel)parameter!),
            _ => IsItemBundle && ItemSpawnRows.Count > 1);
        RemoveItemSpawnCommand = _removeItemSpawnCommand;
        SpawnGroupRows = [];
        if (IsSpawnGroupBundle && VariantDefinition is { } variantDefinition &&
            StrengthDefinition is { } strengthDefinition)
        {
            var entries = model.SpawnGroups.Count > 0
                ? model.SpawnGroups
                : [LegacySpawnGroupEntry(model)];
            foreach (var entry in entries)
            {
                SpawnGroupRows.Add(new SpawnGroupRowViewModel(
                    variantDefinition, strengthDefinition, entry, OnSpawnGroupBundleChanged));
            }
        }
        AddSpawnGroupCommand = new RelayCommand(AddSpawnGroup, () => IsSpawnGroupBundle);
        _removeSpawnGroupCommand = new RelayCommand(
            parameter => RemoveSpawnGroup((SpawnGroupRowViewModel)parameter!),
            _ => IsSpawnGroupBundle && SpawnGroupRows.Count > 1);
        RemoveSpawnGroupCommand = _removeSpawnGroupCommand;
        Triggers = new ObservableCollection<TriggerRuleViewModel>(model.Triggers.Select(rule => new TriggerRuleViewModel(this, rule)));
    }

    public ActionDefinition Model { get; }
    public string Id => Model.Id;
    public string HandlerId => Model.HandlerId;
    public bool IsPreset { get; }
    public bool IsUserPreset { get; }
    public bool IsBuiltInPreset => IsPreset && !IsUserPreset;
    public string AvailabilityMessage { get; }
    public bool IsAvailable => string.IsNullOrWhiteSpace(AvailabilityMessage);
    public bool IsItemBundle => string.Equals(HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal);
    public bool IsSpawnGroupBundle => SpawnGroupBundleCodec.Supports(HandlerId);
    public bool HasRowBundle => IsItemBundle || IsSpawnGroupBundle;
    public string DisplayName => Model.DisplayName;
    public string Description => Model.Description;
    public int DisplayOrdinal { get; set; }
    public string DisplayOrdinalLabel => $"№{DisplayOrdinal}";
    public IReadOnlyList<string> ParameterSummaryItems => IsItemBundle
        ? ItemSpawnRows.Select(row => $"{row.DisplayItem} × {row.CountText}").ToList()
        : IsSpawnGroupBundle
            ? SpawnGroupRows.Select(row => $"{row.DisplayVariant} · {row.DisplayStrength} × {row.CountText}").ToList()
            : Parameters.Select(parameter => $"{parameter.DisplayName}: {parameter.DisplayValue}").ToList();
    // Append the add button as an item so it wraps together with every price chip.
    public IEnumerable<object> PriceItems => Triggers.Cast<object>().Append(this);
    public ActionViewModel CreateEditorDraft()
    {
        var draft = new ActionViewModel(Model.Clone(), IsPreset, IsUserPreset, AvailabilityMessage) { DisplayOrdinal = DisplayOrdinal, LastResult = LastResult };
        draft.RestorePendingEditsFrom(this);
        return draft;
    }
    public void ApplyEditorDraft(ActionViewModel draft)
    {
        if (Id != draft.Id || HandlerId != draft.HandlerId) throw new InvalidOperationException("Действие изменилось. Откройте редактор заново.");
        foreach (var parameter in Parameters)
        {
            parameter.Value = draft.Model.Parameters[parameter.Definition.Key];
            Model.Parameters[parameter.Definition.Key] = parameter.Value;
        }
        ReplaceItemRowsFrom(draft.Model.ItemSpawns);
        ReplaceSpawnGroupRowsFrom(draft.Model.SpawnGroups);
        CooldownSeconds = draft.Model.CooldownSeconds;
        // Price rules are edited separately; never overwrite them from a stale editor draft.
    }
    public void ReplaceTriggers(IEnumerable<TriggerRule> rules)
    {
        var copies = rules.Select(rule => rule.Clone()).ToList();
        Model.Triggers.Clear();
        Triggers.Clear();
        foreach (var rule in copies) AddTrigger(rule);
        RaisePropertyChanged(nameof(PriceItems));
    }
    public string ParameterSummary => string.Join(" · ", ParameterSummaryItems);
    public int CompactParameterColumns =>
        Parameters.Any(parameter => parameter.IsMultiChoice)
            ? 1
            : Parameters.Count == 3 && Parameters.All(parameter => !parameter.IsGroupedItemChoice) ? 3 : 2;
    public int EditorParameterColumns => Parameters.Any(parameter => parameter.IsMultiChoice || parameter.IsGroupedItemChoice) ? 1 : 0;
    public string ShortInstanceId => IsPreset || Id.Length <= 8 ? string.Empty : Id[..8];
    public string CooldownLabel => "Пауза после срабатывания, сек.";
    public string CooldownHint => "Это действие нельзя повторить до окончания паузы.";
    public int CooldownSeconds
    {
        get => Model.CooldownSeconds;
        set
        {
            if (Model.CooldownSeconds == value) return;
            Model.CooldownSeconds = value;
            _cooldownText = value.ToString(CultureInfo.InvariantCulture);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CooldownText));
            RaisePropertyChanged(nameof(CooldownValidationMessage));
        }
    }
    public string CooldownText
    {
        get => _cooldownText;
        set
        {
            if (!SetProperty(ref _cooldownText, value)) return;
            RaisePropertyChanged(nameof(CooldownValidationMessage));
        }
    }
    public string CooldownValidationMessage =>
        int.TryParse(CooldownText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
        seconds is >= 0 and <= 86_400 ? string.Empty : "Пауза должна быть целым числом от 0 до 86 400 секунд.";

    public bool TryValidatePendingEdits(out string error) => TryBuildEditedAction(out _, out error);

    private bool TryBuildEditedAction(out ActionDefinition edited, out string error)
    {
        edited = Model.Clone();
        error = CooldownValidationMessage;
        if (error.Length > 0) return false;
        edited.CooldownSeconds = int.Parse(CooldownText, CultureInfo.InvariantCulture);
        foreach (var parameter in Parameters)
        {
            if (!parameter.TryGetCommittedValue(out var value, out error)) return false;
            edited.Parameters[parameter.Definition.Key] = value;
        }
        if (IsItemBundle)
        {
            edited.ItemSpawns.Clear();
            foreach (var row in ItemSpawnRows)
            {
                if (!row.TryBuild(out var entry, out error)) return false;
                edited.ItemSpawns.Add(entry);
            }
            SyncLegacyItemParameters(edited);
        }
        if (IsSpawnGroupBundle)
        {
            edited.SpawnGroups.Clear();
            foreach (var row in SpawnGroupRows)
            {
                if (!row.TryBuild(out var entry, out error)) return false;
                edited.SpawnGroups.Add(entry);
            }
            SyncLegacySpawnGroupParameters(edited);
        }
        error = string.Join(Environment.NewLine, edited.Validate());
        return error.Length == 0;
    }

    public bool TryCommitPendingEdits(out string error)
    {
        if (!TryBuildEditedAction(out var edited, out error)) return false;
        foreach (var pair in edited.Parameters) Model.Parameters[pair.Key] = pair.Value;
        Model.ItemSpawns = edited.ItemSpawns.Select(entry => entry.Clone()).ToList();
        SyncLegacyItemParameters(Model);
        Model.SpawnGroups = edited.SpawnGroups.Select(entry => entry.Clone()).ToList();
        SyncLegacySpawnGroupParameters(Model);
        Model.CooldownSeconds = edited.CooldownSeconds;
        RaisePropertyChanged(nameof(CooldownSeconds));
        RaisePropertyChanged(nameof(ParameterSummary));
        RaisePropertyChanged(nameof(ParameterSummaryItems));
        return true;
    }

    public void RestorePendingEditsFrom(ActionViewModel existing)
    {
        CooldownText = existing.CooldownText;
        foreach (var parameter in Parameters)
        {
            var previous = existing.Parameters.FirstOrDefault(p => p.Definition.Key == parameter.Definition.Key);
            if (previous is not null && (!parameter.IsChoice || parameter.Definition.Validate(previous.Value) is null))
                parameter.Value = previous.Value;
        }
        if (IsItemBundle) ReplaceItemRowsFrom(existing.ItemSpawnRows.Select(row =>
            row.TryBuild(out var entry, out _) ? entry : LegacyItemEntry(existing.Model)));
        if (IsSpawnGroupBundle) ReplaceSpawnGroupRowsFrom(existing.SpawnGroupRows.Select(row =>
            row.TryBuild(out var entry, out _) ? entry : LegacySpawnGroupEntry(existing.Model)));
    }
    public ObservableCollection<ParameterValueViewModel> Parameters { get; }
    public ObservableCollection<ItemSpawnRowViewModel> ItemSpawnRows { get; }
    public ObservableCollection<SpawnGroupRowViewModel> SpawnGroupRows { get; }
    public ObservableCollection<TriggerRuleViewModel> Triggers { get; }
    public ICommand AddItemSpawnCommand { get; }
    public ICommand RemoveItemSpawnCommand { get; }
    public ICommand AddSpawnGroupCommand { get; }
    public ICommand RemoveSpawnGroupCommand { get; }
    public string LastResult { get => _lastResult; set => SetProperty(ref _lastResult, value); }

    public void AddTrigger(TriggerRule rule)
    {
        Model.Triggers.Add(rule);
        Triggers.Add(new TriggerRuleViewModel(this, rule));
        RaisePropertyChanged(nameof(Triggers));
        RaisePropertyChanged(nameof(PriceItems));
    }

    public void RemoveTrigger(TriggerRuleViewModel trigger)
    {
        Model.Triggers.Remove(trigger.Model);
        Triggers.Remove(trigger);
        RaisePropertyChanged(nameof(PriceItems));
    }

    private void OnParameterChanged()
    {
        RaisePropertyChanged(nameof(ParameterSummary));
        RaisePropertyChanged(nameof(ParameterSummaryItems));
    }

    private ParameterDefinition? ItemDefinition => Model.ParameterDefinitions.FirstOrDefault(definition =>
        string.Equals(definition.Key, "item", StringComparison.OrdinalIgnoreCase));

    private ParameterDefinition? VariantDefinition => IsSpawnGroupBundle
        ? Model.ParameterDefinitions.FirstOrDefault(definition => string.Equals(
            definition.Key,
            SpawnGroupBundleCodec.VariantParameterKey(HandlerId),
            StringComparison.OrdinalIgnoreCase))
        : null;

    private ParameterDefinition? StrengthDefinition => Model.ParameterDefinitions.FirstOrDefault(definition =>
        string.Equals(definition.Key, "strength", StringComparison.OrdinalIgnoreCase));

    private bool IsBundledParameter(string key)
    {
        if (IsItemBundle)
        {
            return key.Equals("item", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("count", StringComparison.OrdinalIgnoreCase);
        }
        if (!IsSpawnGroupBundle) return false;
        return key.Equals(SpawnGroupBundleCodec.VariantParameterKey(HandlerId), StringComparison.OrdinalIgnoreCase) ||
            key.Equals("strength", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("count", StringComparison.OrdinalIgnoreCase);
    }

    private static ItemSpawnEntry LegacyItemEntry(ActionDefinition model)
    {
        var item = model.Parameters.TryGetValue("item", out var itemValue) ? itemValue : "medkit";
        var count = model.Parameters.TryGetValue("count", out var countValue) &&
            int.TryParse(countValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 50)
            : 1;
        return new ItemSpawnEntry { ItemId = item, Count = count };
    }

    private static SpawnGroupEntry LegacySpawnGroupEntry(ActionDefinition model)
    {
        var variantKey = SpawnGroupBundleCodec.VariantParameterKey(model.HandlerId);
        var variantDefinition = model.ParameterDefinitions.First(definition =>
            string.Equals(definition.Key, variantKey, StringComparison.OrdinalIgnoreCase));
        var strengthDefinition = model.ParameterDefinitions.First(definition =>
            string.Equals(definition.Key, "strength", StringComparison.OrdinalIgnoreCase));
        var variant = model.Parameters.TryGetValue(variantKey, out var variantValue)
            ? variantValue
            : variantDefinition.DefaultValue;
        var strength = model.Parameters.TryGetValue("strength", out var strengthValue)
            ? strengthValue
            : strengthDefinition.DefaultValue;
        var count = model.Parameters.TryGetValue("count", out var countValue) &&
            int.TryParse(countValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 99)
            : model.HandlerId == "spawn_mutants" ? 3 : 1;
        return new SpawnGroupEntry { VariantId = variant, Strength = strength, Count = count };
    }

    private void AddItemSpawn()
    {
        if (ItemDefinition is not { } definition) return;
        var first = definition.OptionGroups.Values.SelectMany(options => options).FirstOrDefault()
            ?? definition.DefaultValue;
        ItemSpawnRows.Add(new ItemSpawnRowViewModel(
            definition,
            new ItemSpawnEntry { ItemId = first, Count = 1 },
            OnItemBundleChanged));
        _removeItemSpawnCommand.RaiseCanExecuteChanged();
        OnItemBundleChanged();
    }

    private void RemoveItemSpawn(ItemSpawnRowViewModel row)
    {
        if (ItemSpawnRows.Count <= 1 || !ItemSpawnRows.Remove(row)) return;
        _removeItemSpawnCommand.RaiseCanExecuteChanged();
        OnItemBundleChanged();
    }

    private void AddSpawnGroup()
    {
        if (VariantDefinition is not { } variantDefinition || StrengthDefinition is not { } strengthDefinition) return;
        SpawnGroupRows.Add(new SpawnGroupRowViewModel(
            variantDefinition,
            strengthDefinition,
            new SpawnGroupEntry
            {
                VariantId = variantDefinition.DefaultValue,
                Strength = strengthDefinition.DefaultValue,
                Count = 1
            },
            OnSpawnGroupBundleChanged));
        _removeSpawnGroupCommand.RaiseCanExecuteChanged();
        OnSpawnGroupBundleChanged();
    }

    private void RemoveSpawnGroup(SpawnGroupRowViewModel row)
    {
        if (SpawnGroupRows.Count <= 1 || !SpawnGroupRows.Remove(row)) return;
        _removeSpawnGroupCommand.RaiseCanExecuteChanged();
        OnSpawnGroupBundleChanged();
    }

    private void ReplaceItemRowsFrom(IEnumerable<ItemSpawnEntry> entries)
    {
        if (!IsItemBundle || ItemDefinition is not { } definition) return;
        var replacements = entries.ToList();
        if (replacements.Count == 0) replacements.Add(LegacyItemEntry(Model));
        ItemSpawnRows.Clear();
        foreach (var entry in replacements)
        {
            ItemSpawnRows.Add(new ItemSpawnRowViewModel(definition, entry, OnItemBundleChanged));
        }
        Model.ItemSpawns = replacements.Select(entry => entry.Clone()).ToList();
        SyncLegacyItemParameters(Model);
        _removeItemSpawnCommand.RaiseCanExecuteChanged();
        OnItemBundleChanged();
    }

    private void ReplaceSpawnGroupRowsFrom(IEnumerable<SpawnGroupEntry> entries)
    {
        if (!IsSpawnGroupBundle || VariantDefinition is not { } variantDefinition ||
            StrengthDefinition is not { } strengthDefinition) return;
        var replacements = entries.ToList();
        if (replacements.Count == 0) replacements.Add(LegacySpawnGroupEntry(Model));
        SpawnGroupRows.Clear();
        foreach (var entry in replacements)
        {
            SpawnGroupRows.Add(new SpawnGroupRowViewModel(
                variantDefinition, strengthDefinition, entry, OnSpawnGroupBundleChanged));
        }
        Model.SpawnGroups = replacements.Select(entry => entry.Clone()).ToList();
        SyncLegacySpawnGroupParameters(Model);
        _removeSpawnGroupCommand.RaiseCanExecuteChanged();
        OnSpawnGroupBundleChanged();
    }

    private void OnItemBundleChanged()
    {
        RaisePropertyChanged(nameof(ParameterSummary));
        RaisePropertyChanged(nameof(ParameterSummaryItems));
    }

    private void OnSpawnGroupBundleChanged()
    {
        RaisePropertyChanged(nameof(ParameterSummary));
        RaisePropertyChanged(nameof(ParameterSummaryItems));
    }

    private static void SyncLegacyItemParameters(ActionDefinition action)
    {
        if (action.ItemSpawns.FirstOrDefault() is not { } first) return;
        action.Parameters["item"] = first.ItemId;
        action.Parameters["count"] = first.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static void SyncLegacySpawnGroupParameters(ActionDefinition action)
    {
        if (!SpawnGroupBundleCodec.Supports(action.HandlerId) ||
            action.SpawnGroups.FirstOrDefault() is not { } first) return;
        action.Parameters[SpawnGroupBundleCodec.VariantParameterKey(action.HandlerId)] = first.VariantId;
        action.Parameters["strength"] = first.Strength;
        action.Parameters["count"] = first.Count.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed record TriggerEditorResult(IReadOnlyList<TriggerRule> Rules, bool ReplaceAllPrices)
{
    public TriggerRule SingleRule => Rules.Count == 1
        ? Rules[0]
        : throw new InvalidOperationException("Результат содержит несколько ценовых условий.");
}

public sealed record CalculatedPriceViewModel(decimal Amount, string Currency)
{
    public string Label => $"{Amount:0.##} {Currency}";
}

public sealed class TriggerEditorViewModel : ObservableObject
{
    private TriggerComparator _comparator;
    private string _amountText;
    private string _currency;
    private string _error = string.Empty;
    private string _conversionStatus = string.Empty;
    private bool _isCalculating;
    private readonly IExchangeRateProvider _exchangeRateProvider;

    public TriggerEditorViewModel(
        TriggerRule? existing,
        IEnumerable<string> currencies,
        IExchangeRateProvider? exchangeRateProvider = null)
    {
        Existing = existing;
        _comparator = existing?.Comparator ?? TriggerComparator.Exact;
        _amountText = (existing?.Amount ?? 1m).ToString("0.##", CultureInfo.CurrentCulture);
        _currency = existing?.Currency ?? "RUB";
        _exchangeRateProvider = exchangeRateProvider ?? new CbrExchangeRateProvider();
        Currencies = new ObservableCollection<string>(currencies
            .Concat(DonationCurrencyPriceCalculator.SupportedCurrencies)
            .Select(value => Money.TryNormalizeCurrency(value, out var normalized) ? normalized : null)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));
    }

    public TriggerRule? Existing { get; }
    public IReadOnlyList<TriggerComparator> Comparators { get; } = Enum.GetValues<TriggerComparator>();
    public ObservableCollection<string> Currencies { get; }
    public ObservableCollection<CalculatedPriceViewModel> CalculatedPrices { get; } = [];
    public TriggerComparator Comparator
    {
        get => _comparator;
        set
        {
            if (SetProperty(ref _comparator, value)) ClearCalculatedPrices();
        }
    }
    public string AmountText
    {
        get => _amountText;
        set
        {
            if (SetProperty(ref _amountText, value)) ClearCalculatedPrices();
        }
    }
    public string Currency
    {
        get => _currency;
        set
        {
            if (SetProperty(ref _currency, value)) ClearCalculatedPrices();
        }
    }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string ConversionStatus
    {
        get => _conversionStatus;
        private set => SetProperty(ref _conversionStatus, value);
    }
    public bool IsCalculating
    {
        get => _isCalculating;
        private set
        {
            if (!SetProperty(ref _isCalculating, value)) return;
            RaisePropertyChanged(nameof(CanCalculate));
            RaisePropertyChanged(nameof(CalculateButtonText));
        }
    }
    public bool CanCalculate => !IsCalculating;
    public bool HasCalculatedPrices => CalculatedPrices.Count > 0;
    public string CalculateButtonText => IsCalculating
        ? "Получаем актуальный курс…"
        : "Рассчитать стоимость на все валюты";
    public string SaveButtonText => HasCalculatedPrices
        ? $"Заменить цены ({CalculatedPrices.Count})"
        : "Сохранить условие";

    public async Task CalculateAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsCalculating) return;
        var seed = BuildSeed();
        if (seed is null) return;
        var originalAmount = AmountText;
        var originalCurrency = Currency;
        var originalComparator = Comparator;
        ClearCalculatedPrices();
        IsCalculating = true;
        try
        {
            var snapshot = await _exchangeRateProvider.GetLatestAsync(cancellationToken);
            if (!string.Equals(originalAmount, AmountText, StringComparison.Ordinal) ||
                !string.Equals(originalCurrency, Currency, StringComparison.Ordinal) ||
                originalComparator != Comparator)
            {
                Error = "Сумма, валюта или тип условия изменились во время загрузки курса. Нажмите расчёт ещё раз.";
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var rules = DonationCurrencyPriceCalculator.CalculateAll(seed, snapshot);
            foreach (var rule in rules)
            {
                CalculatedPrices.Add(new CalculatedPriceViewModel(rule.Amount, rule.Currency));
            }
            ConversionStatus = $"{snapshot.SourceName}, курс от {snapshot.EffectiveDate:dd.MM.yyyy}. " +
                               $"При сохранении прежние цены варианта будут заменены на {rules.Count} новых.";
            Error = string.Empty;
            RaiseCalculationProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Error = "Получение курса отменено.";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException or OperationCanceledException or System.Xml.XmlException or OverflowException)
        {
            Error = "Не удалось рассчитать цены: " + exception.Message;
        }
        finally
        {
            IsCalculating = false;
        }
    }

    public TriggerEditorResult? Build()
    {
        if (IsCalculating) { Error = "Дождитесь окончания расчёта или нажмите «Отмена»."; return null; }
        var seed = BuildSeed();
        if (seed is null) return null;
        if (!HasCalculatedPrices)
        {
            return new TriggerEditorResult([seed], ReplaceAllPrices: false);
        }

        var snapshot = CalculatedPrices
            .Select(price => new TriggerRule
            {
                Enabled = seed.Enabled,
                Comparator = seed.Comparator,
                Amount = price.Amount,
                Currency = price.Currency
            })
            .ToList();
        Error = string.Empty;
        return new TriggerEditorResult(snapshot, ReplaceAllPrices: true);
    }

    private TriggerRule? BuildSeed()
    {
        if (!decimal.TryParse(AmountText.Trim().Replace(',', '.'),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
        {
            Error = "Введите корректную сумму.";
            return null;
        }
        string currency;
        try { currency = Money.NormalizeCurrency(Currency); }
        catch (ArgumentException exception) { Error = exception.Message; return null; }
        var result = Existing?.Clone() ?? new TriggerRule();
        result.Comparator = Comparator;
        result.Amount = amount;
        result.Currency = currency;
        var errors = Validation.ValidateTrigger(result);
        if (errors.Count > 0) { Error = string.Join(Environment.NewLine, errors); return null; }
        Error = string.Empty;
        return result;
    }

    private void ClearCalculatedPrices()
    {
        if (CalculatedPrices.Count == 0 && ConversionStatus.Length == 0) return;
        CalculatedPrices.Clear();
        ConversionStatus = string.Empty;
        RaiseCalculationProperties();
    }

    private void RaiseCalculationProperties()
    {
        RaisePropertyChanged(nameof(HasCalculatedPrices));
        RaisePropertyChanged(nameof(SaveButtonText));
    }
}
