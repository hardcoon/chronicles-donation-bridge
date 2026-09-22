using System.Globalization;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.App.Services;

/// <summary>
/// Builds the donation item picker by intersecting the inventory registry that
/// feeds the in-game F1 spawner with the addon's explicit donation allowlist.
/// The registry supplies categories; the allowlist is the security boundary.
/// </summary>
public sealed class InventoryCatalogLoader
{
    public const string RandomWeaponSentinel = "__random_weapon__";
    public const string RandomWeaponLabel = "Случайное оружие";
    public const string FuelCanisterSection = "pf_vehicle_fuel_canister";
    private const string RegistrySection = "inventory_sort_registry";
    private const string GroupOrderSection = "inventory_sort_groups";
    private const string ItemAllowlistSection = "pf_donation_item_allowlist";
    private const string ItemLabelsFileName = "pf_donation_item_labels_ru.ltx";
    private const string ItemLabelsSection = "pf_donation_item_labels_ru";
    private static readonly string[] VisibleGroupOrder =
    [
        "weapons",
        "ammo_explosives",
        "armor",
        "weapon_addons",
        "electronics",
        "artefacts",
        "medicine",
        "food",
        "repair_tools",
        "fuel"
    ];

    // Hidden groups remain known to validation so an active rule saved by an
    // older version keeps its already selected, still allowlisted item. They
    // are deliberately omitted from OptionGroups and cannot be selected in a
    // new preset.
    private static readonly string[] KnownGroupOrder =
    [
        .. VisibleGroupOrder.Where(group => group != "fuel"),
        "materials",
        "mutant_parts",
        "containers",
        "misc"
    ];

