using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.GameIpc;

public sealed record GameConnectionSnapshot(
    bool Connected, bool Ready, string Reason, string AddonVersion, int GameProcessId);

public sealed record ResultRecoveryReport(
    int RequestedCount, int ResolvedCount, int RemainingCount, bool GameConnected, bool QueryRequested);

public sealed class NamedPipeGameServer : IGameCommandTransport, IAsyncDisposable
{
    private sealed class PendingResult(int processId, string sessionId)
    {
        public int ProcessId { get; } = processId;
        public string SessionId { get; } = sessionId;
        public DateTimeOffset SentAt { get; } = DateTimeOffset.UtcNow;
        public bool NeedsRecovery { get; set; }
        public bool RecoverySuggested { get; set; }
        public bool Completing { get; set; }
    }

    private readonly string _applicationVersion;
    private readonly string _pipeName;
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _heartbeatSilence;
    private readonly TimeSpan _resultTimeout;
    private readonly TimeSpan _terminalResultTimeout;
    private readonly Func<int, bool> _processIsAlive;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly Dictionary<string, PendingResult> _awaitingResults = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();
    private NamedPipeServerStream? _activePipe;
    private Task? _serverTask;
    private Task? _healthTask;
    private bool _gameReady;
    private bool _reportedReady;
    private bool _heartbeatSuspended;
    private bool _pingOutstanding;
    private bool _everConnected;
    private DateTimeOffset _lastActivity;
    private string _reportedReason = "ok";
    private string _reason = "Ожидание игры";
    private string _addonVersion = string.Empty;
    private string _sessionId = string.Empty;
    private int _gameProcessId;

    public NamedPipeGameServer(string applicationVersion)
        : this(applicationVersion, PipeProtocol.PipeName) { }

