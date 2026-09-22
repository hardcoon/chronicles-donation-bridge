using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.Persistence;

namespace ChroniclesDonationBridge.Tests;

internal static class FilePresetArchitectureTests
{
    private const int ExpectedPresetCount = 23;

    public static async Task LoadAndInstallVisibilityAsync()
    {
        using var fixture = PresetFixture.Create();
        var sourceOnly = fixture.Loader.Load(null);
        Check(sourceOnly.Warnings.Count == 0, "valid file preset headers produced warnings");
        Check(sourceOnly.SourcePaths.Count == ExpectedPresetCount, "not every file preset header was loaded");
        Check(sourceOnly.Presets.Count == 0, "uninstalled file preset became visible");
        Check(sourceOnly.SourcePaths.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ExpectedPresetCount,
            "file preset handler ids are not unique");

        var addonRoot = fixture.CreateInstalledAddon();
        foreach (var source in sourceOnly.SourcePaths.Values)
        {
            File.Copy(source, Path.Combine(addonRoot, "scripts", Path.GetFileName(source)), true);
        }
        var installed = fixture.Loader.Load(addonRoot);
        Check(installed.Warnings.Count == 0 && installed.Presets.Count == ExpectedPresetCount,
            "installed file presets are not all visible");

        var changed = Path.Combine(addonRoot, "scripts", Path.GetFileName(sourceOnly.SourcePaths.Values.First()));
        await File.AppendAllTextAsync(changed, Environment.NewLine + "-- locally changed");
        Check(fixture.Loader.Load(addonRoot).Presets.Count == ExpectedPresetCount,
            "an installed preset disappeared only because its contents differ from the editable source");
    }

    public static Task InvalidFilesAreIsolatedAsync()
    {
        using var fixture = PresetFixture.Create();
        var scripts = Path.Combine(fixture.PackageRoot, "scripts");
        File.WriteAllText(Path.Combine(scripts, "pf_donation_action_bad.script"),
            FilePresetLoader.HeaderStart + "{}" + FilePresetLoader.HeaderEnd);
        File.Copy(
            fixture.Loader.Load(null).SourcePaths.Values.First(),
            Path.Combine(scripts, "pf_donation_action_duplicate.script"));

        var result = fixture.Loader.Load(null);
        Check(result.SourcePaths.Count == ExpectedPresetCount, "one invalid file corrupted other file presets");
        Check(result.Warnings.Count == 2, "malformed and duplicate presets were not reported independently");

        var outside = Path.Combine(fixture.Root, "pf_donation_action_outside.script");
        File.Copy(result.SourcePaths.Values.First(), outside);
        Expect<InvalidDataException>(() => fixture.Loader.ReadPreset(outside),
            "loader accepted a preset outside the packaged add-on scripts directory");
        return Task.CompletedTask;
    }

    public static Task ScriptLoadListsStaySynchronizedAsync()
    {
        var addonInit = ReadScriptModules(Path.Combine(AppContext.BaseDirectory, "addon.contract.init"));
        var modScript = ReadScriptModules(Path.Combine(
            AppContext.BaseDirectory,
            "mod_script_pf_donation_alerts.contract.ltx"));
        Check(addonInit.SetEquals(modScript),
            "addon.init and mod_script_pf_donation_alerts.ltx load different Lua modules");
        Check(addonInit.Contains("pf_donation_action_invulnerability"),
            "the IX-Ray load lists omit the god-mode action module");
        return Task.CompletedTask;
    }