    private static readonly HashSet<string> SafeGroups = new(KnownGroupOrder, StringComparer.OrdinalIgnoreCase);
    private static readonly StringComparer RussianLabelComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), ignoreCase: true);

    public bool ApplyTo(string gamePath, ParameterDefinition itemParameter, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(itemParameter);
        ArgumentNullException.ThrowIfNull(warnings);

        if (itemParameter.Kind != ParameterKind.ItemChoice)
        {
            warnings.Add("Каталог предметов: параметр item не является ItemChoice.");
            return false;
        }

        var registry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var files = FindRegistryFiles(gamePath, warnings);
        var allowedSections = LoadAllowedSections(files, warnings);
        var itemLabels = LoadItemLabels(files, warnings);
        foreach (var file in files)
        {
            ApplyFile(file, registry, groupPositions, warnings);
        }

        if (registry.Count == 0)
        {
            warnings.Add("Каталог предметов: inventory_sort_registry не найден или пуст; оставлен встроенный безопасный список.");
            return false;
        }
        if (allowedSections.Count == 0)
        {
            warnings.Add("Каталог предметов: безопасный donation allowlist не найден или пуст; оставлен встроенный минимальный список.");
            return false;
        }

        var namesByPosition = groupPositions
            .Where(pair => SafeGroups.Contains(pair.Key))
            .GroupBy(pair => pair.Value)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Key);
        var grouped = VisibleGroupOrder.ToDictionary(
            group => group,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        var acceptedOptions = new List<string>();
        var resolvedGroups = registry.ToDictionary(
            pair => pair.Key,
            pair => ResolveGroup(pair.Value, namesByPosition),
            StringComparer.OrdinalIgnoreCase);
        var weaponSections = resolvedGroups
            .Where(pair => string.Equals(pair.Value, "weapons", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopeSections = resolvedGroups
            .Where(pair => string.Equals(pair.Value, "weapon_addons", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .Where(section => !section.StartsWith("sil_", StringComparison.OrdinalIgnoreCase))
            .Where(section => !section.Contains("grenade_launcher", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(section => section.Length)
            .ThenBy(section => section, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var pair in registry)
        {
            var group = resolvedGroups[pair.Key];
            if (group is null)
            {
                continue;
            }

            // Vehicles have a separate F1 catalog and must never enter the
            // inventory donation picker even if a malformed patch registers one.
            if (pair.Key.StartsWith("veh_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!allowedSections.TryGetValue(pair.Key, out var allowedGroup) ||
                !string.Equals(group, allowedGroup, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(group, "weapons", StringComparison.OrdinalIgnoreCase) &&
                IsScopedWeaponVariant(pair.Key, weaponSections, scopeSections))
            {
                continue;
            }

            acceptedOptions.Add(pair.Key);
            // Presentation-only subgroup. The real registry and Lua allowlist
            // still classify this exact canister as materials. Other hidden
            // materials must not become selectable just to expose gasoline.
            var displayGroup = pair.Key == FuelCanisterSection && group == "materials" ? "fuel" : group;
            if (!grouped.TryGetValue(displayGroup, out var items))
            {
                continue;
            }
            if (string.Equals(group, "ammo_explosives", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.StartsWith("ammo_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(pair.Key);
        }

        foreach (var items in grouped.Values)
        {
            items.Sort((left, right) => CompareItemLabels(left, right, itemLabels));
        }

        // The sentinel is an editor command, not an item section and never an
        // allowlist entry. Offer it only when the current game has at least one
        // concrete weapon in the registry/allowlist intersection. Lua repeats
        // the same intersection and all item safety checks before choosing.
        if (grouped.TryGetValue("weapons", out var weapons) && weapons.Count > 0)
        {
            weapons.Insert(0, RandomWeaponSentinel);
            itemLabels[RandomWeaponSentinel] = RandomWeaponLabel;
        }

        var nonEmptyGroups = grouped
            .Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var visibleOptions = VisibleGroupOrder
            .Where(nonEmptyGroups.ContainsKey)
            .SelectMany(group => nonEmptyGroups[group])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (visibleOptions.Count == 0)
        {
            warnings.Add("Каталог предметов: в inventory_sort_registry нет разрешённых пользовательских групп; оставлен встроенный список.");
            return false;
        }

        acceptedOptions.Sort((left, right) => CompareItemLabels(left, right, itemLabels));
        var options = visibleOptions
            .Concat(acceptedOptions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        itemParameter.OptionGroups = nonEmptyGroups;
        itemParameter.Options = options;
        itemParameter.OptionLabels = options
            .Where(itemLabels.ContainsKey)
            .ToDictionary(section => section, section => itemLabels[section], StringComparer.OrdinalIgnoreCase);
        var missingLabelCount = visibleOptions.Count(section => !itemParameter.OptionLabels.ContainsKey(section));
        if (missingLabelCount > 0)
        {
            warnings.Add($"Каталог предметов: для {missingLabelCount} разрешённых предметов нет русской подписи; показан технический ID.");
        }
        if (!visibleOptions.Contains(itemParameter.DefaultValue, StringComparer.OrdinalIgnoreCase))
        {
            itemParameter.DefaultValue = visibleOptions[0];
        }
        return true;
    }

    private static Dictionary<string, string> LoadItemLabels(
        IEnumerable<string> files,
        ICollection<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = files.Where(path => string.Equals(
                Path.GetFileName(path),
                ItemLabelsFileName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (paths.Count == 0)
        {
            warnings.Add($"Каталог предметов: файл русских подписей {ItemLabelsFileName} не найден.");
            return result;
        }

        foreach (var path in paths)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Каталог предметов: не удалось прочитать русские подписи {path}: {exception.Message}");
                continue;
            }

            var currentSection = string.Empty;
            foreach (var rawLine in lines)
            {
                var line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;
                if (TrySectionName(line, out var sectionName))
                {
                    currentSection = sectionName;
                    continue;
                }
                if (!string.Equals(currentSection, ItemLabelsSection, StringComparison.OrdinalIgnoreCase) ||
                    !TryAssignment(line, out var itemSection, out var label))
                {
                    continue;
                }

                label = label.Trim();
                if (label.Length is > 0 and <= 160 && !label.Any(char.IsControl))
                {
                    result[itemSection] = label;
                }
            }
        }
        return result;
    }

    private static int CompareItemLabels(
        string left,
        string right,
        IReadOnlyDictionary<string, string> labels)
    {
        var leftLabel = labels.TryGetValue(left, out var localizedLeft) ? localizedLeft : left;
        var rightLabel = labels.TryGetValue(right, out var localizedRight) ? localizedRight : right;
        var byLabel = RussianLabelComparer.Compare(leftLabel, rightLabel);
        return byLabel != 0 ? byLabel : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static Dictionary<string, string> LoadAllowedSections(
        IEnumerable<string> files,
        ICollection<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Where(path => string.Equals(
                     Path.GetFileName(path),
                     "mod_system_pf_donation_item_allowlist.ltx",
                     StringComparison.OrdinalIgnoreCase)))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Каталог предметов: не удалось прочитать безопасный allowlist {path}: {exception.Message}");
                continue;
            }

            var currentSection = string.Empty;
            foreach (var rawLine in lines)
            {
                var line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;
                if (TrySectionName(line, out var sectionName))
                {
                    currentSection = sectionName;
                    continue;
                }
                if (!string.Equals(currentSection, ItemAllowlistSection, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (TryAssignment(line, out var itemSection, out var group) &&
                    SafeGroups.Contains(group))
                {
                    result[itemSection] = group.Trim().ToLowerInvariant();
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<string> FindRegistryFiles(string gamePath, ICollection<string> warnings)
    {
        var result = new List<string>();
        var central = Path.Combine(gamePath, "gamedata", "configs", "mod_system_pf_inventory_groups.ltx");
        if (File.Exists(central))
        {
            result.Add(central);
        }
        else
        {
            warnings.Add($"Каталог предметов: не найден центральный реестр {central}");
        }

        AddConfigFiles(Path.Combine(gamePath, "gamedata", "configs"), central, result, warnings);
        var addonsRoot = Path.Combine(gamePath, "ixr_addons");
        if (Directory.Exists(addonsRoot))
        {
            foreach (var addon in Directory.EnumerateDirectories(addonsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                AddConfigFiles(Path.Combine(addon, "configs"), central, result, warnings);
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddConfigFiles(
        string root,
        string central,
        ICollection<string> result,
        ICollection<string> warnings)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.ltx", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(file, central, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Каталог предметов: не удалось прочитать {root}: {exception.Message}");
        }
    }

    private static void ApplyFile(
        string path,
        IDictionary<string, string> registry,
        IDictionary<string, int> groupPositions,
        ICollection<string> warnings)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Каталог предметов: не удалось прочитать {path}: {exception.Message}");
            return;
        }

        var currentSection = string.Empty;
        foreach (var rawLine in lines)
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TrySectionName(line, out var sectionName))
            {
                currentSection = sectionName;
                continue;
            }

            if (string.Equals(currentSection, RegistrySection, StringComparison.OrdinalIgnoreCase))
            {
                if (line[0] == '!' && line.IndexOf('=') < 0)
                {
                    registry.Remove(line[1..].Trim());
                    continue;
                }

                if (TryAssignment(line, out var key, out var value))
                {
                    registry[key] = value;
                }
                continue;
            }

            if (string.Equals(currentSection, GroupOrderSection, StringComparison.OrdinalIgnoreCase) &&
                TryAssignment(line, out var groupName, out var positionText) &&
                int.TryParse(positionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
            {
                groupPositions[groupName] = position;
            }
        }
    }

    private static string StripComment(string line)
    {
        var comment = line.IndexOf(';');
        return comment >= 0 ? line[..comment] : line;
    }

    private static bool TrySectionName(string line, out string name)
    {
        var start = line.StartsWith("![", StringComparison.Ordinal) ? 2 : line.StartsWith("[", StringComparison.Ordinal) ? 1 : -1;
        var end = start >= 0 ? line.IndexOf(']', start) : -1;
        if (start < 0 || end <= start)
        {
            name = string.Empty;
            return false;
        }

        var suffix = line[(end + 1)..].Trim();
        if (suffix.Length > 0 && !suffix.StartsWith(':'))
        {
            name = string.Empty;
            return false;
        }
        name = line[start..end].Trim();
        return name.Length > 0;
    }

    private static bool TryAssignment(string line, out string key, out string value)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = line[..separator].Trim();
        value = line[(separator + 1)..].Trim();
        return key.Length > 0 && value.Length > 0;
    }

    private static string? ResolveGroup(string rawGroup, IReadOnlyDictionary<int, string> namesByPosition)
    {
        var normalized = rawGroup.Trim().ToLowerInvariant();
        if (SafeGroups.Contains(normalized))
        {
            return normalized;
        }

        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) &&
               namesByPosition.TryGetValue(position, out var name)
            ? name
            : null;
    }

    private static bool IsScopedWeaponVariant(
        string section,
        IReadOnlySet<string> weaponSections,
        IEnumerable<string> scopeSections)
    {
        foreach (var scope in scopeSections)
        {
            var suffix = "_" + scope;
            if (section.Length > suffix.Length && section.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                weaponSections.Contains(section[..^suffix.Length]))
            {
                return true;
            }
        }
        return false;
    }
}
