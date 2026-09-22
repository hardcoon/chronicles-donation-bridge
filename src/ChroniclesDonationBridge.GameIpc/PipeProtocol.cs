using System.Globalization;
using System.Text;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.GameIpc;

public abstract record InboundMessage;
public sealed record HelloMessage(string AddonVersion, int GameProcessId, string SessionId = "", string ProtocolVersion = "1") : InboundMessage;
public sealed record StatusMessage(bool Ready, string Reason) : InboundMessage;
public sealed record ResultMessage(GameExecutionResult Result) : InboundMessage;
public sealed record PongMessage : InboundMessage;

public static class PipeProtocol
{
    public const string PipeName = "ChroniclesDonationBridge.v1";
    public const string ProtocolVersion = "2";
    public const int MaximumLineBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Welcome(string applicationVersion, string protocolVersion = ProtocolVersion) =>
        $"WELCOME\t{protocolVersion}\t{PercentEncode(applicationVersion)}";
    public static string Ping() => "PING";
    public static string ResultQuery(string commandId, string sessionId) =>
        $"RESULT_QUERY\t{PercentEncode(commandId)}\t{PercentEncode(sessionId)}";

    public static string Command(GameCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var structuredParameters = string.Join(";", command.Parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{PercentEncode(ValidateParameterKey(pair.Key))}={PercentEncode(pair.Value)}"));

        var line = string.Join('\t',
            "COMMAND",
            PercentEncode(command.CommandId),
            PercentEncode(command.DonationId),
            PercentEncode(command.ActionId),
            command.AmountMinor.ToString(CultureInfo.InvariantCulture),
            PercentEncode(Money.NormalizeCurrency(command.Currency)),
            PercentEncode(structuredParameters));
        if (StrictUtf8.GetByteCount(line) + 1 > MaximumLineBytes)
        {
            throw new InvalidDataException("Исходящая строка протокола превышает 16 КиБ.");
        }
        return line;
    }

    public static InboundMessage ParseInbound(string line)
    {
        if (StrictUtf8.GetByteCount(line) + 1 > MaximumLineBytes)
        {
            throw new InvalidDataException("Строка протокола превышает 16 КиБ.");
        }

        var fields = line.Split('\t');
        if (fields.Length == 0)
        {
            throw new InvalidDataException("Получена пустая команда протокола.");
        }

        return fields[0] switch
        {
            "HELLO" => ParseHello(fields),
            "STATUS" => ParseStatus(fields),
            "RESULT" => ParseResult(fields),
            "PONG" when fields.Length == 1 => new PongMessage(),
            _ => throw new InvalidDataException($"Неизвестная или повреждённая команда протокола: {fields[0]}")
        };
    }

    public static IReadOnlyDictionary<string, string> ParseStructuredParameters(string outerDecodedValue)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(outerDecodedValue))
        {
            return result;
        }

        foreach (var pair in outerDecodedValue.Split(';'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidDataException("Повреждённое поле параметров команды.");
            }

            var key = ValidateParameterKey(PercentDecode(pair[..separator]));
            if (!result.TryAdd(key, PercentDecode(pair[(separator + 1)..])))
            {
                throw new InvalidDataException($"Параметр {key} передан повторно.");
            }
        }

        return result;
    }

    public static string PercentEncode(string? value)
    {
        var bytes = StrictUtf8.GetBytes(value ?? string.Empty);
        var builder = new StringBuilder(bytes.Length);
        foreach (var valueByte in bytes)
        {
            if ((valueByte >= (byte)'a' && valueByte <= (byte)'z') ||
                (valueByte >= (byte)'A' && valueByte <= (byte)'Z') ||
                (valueByte >= (byte)'0' && valueByte <= (byte)'9') ||
                valueByte is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                builder.Append((char)valueByte);
            }
            else
            {
                builder.Append('%');
                builder.Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    public static string PercentDecode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream(value.Length);
        for (var index = 0; index < value.Length;)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length ||
                    !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var decoded))
                {
                    throw new InvalidDataException("Повреждённая percent-последовательность.");
                }

                stream.WriteByte(decoded);
                index += 3;
                continue;
            }

            if (character > 0x7F)
            {
                throw new InvalidDataException("Неэкранированный не-ASCII символ в протоколе.");
            }

            stream.WriteByte((byte)character);
            index++;
        }

        try
        {
            return StrictUtf8.GetString(stream.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Повреждённая UTF-8 последовательность.", exception);
        }
    }

    private static HelloMessage ParseHello(string[] fields)
    {
        if (!((fields.Length == 5 && fields[1] == ProtocolVersion) || (fields.Length == 4 && fields[1] == "1")) ||
            !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0)
        {
            throw new InvalidDataException("Некорректное сообщение HELLO или несовместимая версия протокола.");
        }

        var sessionId = fields.Length == 5 ? PercentDecode(fields[4]) : string.Empty;
        if (fields.Length == 5 && (sessionId.Length is < 1 or > 128 || sessionId.Any(char.IsControl)))
            throw new InvalidDataException("Некорректный идентификатор игровой сессии.");
        return new HelloMessage(PercentDecode(fields[2]), processId, sessionId, fields[1]);
    }

    private static StatusMessage ParseStatus(string[] fields)
    {
        if (fields.Length != 3 || fields[1] is not ("ready" or "waiting"))
        {
            throw new InvalidDataException("Некорректное сообщение STATUS.");
        }

        return new StatusMessage(fields[1] == "ready", PercentDecode(fields[2]));
    }

    private static ResultMessage ParseResult(string[] fields)
    {
        if (fields.Length != 4)
        {
            throw new InvalidDataException("Некорректное сообщение RESULT.");
        }

        var status = fields[2] switch
        {
            "executed" => GameResultStatus.Executed,
            "deferred" => GameResultStatus.Deferred,
            "rejected" => GameResultStatus.Rejected,
            "uncertain" => GameResultStatus.Uncertain,
            _ => throw new InvalidDataException("Неизвестный статус RESULT.")
        };
        return new ResultMessage(new GameExecutionResult
        {
            CommandId = PercentDecode(fields[1]),
            Status = status,
            Reason = PercentDecode(fields[3]),
            ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    private static string ValidateParameterKey(string key)
    {
        if (!Validation.ParameterKey().IsMatch(key))
        {
            throw new InvalidDataException($"Недопустимый ключ параметра: {key}");
        }

        return key;
    }
}
