using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.App.Services;

public sealed record FilePresetLoadResult(
    IReadOnlyList<ActionDefinition> Presets,
    IReadOnlyDictionary<string, string> SourcePaths,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads only the bounded JSON comment at the start of each Chronicles Lua
/// file. Lua source is treated as opaque data and is never executed by the
/// desktop process. A preset becomes visible when an ordinary file with the
/// same name is present in the selected game.
/// </summary>
public sealed class FilePresetLoader
{
    public const string HeaderStart = "--[[CDB_PRESET_V1\n";
    public const string HeaderEnd = "\nCDB_PRESET_END]]";
    public const int MaximumScriptBytes = 512_000;
    public const int MaximumHeaderCharacters = 16_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "npc_mutants", "player_state", "inventory", "world"
    };
    private static readonly Regex EffectFileName = new(
        "^pf_donation_action_[a-z0-9_]+\\.script$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CatalogKind = new(
        "^[a-z][a-z0-9_]{0,31}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _presetRoot;

    public FilePresetLoader(string? presetRoot = null)
    {
        _presetRoot = Path.GetFullPath(presetRoot ?? FindPresetRoot());
    }

    public string SourceScriptsDirectory => Path.Combine(_presetRoot, "scripts");

    public string Fingerprint(string? gamePath)
    {
        try
        {
            var files = SafeSourceFiles().ToList();
            var parts = new List<string> { gamePath ?? string.Empty };
            foreach (var file in files)
            {
                var source = new FileInfo(file);
                parts.Add($"{source.Name}:{source.Length}:{source.LastWriteTimeUtc.Ticks}");
                foreach (var root in InstallationRoots(gamePath))
                {
                    var installed = new FileInfo(Path.Combine(root, "scripts", source.Name));
                    parts.Add(installed.Exists
                        ? $"{root}:{installed.Length}:{installed.LastWriteTimeUtc.Ticks}"
                        : $"{root}:missing");
                }
            }
            return string.Join('|', parts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"scan_error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    public FilePresetLoadResult Load(string? gamePath)
    {
        var presets = new List<ActionDefinition>();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        if (!Directory.Exists(SourceScriptsDirectory)) return new(presets, sources, warnings);

        IReadOnlyList<string> files;
        try { files = SafeSourceFiles().ToList(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add($"Не удалось прочитать папку эффектов: {exception.Message}");
            return new(presets, sources, warnings);
        }

        foreach (var path in files)
        {
            try
            {
                var preset = ReadPreset(path);
                if (sources.ContainsKey(preset.HandlerId))
                    throw new InvalidDataException($"Повторный handlerId {preset.HandlerId}.");
                sources.Add(preset.HandlerId, path);
                if (IsInstalled(path, gamePath)) presets.Add(preset);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or NotSupportedException or ArgumentException)
            {
                warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return new(presets, sources, warnings);
    }

    public ActionDefinition ReadPreset(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Файл пресета не найден.", path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Ссылки и точки повторного разбора запрещены.");
        if (file.Length is <= 0 or > MaximumScriptBytes)
            throw new InvalidDataException("Размер Lua-файла должен быть от 1 до 512000 байт.");
        if (!EffectFileName.IsMatch(file.Name))
            throw new InvalidDataException("Имя файла должно иметь вид pf_donation_action_<id>.script.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(SourceScriptsDirectory));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл находится вне папки эффектов.");

        var script = File.ReadAllText(fullPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!script.StartsWith(HeaderStart, StringComparison.Ordinal))
            throw new InvalidDataException("Отсутствует заголовок CDB_PRESET_V1 в начале файла.");
        var end = script.IndexOf(HeaderEnd, HeaderStart.Length, StringComparison.Ordinal);
        if (end < 0 || end - HeaderStart.Length > MaximumHeaderCharacters)
            throw new InvalidDataException("Заголовок пресета отсутствует или слишком велик.");
        var header = JsonSerializer.Deserialize<FilePresetHeader>(
            script.AsSpan(HeaderStart.Length, end - HeaderStart.Length), JsonOptions)
            ?? throw new InvalidDataException("Пустой заголовок пресета.");
        return BuildPreset(header, file.Name);
    }

    public string ResolveInstalledScriptPath(string sourcePath, string root)
    {
        var addonRoot = ResolveAddonRoot(root);
        return ResolveUnderRoot(addonRoot, Path.Combine("scripts", Path.GetFileName(sourcePath)));
    }

    public static string ResolveAddonRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new DirectoryNotFoundException("Папка игры или аддона не выбрана.");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (File.Exists(Path.Combine(normalized, "addon.init")) ||
            string.Equals(Path.GetFileName(normalized), Path.GetFileName(ActionManifestLoader.AddonRelativeRoot), StringComparison.OrdinalIgnoreCase))
            return normalized;
        return ResolveUnderRoot(normalized, ActionManifestLoader.AddonRelativeRoot);
    }

    private IReadOnlyList<string> SafeSourceFiles()
    {
        var directory = new DirectoryInfo(SourceScriptsDirectory);
        if (!directory.Exists) return [];
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Папка эффектов не может быть ссылкой.");
        return directory.EnumerateFiles("*.script", SearchOption.TopDirectoryOnly)
            .Where(file => !string.Equals(
                file.Name,
                "pf_donation_action_spawn_dogs_3.script",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .ToList();
    }

    private static bool IsInstalled(string source, string? gamePath)
    {
        foreach (var root in InstallationRoots(gamePath))
        {
            try
            {
                var installed = ResolveUnderRoot(root, Path.Combine("scripts", Path.GetFileName(source)));
                if (!File.Exists(installed)) continue;
                if ((File.GetAttributes(installed) & FileAttributes.ReparsePoint) != 0) continue;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                // An inaccessible target is not proof that the effect is installed.
            }
        }
        return false;
    }

    private static IEnumerable<string> InstallationRoots(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) yield break;
        string root;
        try { root = ResolveAddonRoot(gamePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or InvalidDataException)
        { yield break; }
        yield return root;
    }

    private static ActionDefinition BuildPreset(FilePresetHeader header, string fileName)
    {
        if (header.SchemaVersion != 1) throw new InvalidDataException("Требуется schemaVersion 1.");
        var id = (header.HandlerId ?? string.Empty).Trim().ToLowerInvariant();
        if (!Validation.ActionId().IsMatch(id) || id.Length > 128)
            throw new InvalidDataException("Недопустимый handlerId.");
        var name = (header.DisplayName ?? string.Empty).Trim();
        var description = (header.Description ?? string.Empty).Trim();
        if (name.Length is 0 or > 128 || name.Any(char.IsControl) ||
            description.Length is 0 or > 1024 || description.Any(char.IsControl))
            throw new InvalidDataException("Недопустимые название или описание.");
        var category = (header.Category ?? string.Empty).Trim().ToLowerInvariant();
        if (!Categories.Contains(category)) throw new InvalidDataException("Неизвестная категория пресета.");
        if (header.CooldownSeconds is < 0 or > 86_400)
            throw new InvalidDataException("Недопустимый cooldownSeconds.");

        var definitions = header.ParameterDefinitions ?? throw new InvalidDataException("Не заданы parameterDefinitions.");
        if (definitions.Count > 32 || definitions.Any(definition => definition is null))
            throw new InvalidDataException("Недопустимый список параметров.");
        if (definitions.Select(definition => definition.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Count ||
            definitions.Any(definition => definition.Key is null || !Validation.ParameterKey().IsMatch(definition.Key)))
            throw new InvalidDataException("Повторный или недопустимый ключ параметра.");

        var rawValues = header.Parameters ?? throw new InvalidDataException("Не заданы parameters.");
        if (rawValues.Keys.Any(key => key is null || !Validation.ParameterKey().IsMatch(key)) ||
            rawValues.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != rawValues.Count)
            throw new InvalidDataException("Повторный или недопустимый ключ в parameters.");
        var values = rawValues.ToDictionary(
            pair => pair.Key.Trim().ToLowerInvariant(),
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        if (values.Any(pair => pair.Key is null || pair.Value is null) ||
            header.ItemSpawns?.Any(entry => entry is null) == true ||
            header.SpawnGroups?.Any(entry => entry is null) == true)
            throw new InvalidDataException("Пустое значение в заголовке пресета.");
        if (values.Keys.Any(key => definitions.All(definition => !string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Значение для необъявленного параметра.");

        foreach (var definition in definitions)
        {
            definition.Key = definition.Key.Trim().ToLowerInvariant();
            definition.DisplayName = (definition.DisplayName ?? string.Empty).Trim();
            definition.DefaultValue ??= string.Empty;
            definition.Options ??= [];
            definition.OptionGroups ??= new(StringComparer.OrdinalIgnoreCase);
            definition.OptionLabels ??= new(StringComparer.OrdinalIgnoreCase);
            definition.CatalogKind = (definition.CatalogKind ?? string.Empty).Trim().ToLowerInvariant();
            if (definition.DisplayName.Length is 0 or > 128 || definition.DisplayName.Any(char.IsControl))
                throw new InvalidDataException($"Недопустимое название параметра {definition.Key}.");
            if (!Enum.IsDefined(definition.Kind))
                throw new InvalidDataException($"Неизвестный тип параметра {definition.Key}.");
            if (definition.CatalogKind.Length > 0 && !CatalogKind.IsMatch(definition.CatalogKind))
                throw new InvalidDataException($"Недопустимый catalogKind параметра {definition.Key}.");
            if (definition.CatalogKind.Length > 0 && definition.Kind is not (ParameterKind.Choice or ParameterKind.ItemChoice or ParameterKind.MultiChoice))
                throw new InvalidDataException($"Каталог допустим только для выбора: {definition.Key}.");
            if (definition.Options.Count != definition.Options.Distinct(StringComparer.OrdinalIgnoreCase).Count() ||
                definition.Options.Any(option => !Validation.ItemSection().IsMatch(option)))
                throw new InvalidDataException($"Недопустимые или повторяющиеся варианты параметра {definition.Key}.");
            if (definition.Kind is ParameterKind.Choice or ParameterKind.ItemChoice or ParameterKind.MultiChoice && definition.Options.Count == 0)
                throw new InvalidDataException($"У параметра {definition.Key} отсутствуют варианты выбора.");
            if (definition.Minimum is { } minimum && definition.Maximum is { } maximum && minimum > maximum)
                throw new InvalidDataException($"Минимум параметра {definition.Key} больше максимума.");
            if (definition.MinimumSelections < 0 || definition.MinimumSelections > definition.Options.Count)
                throw new InvalidDataException($"Недопустимый minimumSelections параметра {definition.Key}.");
            var value = values.TryGetValue(definition.Key, out var saved) ? saved : definition.DefaultValue;
            if (definition.Kind == ParameterKind.Boolean) value = value.ToLowerInvariant();
            if (definition.Validate(value) is { } error) throw new InvalidDataException(error);
        }

        var preset = new ActionDefinition
        {
            Id = id,
            HandlerId = id,
            DisplayName = name,
            Description = description,
            CategoryId = category,
            ScriptRelativePath = Path.Combine(ActionManifestLoader.AddonRelativeRoot, "scripts", fileName),
            CooldownSeconds = header.CooldownSeconds,
            ParameterDefinitions = definitions.Select(definition => definition.Clone()).ToList(),
            Parameters = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
            ItemSpawns = (header.ItemSpawns ?? []).Select(entry => entry.Clone()).ToList(),
            SpawnGroups = (header.SpawnGroups ?? []).Select(entry => entry.Clone()).ToList()
        };
        foreach (var definition in preset.ParameterDefinitions)
        {
            if (!preset.Parameters.ContainsKey(definition.Key)) preset.Parameters[definition.Key] = definition.DefaultValue;
        }
        var errors = preset.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
        return preset;
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за разрешённую папку.");
        return fullPath;
    }

    private static string FindPresetRoot()
    {
        var shipped = Path.Combine(
            AppContext.BaseDirectory,
            "addon",
            Path.GetFileName(ActionManifestLoader.AddonRelativeRoot));
        if (Directory.Exists(Path.Combine(shipped, "scripts"))) return shipped;
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; cursor is not null && depth < 8; depth++, cursor = cursor.Parent)
        {
            var development = Path.Combine(cursor.FullName, "game-addon");
            if (Directory.Exists(Path.Combine(development, "scripts"))) return development;
        }
        return shipped;
    }
}

public sealed class FilePresetHeader
{
    public int SchemaVersion { get; set; }
    public string? HandlerId { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public int CooldownSeconds { get; set; } = 1;
    public List<ParameterDefinition>? ParameterDefinitions { get; set; } = [];
    public Dictionary<string, string>? Parameters { get; set; } = [];
    public List<ItemSpawnEntry>? ItemSpawns { get; set; } = [];
    public List<SpawnGroupEntry>? SpawnGroups { get; set; } = [];
}