    public static async Task AddonInstallerAsync()
    {
        using var fixture = PresetFixture.Create(copyPresetSources: false);
        var package = Path.Combine(fixture.Root, "package");
        Directory.CreateDirectory(Path.Combine(package, "configs"));
        Directory.CreateDirectory(Path.Combine(package, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(package, "addon.init"), ">script = pf_donation_core");
        await File.WriteAllTextAsync(Path.Combine(package, "configs", "actions.ltx"), "[actions]");
        await File.WriteAllTextAsync(Path.Combine(package, "scripts", "core.script"), "return true");

        var game = Path.Combine(fixture.Root, "game");
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "$game_data$=true");
        var manager = new AddonPackageManager(new AppPaths(Path.Combine(fixture.Root, "state")), package);
        Check((await manager.InspectAsync(game)).State == AddonInstallState.NotInstalled,
            "fresh game was not recognized as missing the add-on");
        Check((await manager.InstallOrUpdateAsync(game)).State == AddonInstallState.Current,
            "fresh add-on install did not complete");

        var legacyGame = Path.Combine(fixture.Root, "legacy-game");
        Directory.CreateDirectory(Path.Combine(legacyGame, "bin"));
        await File.WriteAllTextAsync(Path.Combine(legacyGame, "fsgame.ltx"), "$game_data$=true");
        var legacyTarget = Path.Combine(legacyGame, ActionManifestLoader.AddonRelativeRoot);
        Directory.CreateDirectory(Path.Combine(legacyTarget, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(legacyTarget, "addon.init"), "old official init");
        await File.WriteAllTextAsync(Path.Combine(legacyTarget, "scripts", "unknown.script"), "keep me");
        Check((await manager.InspectAsync(legacyGame)).State == AddonInstallState.UpdateAvailable,
            "partial legacy installation was blocked instead of offered an update");
        await manager.InstallOrUpdateAsync(legacyGame);
        Check(await File.ReadAllTextAsync(Path.Combine(legacyTarget, "scripts", "unknown.script")) == "keep me",
            "legacy update changed an unknown script");

        var targetRoot = Path.Combine(game, ActionManifestLoader.AddonRelativeRoot);
        var obsolete = Path.Combine(targetRoot, "configs", "mod_system_pf_donation_random_squads.ltx");
        await File.WriteAllTextAsync(obsolete, "old simulation squads");
        Check((await manager.InspectAsync(game)).State == AddonInstallState.UpdateAvailable,
            "obsolete simulation-squad config was not detected");
        await manager.InstallOrUpdateAsync(game);
        Check(!File.Exists(obsolete), "obsolete simulation-squad config was not removed");
        var unknown = Path.Combine(targetRoot, "scripts", "user_extension.script");
        await File.WriteAllTextAsync(unknown, "user file");
        await File.WriteAllTextAsync(Path.Combine(package, "scripts", "core.script"), "return 'updated'");
        Check((await manager.InspectAsync(game)).State == AddonInstallState.UpdateAvailable,
            "managed package update was not detected");
        await manager.InstallOrUpdateAsync(game);
        Check(await File.ReadAllTextAsync(unknown) == "user file", "unknown user script was deleted or changed");

        var managedTarget = Path.Combine(targetRoot, "scripts", "core.script");
        await File.WriteAllTextAsync(managedTarget, "user modification");
        await File.WriteAllTextAsync(Path.Combine(package, "scripts", "core.script"), "return 'next update'");
        Check((await manager.InspectAsync(game)).State == AddonInstallState.UpdateAvailable,
            "changed known target was not recognized as updateable");
        await manager.InstallOrUpdateAsync(game);
        Check(await File.ReadAllTextAsync(managedTarget) == "return 'next update'",
            "known target was not updated from the packaged source");
        var backupRoot = new AppPaths(Path.Combine(fixture.Root, "state")).AddonBackupsDirectory;
        Check(Directory.GetFiles(backupRoot, "core.script", SearchOption.AllDirectories)
                  .Any(path => File.ReadAllText(path) == "user modification"),
            "replaced known target was not backed up");
    }

    public static Task MissingSourceKeepsSavedPresetAsync()
    {
        var custom = new ActionDefinition
        {
            Id = "saved-instance",
            HandlerId = "external_file_preset",
            DisplayName = "Моя предустановка",
            HasCustomDisplayName = true,
            Description = "Сохранена до удаления исходника",
            CategoryId = "npc_mutants",
            Parameters = new(StringComparer.OrdinalIgnoreCase),
            ParameterDefinitions = [],
            Triggers = []
        };
        var normalized = SettingsNormalizer.Normalize(new AppSettings
        {
            SchemaVersion = SettingsNormalizer.CurrentSchemaVersion,
            UserPresets = [custom]
        });
        var saved = normalized.UserPresets.Single(preset => preset.HandlerId == custom.HandlerId);
        Check(saved.DisplayName == custom.DisplayName && saved.HasCustomDisplayName,
            "missing source deleted or renamed a saved user preset");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    private static HashSet<string> ReadScriptModules(string path)
    {
        Check(File.Exists(path), "script load-list contract is missing: " + path);
        return File.ReadLines(path)
            .Select(line => line.Split(';', 2)[0].Trim())
            .Where(line => line.StartsWith(">script", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => parts[1].Trim())
            .Where(module => module.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void Expect<TException>(Action action, string error) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(error);
    }

    private sealed class PresetFixture : IDisposable
    {
        private PresetFixture(string root, string packageRoot)
        {
            Root = root;
            PackageRoot = packageRoot;
            Loader = new FilePresetLoader(packageRoot);
        }

        public string Root { get; }
        public string PackageRoot { get; }
        public FilePresetLoader Loader { get; }

        public static PresetFixture Create(bool copyPresetSources = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.Tests", Guid.NewGuid().ToString("N"));
            var package = Path.Combine(root, "addon-package");
            Directory.CreateDirectory(Path.Combine(package, "scripts"));
            if (copyPresetSources)
            {
                var bundled = Path.Combine(AppContext.BaseDirectory, "file-presets");
                foreach (var source in Directory.GetFiles(bundled, "pf_donation_action_*.script"))
                {
                    if (!File.ReadAllText(source).Contains("CDB_PRESET_V1", StringComparison.Ordinal)) continue;
                    File.Copy(source, Path.Combine(package, "scripts", Path.GetFileName(source)));
                }
            }
            return new(root, package);
        }

        public string CreateInstalledAddon()
        {
            var addon = Path.Combine(Root, "installed-addon");
            Directory.CreateDirectory(Path.Combine(addon, "scripts"));
            File.WriteAllText(Path.Combine(addon, "addon.init"), ">script = pf_donation_core");
            return addon;
        }

        public void Dispose()
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ChroniclesDonationBridge.Tests")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Root);
            if (target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                Directory.Delete(target, true);
        }
    }
}
