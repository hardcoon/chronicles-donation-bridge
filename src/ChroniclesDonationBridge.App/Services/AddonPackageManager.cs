using ChroniclesDonationBridge.Persistence;

namespace ChroniclesDonationBridge.App.Services;

public enum AddonInstallState
{
    PackageUnavailable,
    InvalidPackage,
    NotInstalled,
    Current,
    UpdateAvailable
}

public sealed record AddonInstallationStatus(
    AddonInstallState State,
    string Message,
    string PackageVersion = "0.6.3")
{
    public bool CanInstall => State is AddonInstallState.NotInstalled or AddonInstallState.UpdateAvailable;
}

/// <summary>
/// Installs the bridge-owned Chronicles add-on files as one recoverable
/// transaction. Known targets are backed up before replacement; files absent
/// from the package are deliberately left alone.
/// </summary>
public sealed class AddonPackageManager
{
    public const string ShippedAddonVersion = "0.6.3";
    private static readonly string[] ObsoleteRelativePaths =
    [
        "configs/mod_system_pf_donation_random_squads.ltx"
    ];
    private readonly AppPaths _paths;
    private readonly string _packageRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public AddonPackageManager(AppPaths paths, string? packageRoot = null)
    {
        _paths = paths;
        _packageRoot = Path.GetFullPath(packageRoot ?? FindPackageRoot());
    }

    public Task<AddonInstallationStatus> InspectAsync(string gameRoot, CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(gameRoot, cancellationToken), cancellationToken);

