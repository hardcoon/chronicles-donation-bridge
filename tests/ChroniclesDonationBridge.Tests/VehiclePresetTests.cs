using ChroniclesDonationBridge.App;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.Core;
using System.Text.RegularExpressions;

namespace ChroniclesDonationBridge.Tests;

internal static class VehiclePresetTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static Task CatalogAsync()
    {
        var presets = DefaultActionCatalog.CreatePresets();
        var spawn = presets.Single(action => action.HandlerId == "spawn_vehicle");
        var explosion = presets.Single(action => action.HandlerId == "explode_nearest_vehicle");
        foreach (var action in new[] { spawn, explosion })
        {
            Check(!action.IsActive && action.Triggers.Count == 0, "Vehicle preset activated automatically");
            Check(!DefaultActionCatalog.IsRandomAllEligiblePreset(action), "Vehicle preset entered all-random pool");
            Check(action.ScriptRelativePath.EndsWith("pf_donation_action_vehicle.script"), "Wrong Chronicles Lua handler");
            Check(!action.DisplayName.Contains("(тест)", StringComparison.OrdinalIgnoreCase), "Release preset retains test suffix");
        }
        var fuel = spawn.ParameterDefinitions.Single(parameter => parameter.Key == "fuel_level");
        foreach (var invalid in new[] { "0", "11", "-1", "1.5", "", "NaN" })
            Check(fuel.Validate(invalid) is not null, "Invalid fuel accepted: " + invalid);
        Check(fuel.Validate("1") is null && fuel.Validate("10") is null, "Fuel endpoints rejected");
        Check(spawn.Parameters["fuel_level"] == "5", "Fuel default must be five litres");
        Check(fuel.DisplayName == "Бензин, литры (1–10)" && !spawn.Description.Contains('%'), "UI still shows percentages");
        var vehicles = spawn.ParameterDefinitions.Single(parameter => parameter.Key == "vehicle");
        Check(vehicles.Options.Count == 30 && vehicles.Options.Distinct().Count() == 30, "Incomplete/duplicate F1 catalog");
        var legacy = vehicles.Options.Where(s => !s.StartsWith("veh_dcp_")).ToHashSet();
        Check(legacy.SetEquals(["veh_btr", "veh_kamaz", "veh_niva_g", "veh_niva_w", "veh_tr13", "veh_uaz", "veh_zaz"]),
            "Legacy F1 vehicles missing");
        Check(legacy.All(s => FriendlyValueConverter.Display(s).Contains("legacy")), "Legacy variants not identified");
        Check(vehicles.Options.All(s => FriendlyValueConverter.Display(s) != s), "Missing Russian vehicle label");
        Check(vehicles.Validate("heli_mi6") is not null && vehicles.Validate("veh_dcp_paz") is not null, "Uninstalled vehicle accepted");
        var manifest = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "pf_donation_actions.contract.ltx"));
        var declared = manifest.Single(line => line.StartsWith("param.vehicle = ")).Split('|')[3].Split(',');
        Check(vehicles.Options.SequenceEqual(declared), "Compiled choices and game manifest disagree");
        Check(manifest.Single(line => line.StartsWith("param.fuel_level = ")).EndsWith(fuel.DisplayName), "Manifest fuel units differ");
        Check(!manifest.Any(line => line.StartsWith("name = ") && line.Contains("(тест)", StringComparison.OrdinalIgnoreCase)),
            "Game manifest retains test suffix");
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "pf_donation_action_vehicle.contract.lua"));
        var handlers = Regex.Matches(script, @"^\s*(veh_\w+) = \{", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToHashSet();
        Check(handlers.SetEquals(vehicles.Options), "Lua allowlist and editor disagree");
        Check(script.Contains("EXTENDED_RADII = { 12, 15, 20, 25, 30, 40, 50, 65, 80, 100 }", StringComparison.Ordinal) &&
            script.Contains("level.vertex_id(desired)", StringComparison.Ordinal) &&
            script.Contains("level.is_accessible_vertex_id(id)", StringComparison.Ordinal) &&
            script.Contains("vehicle_no_clear_site_within_100m", StringComparison.Ordinal) &&
            !script.Contains("vehicle_no_clear_site_within_10m", StringComparison.Ordinal),
            "Vehicle spawn lacks the indoor-to-outdoor navigation fallback");
        Check(spawn.Description.Contains("100 м", StringComparison.Ordinal),
            "Vehicle preset does not explain its extended nearest-site search");
        var radius = explosion.ParameterDefinitions.Single();
        Check(radius.Validate("0") is not null && radius.Validate("2001") is not null &&
              radius.Validate("45") is null && radius.Validate("2000") is null, "Explosion radius bounds are missing");
        var teleportScript = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "pf_donation_action_teleport_random.contract.lua"));
        Check(teleportScript.Contains("level.vertex_id(desired)", StringComparison.Ordinal) &&
            teleportScript.Contains("level.is_accessible_vertex_id(vertex_id)", StringComparison.Ordinal) &&
            teleportScript.Contains("level.vertex_in_direction(start_vertex, direction, target_distance)", StringComparison.Ordinal) &&
            teleportScript.Contains("math.abs(distance - target_distance)", StringComparison.Ordinal),
            "Teleport does not use the IX-Ray navigation lookup with its compatibility fallback");
        Check(teleportScript.Contains("ATTEMPTS = 360", StringComparison.Ordinal) &&
            teleportScript.Contains("GOLDEN_ANGLE", StringComparison.Ordinal) &&
            !teleportScript.Contains("math.abs(position.y - origin.y) <= 4", StringComparison.Ordinal),
            "Teleport still uses the sparse or four-metre-height search");
        var clone = spawn.CloneAsNewInstance();
        clone.Parameters["fuel_level"] = "10";
        Check(spawn.Parameters["fuel_level"] == "5", "Vehicle instance shares preset parameters");
        Check(!presets.Single(p => p.HandlerId == "random_all_presets").ParameterDefinitions.Single().Options
            .Any(id => id is "spawn_vehicle" or "explode_nearest_vehicle"), "Random selector silently opts into vehicles");
        foreach (var preset in new[] { spawn, explosion })
        {
            var saved = preset.CloneAsNewInstance();
            saved.DisplayName += " (тест)";
            saved.Triggers.Add(new TriggerRule { Amount = 123m, Currency = "RUB" });
            var id = saved.Id;
            var parameters = new Dictionary<string, string>(saved.Parameters);
            var warnings = new ActionManifestLoader().ApplyPresetsToInstances([saved], presets);
            Check(warnings.Count == 0 && saved.DisplayName == preset.DisplayName, "Active instance retains old title");
            Check(saved.Id == id && saved.IsActive && saved.Triggers.Single().Amount == 123m &&
                parameters.All(pair => saved.Parameters[pair.Key] == pair.Value), "Title update changed saved rule");

            var named = preset.CloneAsNewInstance();
            named.DisplayName = "Мой транспорт";
            named.HasCustomDisplayName = true;
            warnings = new ActionManifestLoader().ApplyPresetsToInstances([named], presets);
            Check(warnings.Count == 0 && named.DisplayName == "Мой транспорт" && named.HasCustomDisplayName,
                "Custom active title was replaced by the manifest title");
        }
        return Task.CompletedTask;
    }

    public static Task FuelCategoryAsync()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "ChroniclesBridge-Fuel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Path.Combine(temporary, "gamedata", "configs");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "mod_system_pf_inventory_groups.ltx"),
                "[inventory_sort_registry]\nmedkit = medicine\npf_vehicle_fuel_canister = materials\nmetal_scrap = materials\nveh_bad = technical\n");
            var allowlist = Path.Combine(config, "mod_system_pf_donation_item_allowlist.ltx");
            File.WriteAllText(allowlist, "[pf_donation_item_allowlist]\nmedkit = medicine\npf_vehicle_fuel_canister = materials\nmetal_scrap = materials\n");
            File.WriteAllText(Path.Combine(config, "pf_donation_item_labels_ru.ltx"),
                "[pf_donation_item_labels_ru]\nmedkit = Аптечка\npf_vehicle_fuel_canister = Канистра с бензином (20 л)\n");
            var items = DefaultActionCatalog.CreatePresets().Single(p => p.HandlerId == "spawn_items")
                .ParameterDefinitions.Single(p => p.Key == "item");
            Check(new InventoryCatalogLoader().ApplyTo(temporary, items, new List<string>()), "Cannot load fixture catalog");
            Check(items.OptionGroups["fuel"].SequenceEqual(["pf_vehicle_fuel_canister"]), "Fuel subgroup missing or too broad");
            Check(!items.OptionGroups.ContainsKey("materials") && !items.OptionGroups.Values.Any(v => v.Contains("metal_scrap")),
                "Hidden materials were made visible");
            Check(items.Options.Contains("metal_scrap"), "Existing allowlisted hidden rules no longer validate");
            Check(items.OptionLabels["pf_vehicle_fuel_canister"] == "Канистра с бензином (20 л)", "Canister not localized");
            File.WriteAllText(allowlist, "[pf_donation_item_allowlist]\nmedkit = medicine\n");
            Check(new InventoryCatalogLoader().ApplyTo(temporary, items, new List<string>()), "Cannot load reduced catalog");
            Check(!items.OptionGroups.ContainsKey("fuel") && !items.Options.Contains("pf_vehicle_fuel_canister"),
                "Canister bypassed allowlist");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        return Task.CompletedTask;
    }
}
