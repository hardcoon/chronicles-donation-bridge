using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.DonationAlerts;

public sealed class DonationAlertsRestClient
{
    private readonly HttpClient _http;
    private readonly IDonationAlertsApiRateLimiter _rateLimiter;

    public DonationAlertsRestClient(HttpClient http, IDonationAlertsApiRateLimiter rateLimiter)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public Task<OAuthTokenSet> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        CancellationToken cancellationToken = default) => RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = DonationAlertsEndpoints.RedirectUri,
                ["code"] = code
            }, cancellationToken);

    public async Task<OAuthTokenSet> RefreshTokenAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var refreshed = await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
                ["scope"] = DonationAlertsEndpoints.Scopes
            }, cancellationToken, requireRefreshToken: false).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshed.RefreshToken)) refreshed.RefreshToken = refreshToken;
        return refreshed;
    }

    public async Task<DonationAlertsProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, DonationAlertsEndpoints.Profile, accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var data = document.RootElement.TryGetProperty("data", out var wrapped) ? wrapped : document.RootElement;
        return new DonationAlertsProfile
        {
            Id = GetInt64(data, "id"),
            Code = GetString(data, "code"),
            Name = GetString(data, "name"),
            SocketConnectionToken = GetString(data, "socket_connection_token")
        };
    }

    public async Task<DonationPage> GetDonationsAsync(string accessToken, int page = 1, CancellationToken cancellationToken = default)
    {
        var uri = $"{DonationAlertsEndpoints.Donations}?page={Math.Max(1, page)}";
        using var request = Authorized(HttpMethod.Get, uri, accessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var donations = new List<DonationEvent>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (TryParseDonation(item, out var donation))
                {
                    donations.Add(donation);
                }
            }
        }

        var current = page;
        var last = page;
        if (document.RootElement.TryGetProperty("meta", out var meta))
        {
            current = (int)GetInt64(meta, "current_page", page);
            last = (int)GetInt64(meta, "last_page", current);
        }
        return new DonationPage { Donations = donations, CurrentPage = current, LastPage = last };
    }

    public async Task<string> CreateCentrifugeSubscriptionAsync(
        string accessToken,
        long userId,
        string clientConnectionId,
        CancellationToken cancellationToken = default)
    {
        var channel = $"$alerts:donation_{userId}";
        using var request = Authorized(HttpMethod.Post, DonationAlertsEndpoints.CentrifugeSubscribe, accessToken);
        request.Content = JsonContent.Create(new { channels = new[] { channel }, client = clientConnectionId });
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        return ParseCentrifugeSubscriptionToken(document.RootElement, channel);
    }

    public static string ParseCentrifugeSubscriptionToken(JsonElement root, string channel)
    {
        if (root.TryGetProperty("data", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object) root = wrapped;
        if (!root.TryGetProperty("channels", out var channels))
        {
            throw new InvalidDataException("DonationAlerts не вернул список Centrifugo-каналов.");
        }

        if (channels.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in channels.EnumerateArray())
            {
                if (GetString(item, "channel") == channel && item.TryGetProperty("token", out var arrayToken) && !string.IsNullOrWhiteSpace(arrayToken.GetString()))
                {
                    return arrayToken.GetString()!;
                }
            }
        }
        else if (channels.ValueKind == JsonValueKind.Object && channels.TryGetProperty(channel, out var subscription) &&
                 subscription.TryGetProperty("token", out var objectToken) && !string.IsNullOrWhiteSpace(objectToken.GetString()))
        {
            return objectToken.GetString()!;
        }

        throw new InvalidDataException("DonationAlerts не вернул токен требуемого Centrifugo-канала.");
    }

    internal static bool TryParseDonation(JsonElement item, out DonationEvent donation)
    {
        donation = new DonationEvent();
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
            !item.TryGetProperty("amount", out var amountElement) || !item.TryGetProperty("currency", out var currencyElement))
        {
            return false;
        }

        var amountText = amountElement.ValueKind == JsonValueKind.String ? amountElement.GetString() : amountElement.GetRawText();
        var currency = currencyElement.GetString();
        if (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || string.IsNullOrWhiteSpace(currency))
        {
            return false;
        }

        try { currency = Money.NormalizeCurrency(currency); }
        catch (ArgumentException) { return false; }

        donation = new DonationEvent
        {
            Id = id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : id.GetRawText(),
            Username = GetString(item, "username"),
            Message = GetString(item, "message"),
            Amount = amount,
            Currency = currency,
            CreatedAt = GetDateTimeOffset(item, "created_at") ?? DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        return !string.IsNullOrWhiteSpace(donation.Id);
    }

    private async Task<OAuthTokenSet> RequestTokenAsync(Dictionary<string, string> fields, CancellationToken cancellationToken, bool requireRefreshToken = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DonationAlertsEndpoints.Token)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var tokens = await response.Content.ReadFromJsonAsync<OAuthTokenSet>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("DonationAlerts вернул пустой ответ token endpoint.");
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || (requireRefreshToken && string.IsNullOrWhiteSpace(tokens.RefreshToken)))
        {
            throw new InvalidDataException("DonationAlerts не вернул обязательные OAuth-токены.");
        }
        tokens.ObtainedAt = DateTimeOffset.UtcNow;
        return tokens;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        // Response bodies from OAuth endpoints can contain credentials. Never propagate them to UI/log exceptions.
        _ = cancellationToken;
        return Task.FromException(new HttpRequestException($"DonationAlerts API вернул {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode));
    }

    internal static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    internal static long GetInt64(JsonElement element, string name, long fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : fallback;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix)) return DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
    }
}
