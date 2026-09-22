using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ChroniclesDonationBridge.DonationAlerts;

public sealed class LoopbackOAuthFlow
{
    private readonly DonationAlertsRestClient _client;

    public LoopbackOAuthFlow(DonationAlertsRestClient client) => _client = client;

    public async Task<OAuthAuthorizationResult> AuthorizeAsync(
        string clientId,
        string clientSecret,
        Action<Uri> openBrowser,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Введите Client ID.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(clientSecret)) throw new ArgumentException("Введите Client Secret.", nameof(clientSecret));
        ArgumentNullException.ThrowIfNull(openBrowser);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 17871);
        listener.Start(1);
        try
        {
            var authorizationUri = BuildAuthorizationUri(clientId, state);
            openBrowser(authorizationUri);
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            if (socket.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
            {
                throw new InvalidDataException("OAuth callback принят не с loopback-интерфейса.");
            }

            await using var stream = socket.GetStream();
            var requestTarget = await ReadRequestTargetAsync(stream, timeout.Token).ConfigureAwait(false);
            var callback = new Uri(new Uri(DonationAlertsEndpoints.RedirectUri), requestTarget);
            if (!string.Equals(callback.AbsolutePath, "/donationalerts/callback/", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, false, "Неверный адрес callback.", timeout.Token).ConfigureAwait(false);
                throw new InvalidDataException("Получен callback на неверный путь.");
            }

            var query = ParseQuery(callback.Query);
            if (!query.TryGetValue("state", out var returnedState) || !FixedTimeEquals(state, returnedState))
            {
                await WriteResponseAsync(stream, false, "Проверка state не пройдена.", timeout.Token).ConfigureAwait(false);
                throw new InvalidDataException("OAuth state не совпал. Авторизация отменена.");
            }

            if (query.TryGetValue("error", out var error))
            {
                await WriteResponseAsync(stream, false, "DonationAlerts отклонил авторизацию.", timeout.Token).ConfigureAwait(false);
                throw new InvalidOperationException($"DonationAlerts OAuth: {error}");
            }

            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                await WriteResponseAsync(stream, false, "Код авторизации не получен.", timeout.Token).ConfigureAwait(false);
                throw new InvalidDataException("DonationAlerts не вернул code.");
            }

            try
            {
                var tokens = await _client.ExchangeAuthorizationCodeAsync(clientId, clientSecret, code, timeout.Token).ConfigureAwait(false);
                var profile = await _client.GetProfileAsync(tokens.AccessToken, timeout.Token).ConfigureAwait(false);
                await WriteResponseAsync(stream, true, "Аккаунт подключён. Эту вкладку можно закрыть.", timeout.Token).ConfigureAwait(false);
                return new OAuthAuthorizationResult(tokens, profile);
            }
            catch
            {
                await WriteResponseAsync(stream, false, "Не удалось завершить подключение. Вернитесь в приложение.", timeout.Token).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public static void OpenSystemBrowser(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static Uri BuildAuthorizationUri(string clientId, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["redirect_uri"] = DonationAlertsEndpoints.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = DonationAlertsEndpoints.Scopes,
            ["state"] = state
        };
        return new Uri(DonationAlertsEndpoints.Authorization + "?" + string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
    }

    private static async Task<string> ReadRequestTargetAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        var firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Пустой OAuth callback.");
        if (firstLine.Length > 8192)
        {
            throw new InvalidDataException("Слишком длинный OAuth callback.");
        }

        var parts = firstLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Ожидался HTTP GET callback.");
        }

        string? line;
        var headerBytes = firstLine.Length;
        do
        {
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            headerBytes += line?.Length ?? 0;
            if (headerBytes > 16 * 1024)
            {
                throw new InvalidDataException("Слишком большие HTTP-заголовки OAuth callback.");
            }
        } while (!string.IsNullOrEmpty(line));

        return parts[1];
    }

    private static async Task WriteResponseAsync(Stream stream, bool success, string message, CancellationToken cancellationToken)
    {
        var color = success ? "#53d18b" : "#ff6b6b";
        var safe = WebUtility.HtmlEncode(message);
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>Chronicles Donation Bridge</title><body style=\"background:#10161d;color:#f0f3f6;font:18px Segoe UI;padding:48px\"><h1 style=\"color:{color}\">Chronicles Donation Bridge</h1><p>{safe}</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            result[DecodeQuery(key)] = DecodeQuery(value);
        }
        return result;
    }

    private static string DecodeQuery(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
