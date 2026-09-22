namespace ChroniclesDonationBridge.App.Services;

internal sealed class SingleInstanceGate : IDisposable
{
    private const string InstanceName = @"Local\ChroniclesDonationBridge.App.v1";
    private Mutex? _mutex;

    private SingleInstanceGate(Mutex mutex) => _mutex = mutex;

    public static SingleInstanceGate? TryAcquire(string? name = null)
    {
        // Existence, not thread ownership: async startup/shutdown may resume on
        // another thread. A process crash also releases this handle automatically.
        var mutex = new Mutex(false, name ?? InstanceName, out var createdNew);
        if (createdNew) return new SingleInstanceGate(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => Interlocked.Exchange(ref _mutex, null)?.Dispose();
}
