using System.Text.Json.Serialization;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.DonationAlerts;

public static class DonationAlertsEndpoints
{
    public const string Authorization = "https://www.donationalerts.com/oauth/authorize";
    public const string Token = "https://www.donationalerts.com/oauth/token";
    public const string Profile = "https://www.donationalerts.com/api/v1/user/oauth";
    public const string Donations = "https://www.donationalerts.com/api/v1/alerts/donations";
    public const string CentrifugeSubscribe = "https://www.donationalerts.com/api/v1/centrifuge/subscribe";
    public const string CentrifugeSocket = "wss://centrifugo.donationalerts.com/connection/websocket";
    public const string Applications = "https://www.donationalerts.com/application/clients";
    public const string ApiDocumentation = "https://www.donationalerts.com/apidoc";
    public const string RedirectUri = "http://127.0.0.1:17871/donationalerts/callback/";
    public const string Scopes = "oauth-user-show oauth-donation-subscribe oauth-donation-index";
}

public sealed class OAuthTokenSet
{
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresInSeconds { get; set; }

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    public DateTimeOffset ObtainedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt => ObtainedAt.AddSeconds(Math.Max(0, ExpiresInSeconds));
}

public sealed class DonationAlertsProfile
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SocketConnectionToken { get; set; } = string.Empty;
}

public sealed record OAuthAuthorizationResult(OAuthTokenSet Tokens, DonationAlertsProfile Profile);

public sealed class DonationPage
{
    public List<DonationEvent> Donations { get; init; } = [];
    public int CurrentPage { get; init; } = 1;
    public int LastPage { get; init; } = 1;
}

public sealed record DonationConnectionState(bool Connected, string Message);

public sealed class RecoveryCheckpoint
{
    public bool Initialized { get; set; }
    public string LastDonationId { get; set; } = string.Empty;
    public DateTimeOffset? LastCreatedAt { get; set; }
}

public static class DonationRecoveryPlanner
{
    public static IReadOnlyList<DonationEvent> Select(
        RecoveryCheckpoint checkpoint,
        IEnumerable<DonationEvent> indexed,
        QueuePolicy queuePolicy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(indexed);
        ArgumentNullException.ThrowIfNull(queuePolicy);

        if (!checkpoint.Initialized || queuePolicy.Mode == QueueMode.Skip)
        {
            return [];
        }

        var lifetime = queuePolicy.GetLifetime();
        var minimumCreatedAt = lifetime is null ? DateTimeOffset.MinValue : now - lifetime.Value;
        return indexed
            .Where(item => item.CreatedAt >= minimumCreatedAt)
            // The checkpoint is only a first-run marker, not a reliable lower bound. An older
            // donation can still be queued while a newer terminal event has advanced it. The
            // persistent processed ledger performs the authoritative terminal/deduplication check.
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
    }
}
