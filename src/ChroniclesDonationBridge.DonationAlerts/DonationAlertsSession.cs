using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.DonationAlerts;

public sealed class DonationAlertsSession : IAsyncDisposable
{
    private readonly DonationAlertsRestClient _rest;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Func<OAuthTokenSet> _readTokens;
    private readonly Func<OAuthTokenSet, CancellationToken, Task> _saveTokens;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DonationAlertsWebSocket? _socket;

    public DonationAlertsSession(
        DonationAlertsRestClient rest,
        string clientId,
        string clientSecret,
        Func<OAuthTokenSet> readTokens,
        Func<OAuthTokenSet, CancellationToken, Task> saveTokens)
    {
        _rest = rest;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _readTokens = readTokens;
        _saveTokens = saveTokens;
    }

    public event Func<DonationEvent, Task>? DonationReceived;
    public event Action<DonationConnectionState>? ConnectionChanged;

    public async Task StartAsync(DonationAlertsProfile profile, CancellationToken cancellationToken = default)
    {
        await DisposeSocketAsync().ConfigureAwait(false);
        _socket = new DonationAlertsWebSocket(_rest, GetValidAccessTokenAsync, profile);
        _socket.ConnectionChanged += state => ConnectionChanged?.Invoke(state);
        _socket.DonationReceived += donation => DonationReceived?.Invoke(donation) ?? Task.CompletedTask;
        _socket.Start();
        await Task.CompletedTask;
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var current = _readTokens();
        if (!string.IsNullOrWhiteSpace(current.AccessToken) && current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return current.AccessToken;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _readTokens();
            if (!string.IsNullOrWhiteSpace(current.AccessToken) && current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return current.AccessToken;
            }
            if (string.IsNullOrWhiteSpace(current.RefreshToken))
            {
                throw new InvalidOperationException("Отсутствует refresh token. Подключите аккаунт заново.");
            }

            var refreshed = await _rest.RefreshTokenAsync(_clientId, _clientSecret, current.RefreshToken, cancellationToken).ConfigureAwait(false);
            await _saveTokens(refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<IReadOnlyList<DonationEvent>> RecoverGapAsync(
        RecoveryCheckpoint checkpoint,
        QueuePolicy queuePolicy,
        CancellationToken cancellationToken = default)
    {
        if (!checkpoint.Initialized || queuePolicy.Mode == QueueMode.Skip)
        {
            return [];
        }

        var token = await GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var indexed = new List<DonationEvent>();
        var lifetime = queuePolicy.GetLifetime();
        var minimumCreatedAt = lifetime is null ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow - lifetime.Value;
        for (var pageNumber = 1; pageNumber <= 5; pageNumber++)
        {
            var page = await _rest.GetDonationsAsync(token, pageNumber, cancellationToken).ConfigureAwait(false);
            indexed.AddRange(page.Donations);
            if (pageNumber >= page.LastPage ||
                (lifetime is not null && page.Donations.Any(item => item.CreatedAt < minimumCreatedAt)))
            {
                break;
            }
        }

        return DonationRecoveryPlanner.Select(checkpoint, indexed, queuePolicy, DateTimeOffset.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSocketAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
    }

    private async Task DisposeSocketAsync()
    {
        if (_socket is not null)
        {
            await _socket.DisposeAsync().ConfigureAwait(false);
            _socket = null;
        }
    }
}
