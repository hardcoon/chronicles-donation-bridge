using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.GameIpc;

namespace ChroniclesDonationBridge.Tests;

internal static class IpcRecoveryTests
{
    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static GameCommand Command(string id = "pending") => new()
    {
        CommandId = id, DonationId = "donation-" + id, ActionId = "add_radiation",
        AmountMinor = 100, Currency = "RUB", Parameters = new() { ["percent"] = "25" }
    };

    private sealed class Session : IAsyncDisposable
    {
        public string PipeName { get; } = "ChroniclesBridge-RecoveryTest-" + Guid.NewGuid().ToString("N");
        public NamedPipeGameServer Server { get; }
        public ConcurrentQueue<GameExecutionResult> Results { get; } = new();
        public Session(TimeSpan? silence = null, TimeSpan? resultTimeout = null, Func<int, bool>? alive = null)
        {
            Server = new NamedPipeGameServer("test", PipeName, TimeSpan.FromMilliseconds(25),
                silence ?? TimeSpan.FromSeconds(3), resultTimeout ?? TimeSpan.FromMilliseconds(140), alive ?? (_ => true));
            Server.ResultReceived += Results.Enqueue;
            Server.Start();
        }
        public ValueTask DisposeAsync() => Server.DisposeAsync();
        public async Task<Client> ConnectAsync(string sessionId = "lua-session-a", int? processId = null)
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000);
            var client = new Client(pipe);
            await client.SendAsync($"HELLO\t2\t0.2.0\t{processId ?? Environment.ProcessId}\t{sessionId}");
            await client.ReadAsync("WELCOME\t2\t");
            await client.SendAsync("STATUS\tready\tok");
            await WaitUntilAsync(() => Server.IsGameReady);
            return client;
        }
    }

    private sealed class Client : IDisposable
    {
        public NamedPipeClientStream Pipe { get; }
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        public Client(NamedPipeClientStream pipe)
        {
            Pipe = pipe;
            _reader = new StreamReader(Pipe, new UTF8Encoding(false), false, 4096, true);
            _writer = new StreamWriter(Pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
        }
        public Task SendAsync(string line) => _writer.WriteLineAsync(line);
        public async Task<string> ReadAsync(string prefix)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Pipe closed");
                if (line == "PING") { await SendAsync("PONG"); continue; }
                Assert(line.StartsWith(prefix, StringComparison.Ordinal), $"Expected {prefix}, got {line}");
                return line;
            }
        }
        public void Dispose() { _writer.Dispose(); _reader.Dispose(); Pipe.Dispose(); }
    }

    public static async Task PauseAndResumeAsync()
    {
        await using var session = new Session(TimeSpan.FromMilliseconds(110));
        using var client = await session.ConnectAsync();
        await session.Server.SendAsync(Command());
        await client.ReadAsync("COMMAND\tpending\t");
        // Lua is completely suspended: no STATUS or PONG, longer than ACK timeout.
        await Task.Delay(350);
        Assert(session.Server.IsConnected && !session.Server.IsGameReady, "Pause closed the transport or left it ready");
        Assert(session.Server.IsAwaitingResult("pending") && session.Results.IsEmpty, "Pause expired an in-flight command");
        var waiting = await session.Server.RecoverPendingResultsAsync();
        Assert(waiting.RemainingCount == 1 && !waiting.QueryRequested, "Recovery wrote into a suspended game");
        await client.SendAsync("STATUS\tready\tok");
        var query = await client.ReadAsync("RESULT_QUERY\tpending\t");
        Assert(query.EndsWith("lua-session-a"), "Query was not scoped to the Lua session");
        await client.SendAsync("RESULT\tpending\texecuted\tok");
        await WaitUntilAsync(() => !session.Server.IsAwaitingResult("pending"));
        Assert(session.Results.Count == 1 && session.Results.Single().Status == GameResultStatus.Executed, "Resume lost or duplicated result");
    }

    public static async Task LateAckAsync()
    {
        await using var session = new Session();
        using var client = await session.ConnectAsync();
        await session.Server.SendAsync(Command());
        await client.ReadAsync("COMMAND\t");
        await Task.Delay(250);
        Assert(session.Server.IsAwaitingResult("pending") && session.Results.IsEmpty, "Delayed ACK was discarded");
        await client.SendAsync("RESULT\tpending\texecuted\tlate");
        await WaitUntilAsync(() => !session.Server.IsAwaitingResult("pending"));
        Assert(session.Results.Single().Status == GameResultStatus.Executed, "Late ACK became uncertain");
    }

    public static async Task HealthyRecoveryAndPersistenceAsync()
    {
        await using var session = new Session();
        using var client = await session.ConnectAsync();
        var persistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Server.ResultProcessing += _ => persistence.Task;
        await session.Server.SendAsync(Command());
        await client.ReadAsync("COMMAND\t");
        var refresh = session.Server.RecoverPendingResultsAsync();
        await client.ReadAsync("RESULT_QUERY\tpending\t");
        await client.SendAsync("RESULT\tpending\texecuted\tcached");
        await WaitUntilAsync(() => session.Results.Count == 1);
        Assert(!refresh.IsCompleted, "Refresh completed before result persistence");
        persistence.SetResult();
        var report = await refresh.WaitAsync(TimeSpan.FromSeconds(4));
        Assert(report.ResolvedCount == 1 && report.RemainingCount == 0 && session.Server.IsConnected, "Healthy refresh disconnected or lost result");
        await client.SendAsync("RESULT\tpending\texecuted\tcached");
        await Task.Delay(40);
        Assert(session.Results.Count == 1, "Duplicate ACK was delivered twice");
    }

    public static async Task ReconnectSameSessionAsync()
    {
        await using var session = new Session();
        using (var first = await session.ConnectAsync())
        {
            await session.Server.SendAsync(Command());
            await first.ReadAsync("COMMAND\t");
        }
        await WaitUntilAsync(() => !session.Server.IsConnected);
        Assert(session.Server.IsAwaitingResult("pending") && session.Results.IsEmpty, "Transient disconnect finalized a command");
        using var second = await session.ConnectAsync();
        await second.ReadAsync("RESULT_QUERY\tpending\t");
        await second.SendAsync("RESULT\tpending\texecuted\tcached");
        await WaitUntilAsync(() => !session.Server.IsAwaitingResult("pending"));
        Assert(session.Results.Single().Status == GameResultStatus.Executed, "Same-session recovery failed");
    }

    public static async Task ChangedSessionAsync()
    {
        await using var session = new Session();
        using (var first = await session.ConnectAsync())
        {
            await session.Server.SendAsync(Command());
            await first.ReadAsync("COMMAND\t");
        }
        await WaitUntilAsync(() => !session.Server.IsConnected);
        // Same Windows PID, different Lua runtime: must also fail closed.
        using var second = await session.ConnectAsync("lua-session-b");
        await WaitUntilAsync(() => !session.Server.IsAwaitingResult("pending"));
        Assert(session.Results.Single().Status == GameResultStatus.Uncertain, "Changed Lua session replayed a command");
        Assert((await session.Server.RecoverPendingResultsAsync()).RequestedCount == 0, "Terminal uncertain was re-queried");
    }

    public static async Task ProcessExitAsync()
    {
        var alive = true;
        await using var session = new Session(alive: _ => Volatile.Read(ref alive));
        using (var client = await session.ConnectAsync())
        {
            await session.Server.SendAsync(Command());
            await client.ReadAsync("COMMAND\t");
        }
        Volatile.Write(ref alive, false);
        await WaitUntilAsync(() => !session.Server.IsAwaitingResult("pending"));
        Assert(session.Results.Single().Status == GameResultStatus.Uncertain, "Process exit left queue blocked forever");
    }

    public static async Task UnknownResultAsync()
    {
        await using var session = new Session();
        using var client = await session.ConnectAsync();
        await session.Server.SendAsync(Command());
        await client.ReadAsync("COMMAND\t");
        var refresh = session.Server.RecoverPendingResultsAsync();
        await client.ReadAsync("RESULT_QUERY\tpending\t");
        await client.SendAsync("RESULT\tpending\tuncertain\tresult_not_cached");
        var report = await refresh.WaitAsync(TimeSpan.FromSeconds(4));
        Assert(report.RemainingCount == 0 && session.Results.Single().Status == GameResultStatus.Uncertain,
            "A missing cached result was treated as successful or re-executed");
    }

    public static Task SessionProtocolAsync()
    {
        var parsed = (HelloMessage)PipeProtocol.ParseInbound("HELLO\t2\t0.2.0\t123\tlua-123");
        Assert(parsed.SessionId == "lua-123", "Session ID was lost");
        Assert(((HelloMessage)PipeProtocol.ParseInbound("HELLO\t1\t0.1.0\t123")).SessionId == "", "Legacy HELLO broken");
        foreach (var line in new[] { "HELLO\t2\t0.2.0\t123\t", "HELLO\t2\t0.2.0\t123\tbad%0A", "HELLO\t3\t0.2.0\t123\tsession", "HELLO\t1\t0.2.0\t123\tsession" })
        {
            try { PipeProtocol.ParseInbound(line); throw new Exception("Invalid HELLO was accepted"); }
            catch (InvalidDataException) { }
        }
        return Task.CompletedTask;
    }

    public static async Task QueueRoundTripAsync()
    {
        await using var session = new Session();
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history) { GlobalCooldown = TimeSpan.Zero };
        var policy = new QueuePolicy { Mode = QueueMode.WaitIndefinitely };
        var action = DefaultActionCatalog.CreatePresets().Single(a => a.HandlerId == "add_radiation").CloneAsNewInstance();
        action.CooldownSeconds = 0;
        foreach (var id in new[] { "roundtrip-a", "roundtrip-b" })
            await dispatcher.EnqueueActionAsync(new DonationEvent { Id = id, Amount = 1m, Currency = "RUB", CreatedAt = DateTimeOffset.UtcNow }, action, policy);
        session.Server.ResultProcessing += async result => { await dispatcher.CompleteAsync(result); };
        string commandId;
        using (var first = await session.ConnectAsync())
        {
            await dispatcher.DrainAsync(session.Server, policy);
            var command = (await first.ReadAsync("COMMAND\t")).Split('\t');
            Assert(command[2] == "roundtrip-a", "First queued donation was overtaken");
            commandId = command[1];
            await dispatcher.DrainAsync(session.Server, policy);
            Assert(dispatcher.Count == 1, "Second command left queue before ACK");
        }
        await WaitUntilAsync(() => !session.Server.IsConnected);
        await dispatcher.DrainAsync(session.Server, policy);
        Assert(dispatcher.Count == 1, "Disconnect consumed the next command");
        using var second = await session.ConnectAsync();
        var query = (await second.ReadAsync("RESULT_QUERY\t")).Split('\t');
        Assert(query[1] == commandId, "Recovery generated a different attempt ID");
        await second.SendAsync($"RESULT\t{commandId}\texecuted\tcached");
        await WaitUntilAsync(() => !session.Server.IsAwaitingResult(commandId));
        await dispatcher.DrainAsync(session.Server, policy);
        var next = (await second.ReadAsync("COMMAND\t")).Split('\t');
        Assert(next[2] == "roundtrip-b" && next[1] != commandId, "Queue order or command identity changed");
        await second.SendAsync($"RESULT\t{next[1]}\texecuted\tok");
        await WaitUntilAsync(() => !session.Server.IsAwaitingResult(next[1]));
        Assert(history.Entries.Count(e => e.State == DispatchState.Executed) == 2, "History duplicated or lost completion");
    }
}