    public NamedPipeGameServer(string applicationVersion, string pipeName,
        TimeSpan? pingInterval = null, TimeSpan? heartbeatSilence = null,
        TimeSpan? resultTimeout = null, Func<int, bool>? processIsAlive = null,
        TimeSpan? terminalResultTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _applicationVersion = applicationVersion;
        _pipeName = pipeName;
        _pingInterval = pingInterval ?? TimeSpan.FromSeconds(10);
        _heartbeatSilence = heartbeatSilence ?? TimeSpan.FromSeconds(25);
        _resultTimeout = resultTimeout ?? TimeSpan.FromSeconds(30);
        _terminalResultTimeout = terminalResultTimeout ?? TimeSpan.FromSeconds(60);
        if (_pingInterval <= TimeSpan.Zero || _heartbeatSilence <= TimeSpan.Zero ||
            _resultTimeout <= TimeSpan.Zero || _terminalResultTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pingInterval));
        _processIsAlive = processIsAlive ?? ProcessIsAlive;
    }

    public event Action<GameConnectionSnapshot>? ConnectionChanged;
    public event Action<GameExecutionResult>? ResultReceived;
    // Await persistence before reporting recovery complete to the history UI.
    public event Func<GameExecutionResult, Task>? ResultProcessing;
    public event Action<Exception>? ProtocolError;

    public bool IsConnected { get { lock (_stateGate) return _activePipe?.IsConnected == true; } }
    public bool IsGameReady
    {
        get
        {
            lock (_stateGate)
                return _activePipe?.IsConnected == true && _gameReady &&
                    DateTimeOffset.UtcNow - _lastActivity < _heartbeatSilence;
        }
    }

    public bool IsAwaitingResult(string commandId)
    {
        lock (_stateGate) return _awaitingResults.ContainsKey(commandId);
    }

    public void Start()
    {
        if (_serverTask is not null) return;
        _serverTask = Task.Run(() => ServerLoopAsync(_lifetime.Token));
        _healthTask = Task.Run(() => HealthLoopAsync(_lifetime.Token));
    }

    public async Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
    {
        var line = PipeProtocol.Command(command);
        NamedPipeServerStream pipe;
        lock (_stateGate)
        {
            pipe = _activePipe is { IsConnected: true } connected
                ? connected : throw new InvalidOperationException("Игра не подключена.");
            if (!IsGameReady) throw new InvalidOperationException("Игра ещё не готова принимать эффекты.");
            if (!_awaitingResults.TryAdd(command.CommandId, new PendingResult(_gameProcessId, _sessionId)))
                throw new InvalidOperationException("Эта команда уже ожидает ответа игры.");
        }
        try { await WriteLineAsync(pipe, line, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            // A failed write may have reached Lua. Keep SENT and resolve it with a
            // read-only query; never resend COMMAND or release the next queued item.
            BreakTransport(pipe, exception);
        }
    }

    public async Task<ResultRecoveryReport> RecoverPendingResultsAsync(
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        string[] ids;
        lock (_stateGate)
        {
            ids = _awaitingResults.Keys.ToArray();
            foreach (var pending in _awaitingResults.Values) pending.NeedsRecovery = true;
        }
        var queried = await QueryPendingAsync(linked.Token).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (queried > 0 && DateTimeOffset.UtcNow < deadline && ids.Any(IsAwaitingResult) && IsGameReady)
            await Task.Delay(50, linked.Token).ConfigureAwait(false);
        var remaining = ids.Count(IsAwaitingResult);
        return new ResultRecoveryReport(ids.Length, ids.Length - remaining, remaining, IsConnected, queried > 0);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        lock (_stateGate) _activePipe?.Dispose();
        foreach (var task in new[] { _serverTask, _healthTask })
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _writeGate.Dispose();
        _recoveryGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    PipeProtocol.MaximumLineBytes, PipeProtocol.MaximumLineBytes);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleSessionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (IsTransportFailure(exception) || exception is InvalidDataException or UnauthorizedAccessException)
            {
                if (!cancellationToken.IsCancellationRequested) ProtocolError?.Invoke(exception);
            }
            finally { DisconnectActiveSession(); }
            await ResolveEndedProcessesAsync().ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleSessionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(TimeSpan.FromSeconds(5));
        var firstLine = await ReadLineAsync(pipe, handshake.Token).ConfigureAwait(false);
        if (PipeProtocol.ParseInbound(firstLine) is not HelloMessage hello)
            throw new InvalidDataException("Первым сообщением игры должен быть HELLO.");

        string[] changedSessionIds;
        lock (_stateGate)
        {
            _activePipe = pipe;
            _gameReady = _reportedReady = _heartbeatSuspended = _pingOutstanding = false;
            _everConnected = true;
            _reason = "Аддон подключён, ожидается готовность игры";
            _reportedReason = "game_loading";
            _addonVersion = hello.AddonVersion;
            _gameProcessId = hello.GameProcessId;
            _sessionId = hello.SessionId;
            _lastActivity = DateTimeOffset.UtcNow;
            changedSessionIds = _awaitingResults.Where(pair => pair.Value.ProcessId != _gameProcessId ||
                    string.IsNullOrEmpty(_sessionId) || pair.Value.SessionId != _sessionId)
                .Select(pair => pair.Key).ToArray();
        }
        foreach (var id in changedSessionIds)
            await CompletePendingAsync(Uncertain(id, "Игровая сессия сменилась; эффект автоматически не повторяется")).ConfigureAwait(false);
        PublishSnapshot();
        await WriteLineAsync(pipe, PipeProtocol.Welcome(_applicationVersion, hello.ProtocolVersion), cancellationToken).ConfigureAwait(false);
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            var message = PipeProtocol.ParseInbound(await ReadLineAsync(pipe, cancellationToken).ConfigureAwait(false));
            var resumed = false;
            lock (_stateGate)
            {
                _lastActivity = DateTimeOffset.UtcNow;
                if (_heartbeatSuspended)
                {
                    _heartbeatSuspended = false;
                    _gameReady = _reportedReady;
                    _reason = _reportedReady ? ReadyReasonUnderLock() : _reportedReason;
                    resumed = true;
                }
            }
            switch (message)
            {
                case StatusMessage status:
                    lock (_stateGate)
                    {
                        _gameReady = _reportedReady = status.Ready;
                        _reportedReason = status.Reason;
                        _reason = status.Ready ? ReadyReasonUnderLock() : status.Reason;
                    }
                    PublishSnapshot();
                    if (status.Ready) await QueryPendingAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ResultMessage result:
                    await CompletePendingAsync(result.Result).ConfigureAwait(false);
                    break;
                case PongMessage:
                    lock (_stateGate) _pingOutstanding = false;
                    break;
                case HelloMessage:
                    throw new InvalidDataException("Повторный HELLO в активной сессии запрещён.");
            }
            if (resumed)
            {
                PublishSnapshot();
                await QueryPendingAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pingInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await ResolveEndedProcessesAsync().ConfigureAwait(false);
            NamedPipeServerStream? pingPipe = null;
            string[] timedOutIds = [];
            var changed = false;
            lock (_stateGate)
            {
                var now = DateTimeOffset.UtcNow;
                timedOutIds = _awaitingResults
                    .Where(pair => !pair.Value.Completing &&
                        now - pair.Value.SentAt >= _terminalResultTimeout)
                    .Select(pair => pair.Key)
                    .ToArray();
                if (_activePipe?.IsConnected == true)
                {
                    if (!_heartbeatSuspended && now - _lastActivity >= _heartbeatSilence)
                    {
                        _heartbeatSuspended = true;
                        _gameReady = false;
                        _reason = "game_suspended";
                        changed = true;
                    }
                    if (_gameReady && _awaitingResults.Values.Any(p =>
                            !p.Completing && !p.RecoverySuggested && now - p.SentAt >= _resultTimeout))
                    {
                        foreach (var pending in _awaitingResults.Values) pending.RecoverySuggested = true;
                        _reason = "result_delayed";
                        changed = true;
                    }
                    // Only one unanswered ping: an hours-long pause must not fill
                    // the buffer and turn a harmless Alt+Tab into a disconnect.
                    if (!_pingOutstanding)
                    {
                        _pingOutstanding = true;
                        pingPipe = _activePipe;
                    }
                }
            }
            foreach (var commandId in timedOutIds)
            {
                await CompletePendingAsync(Uncertain(
                    commandId,
                    $"Игра не подтвердила выполнение за {(int)_terminalResultTimeout.TotalSeconds} сек.; автоматический повтор запрещён"))
                    .ConfigureAwait(false);
            }
            if (changed) PublishSnapshot();
            if (pingPipe is not null)
            {
                try { await WriteLineAsync(pingPipe, PipeProtocol.Ping(), cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (IsTransportFailure(exception)) { BreakTransport(pingPipe, exception); }
            }
        }
    }

    private async Task<int> QueryPendingAsync(CancellationToken cancellationToken)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NamedPipeServerStream? pipe;
            string sessionId;
            string[] ids;
            lock (_stateGate)
            {
                if (!IsGameReady) return 0;
                pipe = _activePipe;
                sessionId = _sessionId;
                ids = _awaitingResults.Where(p => p.Value.NeedsRecovery && !p.Value.Completing)
                    .Select(p => p.Key).ToArray();
                foreach (var id in ids) _awaitingResults[id].NeedsRecovery = false;
            }
            var sent = 0;
            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    await CompletePendingAsync(Uncertain(id, "Старый аддон не поддерживает запрос результата; обновите аддон")).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    await WriteLineAsync(pipe!, PipeProtocol.ResultQuery(id, sessionId), cancellationToken).ConfigureAwait(false);
                    sent++;
                }
                catch (Exception exception) when (IsTransportFailure(exception))
                {
                    BreakTransport(pipe!, exception);
                    break;
                }
            }
            return sent;
        }
        finally { _recoveryGate.Release(); }
    }

    private void BreakTransport(NamedPipeServerStream pipe, Exception exception)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_activePipe, pipe))
            {
                _gameReady = false;
                foreach (var pending in _awaitingResults.Values) pending.NeedsRecovery = true;
            }
        }
        pipe.Dispose();
        if (!_lifetime.IsCancellationRequested) ProtocolError?.Invoke(exception);
    }

    private void DisconnectActiveSession()
    {
        lock (_stateGate)
        {
            _activePipe = null;
            _gameReady = _reportedReady = _heartbeatSuspended = _pingOutstanding = false;
            _reason = _everConnected ? "Соединение с игрой потеряно · ожидание переподключения" : "Ожидание игры";
            _addonVersion = _sessionId = string.Empty;
            _gameProcessId = 0;
            foreach (var pending in _awaitingResults.Values) pending.NeedsRecovery = true;
        }
        PublishSnapshot();
    }

    private async Task ResolveEndedProcessesAsync()
    {
        KeyValuePair<string, PendingResult>[] pending;
        lock (_stateGate) pending = _awaitingResults.Where(p => !p.Value.Completing).ToArray();
        foreach (var pair in pending)
        {
            // Legacy add-ons have no session identity, so a disconnected command
            // cannot be queried safely even if Windows has reused the same PID.
            if (!_processIsAlive(pair.Value.ProcessId) ||
                (pair.Value.NeedsRecovery && string.IsNullOrEmpty(pair.Value.SessionId)))
                await CompletePendingAsync(Uncertain(pair.Key,
                    "Игровая сессия завершилась или не поддерживает восстановление; эффект не повторяется")).ConfigureAwait(false);
        }
    }

    private async Task CompletePendingAsync(GameExecutionResult result)
    {
        lock (_stateGate)
        {
            if (!_awaitingResults.TryGetValue(result.CommandId, out var pending) || pending.Completing) return;
            pending.Completing = true;
        }
        try
        {
            ResultReceived?.Invoke(result);
            if (ResultProcessing is { } processing)
                foreach (var handler in processing.GetInvocationList().Cast<Func<GameExecutionResult, Task>>())
                    await handler(result).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateGate)
            {
                _awaitingResults.Remove(result.CommandId);
                if (_reason == "result_delayed") _reason = ReadyReasonUnderLock();
            }
            PublishSnapshot();
        }
    }

    private static GameExecutionResult Uncertain(string id, string reason) => new()
    {
        CommandId = id, Status = GameResultStatus.Uncertain, Reason = reason, ReceivedAt = DateTimeOffset.UtcNow
    };

    private string ReadyReasonUnderLock() => _awaitingResults.Values.Any(p => p.RecoverySuggested && !p.Completing)
        ? "result_delayed" : _reportedReason;

    private static bool ProcessIsAlive(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // Unknown is not proof of exit.
    }

    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException or OperationCanceledException or ObjectDisposedException;

    private void PublishSnapshot()
    {
        GameConnectionSnapshot snapshot;
        lock (_stateGate)
            snapshot = new GameConnectionSnapshot(IsConnected, IsGameReady, _reason, _addonVersion, _gameProcessId);
        ConnectionChanged?.Invoke(snapshot);
    }

    private async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        if (bytes.Length > PipeProtocol.MaximumLineBytes)
            throw new InvalidDataException("Исходящая строка протокола превышает 16 КиБ.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(3));
        await _writeGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, bounded.Token).ConfigureAwait(false);
            await stream.FlushAsync(bounded.Token).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[PipeProtocol.MaximumLineBytes];
        var one = new byte[1];
        var length = 0;
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                throw new EndOfStreamException("Канал игры закрыт.");
            if (one[0] == (byte)'\n')
            {
                if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
                return new UTF8Encoding(false, true).GetString(bytes, 0, length);
            }
            if (length >= PipeProtocol.MaximumLineBytes - 1)
                throw new InvalidDataException("Входящая строка протокола превышает 16 КиБ.");
            bytes[length++] = one[0];
        }
    }
}
