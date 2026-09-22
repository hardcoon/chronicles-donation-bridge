namespace ChroniclesDonationBridge.DonationAlerts;

public interface IDonationAlertsApiRateLimiter
{
    ValueTask WaitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Limits only outbound DonationAlerts HTTP API calls. The Centrifugo
/// WebSocket receive loop does not use this limiter.
/// </summary>
public sealed class DonationAlertsApiRateLimiter : IDonationAlertsApiRateLimiter, IDisposable
{
    public const int DefaultMaximumRequests = 60;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromSeconds(1);

    private readonly Func<bool> _isEnabled;
    private readonly int _maximumRequests;
    private readonly TimeSpan _window;
    private readonly TimeSpan _minimumInterval;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _requestStarts = [];
    private DateTimeOffset? _lastRequestStart;

    public DonationAlertsApiRateLimiter(Func<bool> isEnabled)
        : this(
            isEnabled,
            DefaultMaximumRequests,
            DefaultWindow,
            DefaultMinimumInterval,
            () => DateTimeOffset.UtcNow,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    public DonationAlertsApiRateLimiter(
        Func<bool> isEnabled,
        int maximumRequests,
        TimeSpan window,
        TimeSpan minimumInterval,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(delay);
        if (maximumRequests <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        if (minimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));

        _isEnabled = isEnabled;
        _maximumRequests = maximumRequests;
        _window = window;
        _minimumInterval = minimumInterval;
        _utcNow = utcNow;
        _delay = delay;
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        if (!_isEnabled()) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_isEnabled())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _utcNow();
                while (_requestStarts.TryPeek(out var oldest) && now - oldest >= _window)
                {
                    _requestStarts.Dequeue();
                }

                var allowedAt = now;
                if (_lastRequestStart is { } last)
                {
                    var intervalEnd = last + _minimumInterval;
                    if (intervalEnd > allowedAt) allowedAt = intervalEnd;
                }
                if (_requestStarts.Count >= _maximumRequests)
                {
                    var windowEnd = _requestStarts.Peek() + _window;
                    if (windowEnd > allowedAt) allowedAt = windowEnd;
                }

                var wait = allowedAt - now;
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                now = _utcNow();
                _requestStarts.Enqueue(now);
                _lastRequestStart = now;
                return;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
