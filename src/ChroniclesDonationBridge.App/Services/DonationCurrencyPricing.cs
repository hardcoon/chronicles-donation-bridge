using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.App.Services;

public sealed record ExchangeRateSnapshot(
    DateOnly EffectiveDate,
    IReadOnlyDictionary<string, decimal> RublesPerUnit,
    string SourceName);

public interface IExchangeRateProvider
{
    Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads the official daily rates published by the Bank of Russia. The feed is
/// used only when the user explicitly asks to rebuild prices; donation matching
/// itself remains strict and never converts currencies at runtime.
/// </summary>
internal sealed class CbrExchangeRateProvider : IExchangeRateProvider
{
    internal const string SourceName = "Банк России";
    internal static readonly Uri DailyRatesUri = new("https://www.cbr.ru/scripts/XML_daily.asp");
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly HttpClient SharedClient = CreateSharedClient();
    private readonly HttpClient _httpClient;

    public CbrExchangeRateProvider() : this(SharedClient)
    {
    }

    internal CbrExchangeRateProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        cancellationToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, DailyRatesUri);
        request.Headers.Accept.ParseAdd("application/xml");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("Ответ сервиса курсов валют слишком большой.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes)
                throw new InvalidDataException("Ответ сервиса курсов валют слишком большой.");
            buffer.Write(chunk, 0, count);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidDataException("Сервис курсов валют вернул пустой или слишком большой ответ.");
        }

        // Codes, dates and numeric values in the CBR feed are ASCII. Latin-1
        // keeps those bytes stable without requiring a process-wide code-page
        // provider for the Windows-1251 names that are not used here.
        return Parse(Encoding.Latin1.GetString(bytes));
    }

    internal static ExchangeRateSnapshot Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new InvalidDataException("Сервис курсов валют вернул пустой ответ.");
        }

        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumResponseBytes
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("В ответе сервиса курсов отсутствует корневой элемент.");
        if (!DateOnly.TryParseExact(
                root.Attribute("Date")?.Value,
                "dd.MM.yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var effectiveDate))
        {
            throw new InvalidDataException("В ответе сервиса курсов отсутствует корректная дата.");
        }

        var rates = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["RUB"] = 1m
        };
        foreach (var element in root.Elements("Valute"))
        {
            var code = element.Element("CharCode")?.Value.Trim().ToUpperInvariant();
            if (!Money.TryNormalizeCurrency(code, out var normalized)) continue;

            decimal rublesPerUnit;
            var unitRateText = element.Element("VunitRate")?.Value;
            if (!TryParseRate(unitRateText, out rublesPerUnit))
            {
                if (!TryParseRate(element.Element("Value")?.Value, out var value) ||
                    !decimal.TryParse(
                        element.Element("Nominal")?.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var nominal) ||
                    nominal <= 0)
                {
                    continue;
                }
                rublesPerUnit = value / nominal;
            }

            if (rublesPerUnit > 0) rates[normalized] = rublesPerUnit;
        }

        return new ExchangeRateSnapshot(effectiveDate, rates, SourceName);
    }

    private static bool TryParseRate(string? value, out decimal result) =>
        decimal.TryParse(
            (value ?? string.Empty).Trim().Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result);

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChroniclesDonationBridge/1.0");
        return client;
    }
}

internal static class DonationCurrencyPriceCalculator
{
    // DonationAlerts public API output currencies. Keep the stable order so a
    // recalculation produces deterministic chips and settings files.
    public static IReadOnlyList<string> SupportedCurrencies { get; } =
        ["RUB", "USD", "EUR", "BYN", "KZT", "UAH", "BRL", "TRY"];

    public static IReadOnlyList<TriggerRule> CalculateAll(
        TriggerRule seed,
        ExchangeRateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(snapshot);
        var baseCurrency = Money.NormalizeCurrency(seed.Currency);
        if (!SupportedCurrencies.Contains(baseCurrency, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Валюта {baseCurrency} не входит в список валют DonationAlerts.");
        }
        if (seed.Amount <= 0)
        {
            throw new InvalidOperationException("Для пересчёта укажите сумму больше нуля.");
        }
        if (!snapshot.RublesPerUnit.TryGetValue(baseCurrency, out var baseRate) || baseRate <= 0)
        {
            throw new InvalidDataException($"Источник курсов не вернул курс {baseCurrency}.");
        }

        var result = new List<TriggerRule>(SupportedCurrencies.Count);
        foreach (var currency in SupportedCurrencies)
        {
            if (!snapshot.RublesPerUnit.TryGetValue(currency, out var targetRate) || targetRate <= 0)
            {
                throw new InvalidDataException($"Источник курсов не вернул курс {currency}.");
            }

            var raw = seed.Amount * baseRate / targetRate;
            var digits = Money.MinorDigits(currency);
            var amount = decimal.Round(raw, digits, MidpointRounding.AwayFromZero);
            if (amount == 0 && raw > 0)
            {
                amount = 1m / DecimalPower(10m, digits);
            }
            if (amount > 1_000_000_000m)
            {
                throw new InvalidOperationException(
                    $"После пересчёта сумма в {currency} превышает допустимый предел.");
            }

            result.Add(new TriggerRule
            {
                Enabled = seed.Enabled,
                Comparator = seed.Comparator,
                Amount = amount,
                Currency = currency
            });
        }
        return result;
    }

    private static decimal DecimalPower(decimal value, int exponent)
    {
        var result = 1m;
        for (var index = 0; index < exponent; index++) result *= value;
        return result;
    }
}
