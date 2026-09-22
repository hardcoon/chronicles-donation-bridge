using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.DonationAlerts;

public sealed class DonationAlertsWebSocket : IAsyncDisposable
{
    private readonly DonationAlertsRestClient _rest;
    private readonly Func<CancellationToken, Task<string>> _accessTokenProvider;
    private readonly DonationAlertsProfile _profile;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _runTask;

    public DonationAlertsWebSocket(
        DonationAlertsRestClient rest,
        Func<CancellationToken, Task<string>> accessTokenProvider,
        DonationAlertsProfile profile)
    {
        _rest = rest;
        _accessTokenProvider = accessTokenProvider;
        _profile = profile;
    }

    public event Func<DonationEvent, Task>? DonationReceived;
    public event Action<DonationConnectionState>? ConnectionChanged;

    public void Start()
    {
        _runTask ??= Task.Run(() => ReconnectLoopAsync(_lifetime.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ConnectionChanged?.Invoke(new DonationConnectionState(false, "Подключение к DonationAlerts…"));
                await RunSessionAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException or InvalidDataException or IOException)
            {
                attempt++;
                var delaySeconds = Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))) + Random.Shared.NextDouble();
                ConnectionChanged?.Invoke(new DonationConnectionState(false, $"Переподключение через {Math.Ceiling(delaySeconds)} с: {SafeMessage(exception)}"));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        ConnectionChanged?.Invoke(new DonationConnectionState(false, "Отключено"));
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(new Uri(DonationAlertsEndpoints.CentrifugeSocket), cancellationToken).ConfigureAwait(false);

        await SendJsonAsync(socket, new { @params = new { token = _profile.SocketConnectionToken }, id = 1 }, cancellationToken).ConfigureAwait(false);
        var clientId = string.Empty;
        for (var attempt = 0; attempt < 10 && string.IsNullOrWhiteSpace(clientId); attempt++)
        {
            var connectReply = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (connectReply == "{}")
            {
                await SendRawAsync(socket, "{}", cancellationToken).ConfigureAwait(false);
                continue;
            }
            using var connectedDocument = JsonDocument.Parse(connectReply);
            clientId = FindStringProperty(connectedDocument.RootElement, "client");
        }
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidDataException("Centrifugo не вернул client id.");
        }

        var accessToken = await _accessTokenProvider(cancellationToken).ConfigureAwait(false);
        var subscriptionToken = await _rest.CreateCentrifugeSubscriptionAsync(accessToken, _profile.Id, clientId, cancellationToken).ConfigureAwait(false);
        var channel = $"$alerts:donation_{_profile.Id}";
        await SendJsonAsync(socket, new { @params = new { channel, token = subscriptionToken }, method = 1, id = 2 }, cancellationToken).ConfigureAwait(false);
        var subscribed = false;
        for (var attempt = 0; attempt < 10 && !subscribed; attempt++)
        {
            var subscribeReply = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (subscribeReply == "{}")
            {
                await SendRawAsync(socket, "{}", cancellationToken).ConfigureAwait(false);
                continue;
            }
            using var ackDocument = JsonDocument.Parse(subscribeReply);
            subscribed = IsSubscriptionAck(ackDocument.RootElement);
            if (!subscribed && ackDocument.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidDataException("Centrifugo отклонил подписку: " + error.GetRawText());
            }
            foreach (var earlyDonation in FindDonations(ackDocument.RootElement))
            {
                if (DonationReceived is { } earlyHandler) await earlyHandler(earlyDonation).ConfigureAwait(false);
            }
        }
        if (!subscribed) throw new InvalidDataException("Centrifugo не подтвердил подписку на donation channel.");
        ConnectionChanged?.Invoke(new DonationConnectionState(true, $"Подключён аккаунт {_profile.Code}"));

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (text == "{}")
            {
                await SendRawAsync(socket, "{}", cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var document = JsonDocument.Parse(text);
            foreach (var donation in FindDonations(document.RootElement))
            {
                if (DonationReceived is { } handler)
                {
                    await handler(donation).ConfigureAwait(false);
                }
            }
        }

        throw new WebSocketException("Centrifugo закрыл соединение.");
    }

    private static IEnumerable<DonationEvent> FindDonations(JsonElement element)
    {
        if (DonationAlertsRestClient.TryParseDonation(element, out var donation))
        {
            yield return donation;
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in FindDonations(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                foreach (var nested in FindDonations(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string FindStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindStringProperty(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return string.Empty;
    }

    private static bool IsSubscriptionAck(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("error", out _)) return false;
        return root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value == 2;
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
        => await SendRawAsync(socket, JsonSerializer.Serialize(payload), cancellationToken).ConfigureAwait(false);

    private static async Task SendRawAsync(ClientWebSocket socket, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        const int maximum = 1024 * 1024;
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Centrifugo отправил Close.");
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Centrifugo прислал неподдерживаемое бинарное сообщение.");
            }
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > maximum)
            {
                throw new InvalidDataException("Сообщение Centrifugo превышает 1 МиБ.");
            }
            if (result.EndOfMessage) break;
        }

        return new UTF8Encoding(false, true).GetString(stream.ToArray());
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length > 180 ? message[..180] : message;
    }
}
