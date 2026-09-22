using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.Persistence;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions JsonLinesOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class JsonSettingsStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(AppPaths paths) => _paths = paths;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureExists();
        if (!File.Exists(_paths.SettingsFile))
        {
            return SettingsNormalizer.Normalize(null);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
            return SettingsNormalizer.Normalize(settings);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Файл settings.json повреждён. Исправьте или переименуйте его; приложение не будет молча сбрасывать настройки.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var persisted = SettingsNormalizer.Normalize(SettingsNormalizer.CreatePersistenceSnapshot(settings));
            var temporary = _paths.SettingsFile + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _paths.SettingsFile, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class JsonLineHistoryStore : IEventHistorySink
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLineHistoryStore(AppPaths paths) => _paths = paths;

    public async Task AppendAsync(EventHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _paths.EnsureExists();
        var line = JsonSerializer.Serialize(entry, JsonDefaults.JsonLinesOptions) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_paths.HistoryFile, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EventHistoryEntry>> LoadRecentAsync(int maximum = 500, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.HistoryFile))
        {
            return [];
        }

        var queue = new Queue<EventHistoryEntry>(Math.Max(1, maximum));
        using var stream = new FileStream(_paths.HistoryFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<EventHistoryEntry>(line, JsonDefaults.JsonLinesOptions);
                if (entry is null)
                {
                    continue;
                }

                queue.Enqueue(entry);
                while (queue.Count > maximum)
                {
                    queue.Dequeue();
                }
            }
            catch (JsonException)
            {
                // A torn final JSONL line after a crash is ignored; previous records remain usable.
            }
        }

        return queue.Reverse().ToList();
    }

    public async Task<EventHistoryEntry?> FindCommandSnapshotAsync(
        string donationId,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(donationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (!File.Exists(_paths.HistoryFile))
        {
            return null;
        }

        EventHistoryEntry? latest = null;
        using var stream = new FileStream(_paths.HistoryFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<EventHistoryEntry>(line, JsonDefaults.JsonLinesOptions);
                if (entry is not null &&
                    entry.State is DispatchState.Queued or DispatchState.Sent &&
                    string.Equals(entry.DonationId, donationId, StringComparison.Ordinal) &&
                    string.Equals(entry.CommandId, commandId, StringComparison.Ordinal))
                {
                    latest = entry;
                }
            }
            catch (JsonException)
            {
                // Match LoadRecentAsync: a damaged line must not discard earlier snapshots.
            }
        }

        return latest;
    }
}

public sealed class JsonLineProcessedDonationStore : IProcessedDonationStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ProcessedDonationRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessedDonationRecord> _commands = new(StringComparer.Ordinal);
    private bool _loaded;

    public JsonLineProcessedDonationStore(AppPaths paths) => _paths = paths;

    public async Task<ProcessedDonationRecord?> FindAsync(string donationId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _records.TryGetValue(donationId, out var record) ? record : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProcessedDonationRecord?> FindCommandAsync(
        string donationId,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _commands.TryGetValue(CommandKey(donationId, commandId), out var record) ? record : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(ProcessedDonationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        _paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _records[record.DonationId] = record;
            if (!string.IsNullOrWhiteSpace(record.CommandId))
            {
                _commands[CommandKey(record.DonationId, record.CommandId)] = record;
            }
            var line = JsonSerializer.Serialize(record, JsonDefaults.JsonLinesOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(_paths.ProcessedDonationsFile, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            if (File.Exists(_paths.ProcessedDonationsFile))
            {
                using var stream = new FileStream(_paths.ProcessedDonationsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<ProcessedDonationRecord>(line, JsonDefaults.JsonLinesOptions);
                        if (record is not null && !string.IsNullOrWhiteSpace(record.DonationId))
                        {
                            _records[record.DonationId] = record;
                            if (!string.IsNullOrWhiteSpace(record.CommandId))
                            {
                                _commands[CommandKey(record.DonationId, record.CommandId)] = record;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore only the damaged line; do not discard the valid ledger.
                    }
                }
            }

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CommandKey(string donationId, string commandId) => donationId + "\0" + commandId;
}

public sealed class JsonPendingDispatchStore : IPendingDispatchStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonPendingDispatchStore(AppPaths paths) => _paths = paths;

    public async Task<IReadOnlyList<PendingDispatchSnapshot>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.PendingQueueFile)) return [];
            await using var stream = new FileStream(
                _paths.PendingQueueFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<PendingDispatchSnapshot>>(
                stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Файл pending-queue.json повреждён; очередь не будет воспроизведена автоматически.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<PendingDispatchSnapshot> pending,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        _paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = _paths.PendingQueueFile + ".tmp";
            await using (var stream = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, pending, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _paths.PendingQueueFile, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class SafeFileLogger
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SafeFileLogger(AppPaths paths) => _paths = paths;

    public async Task WriteAsync(string level, string message, CancellationToken cancellationToken = default)
    {
        _paths.EnsureExists();
        var clean = SensitiveDataSanitizer.Redact((message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '));
        if (clean.Length > 2_000)
        {
            clean = clean[..2_000];
        }

        var line = $"{DateTimeOffset.UtcNow:O}\t{level}\t{clean}{Environment.NewLine}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_paths.LogFile, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static partial class SensitiveDataSanitizer
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = NamedSecret().Replace(value, match => match.Groups[1].Value + "=[REDACTED]");
        result = BearerToken().Replace(result, "Bearer [REDACTED]");
        result = JwtLike().Replace(result, "[REDACTED_TOKEN]");
        return result;
    }

    [GeneratedRegex("(?i)(client_secret|access_token|refresh_token|authorization)[\\\"']?\\s*(?:=|:)\\s*[\\\"']?(?:bearer\\s+)?[^\\s&,}\\\"']+")]
    private static partial Regex NamedSecret();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/-]{8,}")]
    private static partial Regex BearerToken();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{16,}(?:\\.[A-Za-z0-9_-]{8,}){1,2}\\b")]
    private static partial Regex JwtLike();
}
