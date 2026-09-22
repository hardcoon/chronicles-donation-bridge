using System.Globalization;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.App.Services;

public sealed class ActionManifestLoader
{
    public const string AddonRelativeRoot = "ixr_addons\\zzzzzzzzzzzzzzzz_FOR_STREAMERS_pf-donation-alerts";
    public const string ManifestRelativePath = AddonRelativeRoot + "\\configs\\pf_donation_actions.ltx";
    private static readonly HashSet<string> LegacyHiddenHandlers = new(StringComparer.Ordinal)
    {
        "spawn_dogs_3"
    };
    private readonly InventoryCatalogLoader _inventoryCatalogLoader = new();

    public IReadOnlyList<string> MergeFromGame(
        string gamePath,
        IList<ActionDefinition> catalog,
        bool allowNewActions = true)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return [];
        }

        var manifestPath = Path.Combine(gamePath, ManifestRelativePath);
        if (!File.Exists(manifestPath))
        {
            return [$"Manifest действий не найден: {manifestPath}"];
        }

        var warnings = new List<string>();
        var declaredActionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in ParseSections(File.ReadAllLines(manifestPath)))
        {
            if (!section.Key.StartsWith("action:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = section.Key["action:".Length..].Trim().ToLowerInvariant();
            if (!Validation.ActionId().IsMatch(id))
            {
                warnings.Add($"Manifest: пропущен недопустимый action id {id}");
                continue;
            }
            if (LegacyHiddenHandlers.Contains(id))
            {
                // Kept executable in Lua for old queued commands, but the generic
                // spawn_mutants preset fully replaces it in the editor.
                continue;
            }

            declaredActionIds.Add(id);

            var values = section.Value;
            var existing = catalog.FirstOrDefault(action => string.Equals(action.HandlerId, id, StringComparison.Ordinal));
            if (existing is null && !allowNewActions)
            {
                continue;
            }
            var isNew = existing is null;
            var action = existing ?? new ActionDefinition { Id = id, HandlerId = id };
            action.HandlerId = id;
            action.IsActive = false;
            action.DisplayName = Value(values, "name", action.DisplayName.Length > 0 ? action.DisplayName : id);
            action.Description = Value(values, "description", action.Description);
            var script = Value(values, "script", action.ScriptRelativePath);
            if (!string.IsNullOrWhiteSpace(script))
            {
                action.ScriptRelativePath = script.StartsWith("ixr_addons", StringComparison.OrdinalIgnoreCase)
                    ? script
                    : Path.Combine(AddonRelativeRoot, script);
            }

            if (int.TryParse(Value(values, "cooldown_seconds", action.CooldownSeconds.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown))
            {
                action.CooldownSeconds = Math.Clamp(cooldown, 0, 86_400);
            }

            var parsedDefinitions = ParseParameterDefinitions(values, warnings, id);
            if (parsedDefinitions.Count > 0)
            {
                foreach (var definition in parsedDefinitions)
                {
                    var metadataDefinition = action.ParameterDefinitions.FirstOrDefault(candidate =>
                        string.Equals(candidate.Key, definition.Key, StringComparison.OrdinalIgnoreCase));
                    if (metadataDefinition is null) continue;
                    definition.CatalogKind = metadataDefinition.CatalogKind;
                    definition.MinimumSelections = metadataDefinition.MinimumSelections;
                }
                action.ParameterDefinitions = parsedDefinitions;
            }
            foreach (var pair in ParseKeyValues(Value(values, "default_params", string.Empty)))
            {
                if (!action.Parameters.ContainsKey(pair.Key))
                {
                    action.Parameters[pair.Key] = pair.Value;
                }
            }

            if (isNew)
            {
                action.Triggers = ParseTriggers(values, warnings, id);
                catalog.Add(action);
            }
        }

        // A readable game manifest is authoritative. The compiled catalog is
        // only a first-run fallback and must not expose handlers that are not
        // actually installed in the selected addon version.
        CatalogReconciliation.RetainDeclaredActions(catalog, declaredActionIds);

        var itemParameter = catalog
            .FirstOrDefault(action => string.Equals(action.HandlerId, "spawn_items", StringComparison.Ordinal))?
            .ParameterDefinitions
            .FirstOrDefault(definition => string.Equals(definition.Key, "item", StringComparison.Ordinal));
        if (itemParameter is not null)
        {
            _inventoryCatalogLoader.ApplyTo(gamePath, itemParameter, warnings);
        }

        // Keep this bridge-side safety list synchronized with the localized
        // names and concrete handlers that survived manifest loading.
        DefaultActionCatalog.RefreshRandomAllPresetSelection(catalog);

        return warnings;
    }

    public IReadOnlyList<string> ApplyPresetsToInstances(
        IList<ActionDefinition> instances,
        IReadOnlyList<ActionDefinition> presets,
        bool preserveDisplayNames = false)
    {
        var warnings = new List<string>();
        var presetsByHandler = presets
            .Where(preset => Validation.ActionId().IsMatch(preset.HandlerId))
            .GroupBy(preset => preset.HandlerId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var instance in instances)
        {
            var shouldRemainActive = instance.IsActive;
            if (!presetsByHandler.TryGetValue(instance.HandlerId, out var preset))
            {
                warnings.Add($"Для активного экземпляра {instance.Id} не найден пресет {instance.HandlerId}");
                continue;
            }

            var savedParameters = instance.Parameters is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(instance.Parameters, StringComparer.OrdinalIgnoreCase);
            var displayName = string.Empty;
            var keepDisplayName = (preserveDisplayNames || instance.HasCustomDisplayName) &&
                Validation.TryNormalizeUserPresetName(instance.DisplayName, out displayName);
            instance.DisplayName = keepDisplayName ? displayName : preset.DisplayName;
            instance.HasCustomDisplayName = preserveDisplayNames || instance.HasCustomDisplayName;
            instance.Description = preset.Description;
            instance.CategoryId = preset.CategoryId;
            instance.ScriptRelativePath = preset.ScriptRelativePath;
            instance.ParameterDefinitions = preset.ParameterDefinitions
                .Select(definition => definition.Clone())
                .ToList();
            instance.Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in instance.ParameterDefinitions)
            {
                // Multi-choice values preserve the intersection with the new
                // manifest, including an intentionally empty selection.
                instance.Parameters[definition.Key] =
                    CatalogReconciliation.ResolveSavedParameterValue(definition, savedParameters);
            }
            ReconcileItemBundle(instance, warnings);
            ReconcileSpawnGroupBundle(instance, warnings);
            instance.IsActive = shouldRemainActive;
        }

        return warnings;
    }

    private static void ReconcileItemBundle(ActionDefinition action, ICollection<string> warnings)
    {
        if (!string.Equals(action.HandlerId, ItemBundleCodec.HandlerId, StringComparison.Ordinal))
        {
            return;
        }

        var itemDefinition = action.ParameterDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Key, "item", StringComparison.OrdinalIgnoreCase));
        if (itemDefinition is null)
        {
            warnings.Add($"Для {action.Id} отсутствует каталог предметов.");
            return;
        }

        if (action.ItemSpawns.Count == 0)
        {
            var legacyItem = action.Parameters.TryGetValue("item", out var item)
                ? item
                : itemDefinition.DefaultValue;
            var legacyCount = action.Parameters.TryGetValue("count", out var countText) &&
                int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? Math.Clamp(count, 1, 50)
                : 1;
            action.ItemSpawns.Add(new ItemSpawnEntry { ItemId = legacyItem, Count = legacyCount });
        }

        var accepted = action.ItemSpawns
            .Where(entry => entry.Count is >= 1 and <= 50 && itemDefinition.Validate(entry.ItemId) is null)
            .Select(entry => entry.Clone())
            .ToList();
        if (accepted.Count != action.ItemSpawns.Count)
        {
            warnings.Add($"Из набора {action.Id} удалены недоступные или повреждённые позиции предметов.");
        }
        if (accepted.Count == 0)
        {
            accepted.Add(new ItemSpawnEntry { ItemId = itemDefinition.DefaultValue, Count = 1 });
        }

        action.ItemSpawns = accepted;
        action.Parameters["item"] = accepted[0].ItemId;
        action.Parameters["count"] = accepted[0].Count.ToString(CultureInfo.InvariantCulture);
    }

    private static void ReconcileSpawnGroupBundle(ActionDefinition action, ICollection<string> warnings)
    {
        if (!SpawnGroupBundleCodec.Supports(action.HandlerId))
        {
            return;
        }

        var variantKey = SpawnGroupBundleCodec.VariantParameterKey(action.HandlerId);
        var variantDefinition = action.ParameterDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Key, variantKey, StringComparison.OrdinalIgnoreCase));
        var strengthDefinition = action.ParameterDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Key, "strength", StringComparison.OrdinalIgnoreCase));
        if (variantDefinition is null || strengthDefinition is null)
        {
            warnings.Add($"Для {action.Id} отсутствует схема составного спавна.");
            return;
        }

        if (action.SpawnGroups.Count == 0)
        {
            var legacyVariant = action.Parameters.TryGetValue(variantKey, out var variant)
                ? variant
                : variantDefinition.DefaultValue;
            var legacyStrength = action.Parameters.TryGetValue("strength", out var strength)
                ? strength
                : strengthDefinition.DefaultValue;
            var legacyCount = action.Parameters.TryGetValue("count", out var countText) &&
                int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? Math.Clamp(count, 1, 99)
                : action.HandlerId == "spawn_mutants" ? 3 : 1;
            action.SpawnGroups.Add(new SpawnGroupEntry
            {
                VariantId = legacyVariant,
                Strength = legacyStrength,
                Count = legacyCount
            });
        }

        var accepted = action.SpawnGroups
            .Where(entry => entry.Count is >= 1 and <= 99 &&
                variantDefinition.Validate(entry.VariantId) is null &&
                strengthDefinition.Validate(entry.Strength) is null)
            .Select(entry => entry.Clone())
            .ToList();
        if (accepted.Count != action.SpawnGroups.Count)
        {
            warnings.Add($"Из списка {action.Id} удалены недоступные или повреждённые группы.");
        }
        if (accepted.Count == 0)
        {
            accepted.Add(new SpawnGroupEntry
            {
                VariantId = variantDefinition.DefaultValue,
                Strength = strengthDefinition.DefaultValue,
                Count = action.HandlerId == "spawn_mutants" ? 3 : 1
            });
        }

        action.SpawnGroups = accepted;
        action.Parameters[variantKey] = accepted[0].VariantId;
        action.Parameters["strength"] = accepted[0].Strength;
        action.Parameters["count"] = accepted[0].Count.ToString(CultureInfo.InvariantCulture);
    }

    private static List<ParameterDefinition> ParseParameterDefinitions(Dictionary<string, string> values, List<string> warnings, string actionId)
    {
        var result = new List<ParameterDefinition>();
        foreach (var item in values.Where(pair => pair.Key.StartsWith("param.", StringComparison.OrdinalIgnoreCase)))
        {
            var key = item.Key["param.".Length..].Trim().ToLowerInvariant();
            var parts = item.Value.Split('|');
            if (!Validation.ParameterKey().IsMatch(key) || parts.Length < 6 || !Enum.TryParse<ParameterKind>(parts[0], true, out var kind))
            {
                warnings.Add($"Manifest {actionId}: неверная схема параметра {key}");
                continue;
            }

            decimal? minimum = decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var min) ? min : null;
            decimal? maximum = decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var max) ? max : null;
            result.Add(new ParameterDefinition
            {
                Key = key,
                Kind = kind,
                Minimum = minimum,
                Maximum = maximum,
                Options = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                DefaultValue = parts[4],
                DisplayName = parts[5]
            });
        }
        return result;
    }

    private static List<TriggerRule> ParseTriggers(Dictionary<string, string> values, List<string> warnings, string actionId)
    {
        var result = new List<TriggerRule>();
        foreach (var item in values.Where(pair => pair.Key.StartsWith("trigger.", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = item.Value.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !Enum.TryParse<TriggerComparator>(parts[0], true, out var comparator) ||
                !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                warnings.Add($"Manifest {actionId}: неверное условие {item.Key}");
                continue;
            }
            try
            {
                result.Add(new TriggerRule { Comparator = comparator, Amount = amount, Currency = Money.NormalizeCurrency(parts[2]) });
            }
            catch (ArgumentException)
            {
                warnings.Add($"Manifest {actionId}: неверная валюта в {item.Key}");
            }
        }
        return result;
    }

    private static Dictionary<string, string> ParseKeyValues(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = part[..separator].Trim().ToLowerInvariant();
            if (Validation.ParameterKey().IsMatch(key)) result[key] = part[(separator + 1)..].Trim();
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[name] = current;
                continue;
            }
            if (current is null) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            current[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return sections;
    }

    private static string Value(Dictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) ? value : fallback;
}