    public async Task<AddonInstallationStatus> InstallOrUpdateAsync(
        string gameRoot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Install(gameRoot, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private AddonInstallationStatus Inspect(string gameRoot, CancellationToken cancellationToken)
    {
        try
        {
            var package = ReadPackage(cancellationToken);
            var targetRoot = ResolveTargetRoot(gameRoot);
            var missing = 0;
            var changed = 0;
            var obsolete = 0;
            foreach (var source in package)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveUnderRoot(targetRoot, source.RelativePath);
                EnsureSafePath(targetRoot, target);
                if (!File.Exists(target)) missing++;
                else if (!FilesEqual(source.Source, target)) changed++;
            }
            foreach (var relativePath in ObsoleteRelativePaths)
            {
                var target = ResolveUnderRoot(targetRoot, relativePath);
                EnsureSafePath(targetRoot, target);
                if (File.Exists(target)) obsolete++;
            }

            if (missing == package.Count)
                return new(AddonInstallState.NotInstalled, $"Аддон не установлен. Доступна версия {ShippedAddonVersion}.");
            if (missing > 0 || changed > 0 || obsolete > 0)
                return new(AddonInstallState.UpdateAvailable,
                    $"Доступно обновление с резервной копией: отсутствует {missing}, отличается {changed}, устарело {obsolete} файлов.");
            return new(AddonInstallState.Current, $"Аддон {ShippedAddonVersion} установлен.");
        }
        catch (FileNotFoundException exception)
        { return new(AddonInstallState.PackageUnavailable, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { return new(AddonInstallState.InvalidPackage, exception.Message); }
    }

    private AddonInstallationStatus Install(string gameRoot, CancellationToken cancellationToken)
    {
        var status = Inspect(gameRoot, cancellationToken);
        if (status.State == AddonInstallState.Current) return status;
        if (!status.CanInstall) throw new InvalidOperationException(status.Message);

        var package = ReadPackage(cancellationToken);
        var targetRoot = ResolveTargetRoot(gameRoot);
        var stageRoot = Path.Combine(_paths.RootDirectory, "install-staging", Guid.NewGuid().ToString("N"));
        var backupRoot = Path.Combine(_paths.AddonBackupsDirectory,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
        var replaced = new List<(PackageFile File, string Target, string? Backup)>();
        var removed = new List<(string Target, string Backup)>();
        try
        {
            foreach (var file in package)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveUnderRoot(targetRoot, file.RelativePath);
                EnsureSafePath(targetRoot, target);
            }

            foreach (var file in package)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = ResolveUnderRoot(stageRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(file.Source, staged, true);
                if (!FilesEqual(file.Source, staged))
                    throw new IOException($"Файл изменился при подготовке {file.RelativePath}.");
            }

            foreach (var file in package)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveUnderRoot(targetRoot, file.RelativePath);
                if (File.Exists(target) && FilesEqual(file.Source, target)) continue;
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = ResolveUnderRoot(backupRoot, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, false);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".cdb.tmp";
                File.Copy(ResolveUnderRoot(stageRoot, file.RelativePath), temporary, true);
                if (!FilesEqual(file.Source, temporary))
                    throw new IOException($"Файл изменился при копировании {file.RelativePath}.");
                File.Move(temporary, target, true);
                replaced.Add((file, target, backup));
            }

            foreach (var relativePath in ObsoleteRelativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveUnderRoot(targetRoot, relativePath);
                EnsureSafePath(targetRoot, target);
                if (!File.Exists(target)) continue;
                var backup = ResolveUnderRoot(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, false);
                File.Delete(target);
                removed.Add((target, backup));
            }

            return new(AddonInstallState.Current,
                $"Аддон {ShippedAddonVersion} установлен; обработано файлов: {package.Count}. Неизвестные файлы сохранены.");
        }
        catch
        {
            foreach (var changed in removed.AsEnumerable().Reverse())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(changed.Target)!);
                    File.Copy(changed.Backup, changed.Target, true);
                }
                catch { }
            }
            foreach (var changed in replaced.AsEnumerable().Reverse())
            {
                try
                {
                    if (changed.Backup is not null && File.Exists(changed.Backup)) File.Copy(changed.Backup, changed.Target, true);
                    else if (File.Exists(changed.Target)) File.Delete(changed.Target);
                }
                catch { }
            }
            throw;
        }
        finally
        {
            DeleteStagingDirectory(stageRoot);
        }
    }

    private List<PackageFile> ReadPackage(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_packageRoot))
            throw new FileNotFoundException("В поставке отсутствует папка игрового аддона.", _packageRoot);
        var root = new DirectoryInfo(_packageRoot);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Папка пакета аддона не может быть ссылкой.");
        foreach (var directory in root.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Пакет содержит ссылку: {directory.Name}.");
        }
        var result = new List<PackageFile>();
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)
                     .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Пакет содержит ссылку: {file.Name}.");
            var relative = NormalizeRelativePath(Path.GetRelativePath(_packageRoot, file.FullName));
            result.Add(new(file.FullName, relative));
        }
        if (result.Count == 0 || result.All(file => !string.Equals(file.RelativePath, "addon.init", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Пакет аддона пуст или не содержит addon.init.");
        return result;
    }

    private static string ResolveTargetRoot(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) throw new InvalidDataException("Не выбрана папка игры.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        if (!File.Exists(Path.Combine(root, "fsgame.ltx")) || !Directory.Exists(Path.Combine(root, "bin")))
            throw new InvalidDataException("Выбранная папка не похожа на Chronicles of Pripyat IX-Ray.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Папка игры не может быть ссылкой.");
        return ResolveUnderRoot(root, ActionManifestLoader.AddonRelativeRoot);
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized) || normalized.Length is 0 or > 240 ||
            normalized.Split('/').Any(part => part is "" or "." or "..") ||
            normalized.Any(char.IsControl))
            throw new InvalidDataException($"Недопустимый путь пакета: {path}.");
        return normalized;
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Путь выходит за разрешённую папку: {relativePath}.");
        return fullPath;
    }

    private static void EnsureSafePath(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь установки выходит за папку аддона.");
        var cursor = fullPath;
        while (!string.Equals(cursor, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) &&
                (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Ссылки запрещены в пути установки: {cursor}");
            cursor = Path.GetDirectoryName(cursor)
                ?? throw new InvalidDataException("Не удалось проверить путь установки.");
        }
        if (Directory.Exists(normalizedRoot) &&
            (File.GetAttributes(normalizedRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Папка аддона не может быть ссылкой.");
    }

    private void DeleteStagingDirectory(string stageRoot)
    {
        if (!Directory.Exists(stageRoot)) return;
        var stagingParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Combine(_paths.RootDirectory, "install-staging")));
        var target = Path.GetFullPath(stageRoot);
        if (!target.StartsWith(stagingParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Отказано в очистке неожиданной временной папки.");
        EnsureSafePath(stagingParent, target);
        Directory.Delete(target, true);
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length) return false;
        using var leftStream = leftInfo.OpenRead();
        using var rightStream = rightInfo.OpenRead();
        var leftBuffer = new byte[8192];
        var rightBuffer = new byte[8192];
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
        }
    }

    private static string FindPackageRoot()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "addon", Path.GetFileName(ActionManifestLoader.AddonRelativeRoot));
        if (File.Exists(Path.Combine(shipped, "addon.init"))) return shipped;
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; cursor is not null && depth < 8; depth++, cursor = cursor.Parent)
        {
            var development = Path.Combine(cursor.FullName, "game-addon");
            if (File.Exists(Path.Combine(development, "addon.init"))) return development;
        }
        return shipped;
    }

    private sealed record PackageFile(string Source, string RelativePath);
}
