using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using ChroniclesDonationBridge.App.Services;
using ChroniclesDonationBridge.App.ViewModels;
using ChroniclesDonationBridge.Core;
using ChroniclesDonationBridge.Persistence;

namespace ChroniclesDonationBridge.Tests;

internal static class UiParityTests
{
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static ActionViewModel Preset(string id) => new(DefaultActionCatalog.CreatePresets().Single(a => a.HandlerId == id), true);
    private static ExchangeRateSnapshot Rates() => new(new DateOnly(2026, 9, 5), new Dictionary<string, decimal>
        { ["RUB"] = 1, ["USD"] = 80, ["EUR"] = 100, ["BYN"] = 25, ["KZT"] = 0.2m, ["UAH"] = 2, ["BRL"] = 16, ["TRY"] = 2.5m }, "Test feed");

    public static Task CategoriesAsync() => OnSta(() =>
    {
        var all = new ObservableCollection<ActionViewModel>(DefaultActionCatalog.CreatePresets().Select(a => new ActionViewModel(a, true)));
        var active = new ActionBrowserViewModel(all);
        var presets = new ActionBrowserViewModel(all);
        Check(active.VisibleActions.Cast<ActionViewModel>().Count() == 23, "All must retain all 23 handlers");
        active.SelectCategory("npc_mutants");
        Check(active.VisibleActions.Cast<ActionViewModel>().Select(a => a.HandlerId).ToHashSet().SetEquals(["spawn_npcs", "spawn_mutants", "spawn_random_hostile_squad", "spawn_random_mutant_pack"]), "NPC/mutant category is incomplete");
        presets.SelectCategory("world");
        Check(presets.VisibleActions.Cast<ActionViewModel>().Select(a => a.HandlerId).ToHashSet().SetEquals(["spawn_anomaly", "trigger_emission", "spawn_vehicle", "explode_nearest_vehicle", "random_active", "random_all_presets"]), "Wrong world category");
        Check(active.VisibleActions.Cast<ActionViewModel>().Count() == 4, "Active and Presets filters interfere");
        active.SelectCategory("player_state");
        Check(active.VisibleActions.Cast<ActionViewModel>().Count() == 7 &&
            ActionBrowserViewModel.CategoryFor("drop_outfit") == "inventory" &&
            ActionBrowserViewModel.CategoryFor("god_mode") == "player_state", "Player-state or inventory category is wrong");
        Check(ActionBrowserViewModel.CategoryFor("random_all_presets") == "world" && ActionBrowserViewModel.CategoryFor("spawn_random_mutant_pack") == "npc_mutants" && ActionBrowserViewModel.CategoryFor("extension_future") == "all", "Moved or unknown category fallback is wrong");
        active.AutomaticColumns = false;
        active.Columns = 9;
        Check(active.EffectiveColumns == 4, "Grid must clamp to four");
        active.AutomaticColumns = true;
        Check(active.EffectiveColumns == 0, "Auto mode must use available width");
    });

    public static Task ViewPreferencesAsync()
    {
        var settings = SettingsNormalizer.Normalize(new AppSettings { ActiveView = new() { Category = "world", Columns = 3, AutomaticColumns = false }, PresetView = new() { Category = "bad", Columns = 9 } });
        var snapshot = SettingsNormalizer.CreatePersistenceSnapshot(settings);
        snapshot.ActiveView.Category = "inventory";
        Check(settings.ActiveView.Category == "world", "Settings snapshot shared view preferences");
        var reloaded = SettingsNormalizer.Normalize(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)));
        Check(reloaded.ActiveView.Columns == 3 && !reloaded.ActiveView.AutomaticColumns && reloaded.PresetView.Category == "all" && reloaded.PresetView.Columns == 4, "View preferences lost or unvalidated");
        Check(SettingsNormalizer.Normalize(JsonSerializer.Deserialize<AppSettings>("{}" )).ActiveView.AutomaticColumns, "Legacy settings must default to automatic grid");
        return Task.CompletedTask;
    }

    public static async Task PopupDraftAsync()
    {
        var source = Preset("spawn_mutants");
        source.AddTrigger(new TriggerRule { Amount = 10 });
        var count = source.Model.Parameters["count"];
        var saves = 0;
        var editor = new ActionEditorViewModel(source, draft => { saves++; source.ApplyEditorDraft(draft); return Task.CompletedTask; });
        editor.Draft.SpawnGroupRows.Single().CountText = "7";
        Check(source.Model.Parameters["count"] == count, "Typing in popup changed live action before Save");
        editor.Draft.CooldownText = "";
        Check(!await editor.SaveAsync() && saves == 0, "Invalid popup draft reached Save");
        editor.Draft.CooldownText = "31";
        source.AddTrigger(new TriggerRule { Amount = 20, Currency = "EUR" });
        Check(await editor.SaveAsync() && saves == 1 && source.Model.Parameters["count"] == "7" && source.CooldownSeconds == 31, "Popup Save lost changes");
        Check(source.Triggers.Count == 2, "Popup overwrote concurrently edited prices");
        var cancelled = source.CreateEditorDraft();
        cancelled.SpawnGroupRows.Single().CountText = "2";
        Check(source.Model.Parameters["count"] == "7", "Cancelled draft leaked into source");
        var multi = Preset("random_all_presets");
        var multiDraft = multi.CreateEditorDraft();
        foreach (var option in multiDraft.Parameters.Single(p => p.IsMultiChoice).MultiChoiceOptions.Skip(2)) option.IsSelected = false;
        Check(multiDraft.TryCommitPendingEdits(out _), "Multi-choice draft invalid");
        multi.ApplyEditorDraft(multiDraft);
        Check(multi.Parameters.Single(p => p.IsMultiChoice).MultiChoiceOptions.Count(p => p.IsSelected) == 2, "Popup lost checkbox selections");
    }

    public static Task PriceMathAsync()
    {
        var seed = new TriggerRule { Amount = 100, Currency = "RUB", Comparator = TriggerComparator.GreaterOrEqual };
        var prices = DonationCurrencyPriceCalculator.CalculateAll(seed, Rates());
        Check(prices.Count == 8 && prices.Select(p => p.Id).Distinct().Count() == 8, "Generated price IDs must be distinct");
        Check(prices.Single(p => p.Currency == "USD").Amount == 1.25m && prices.Single(p => p.Currency == "KZT").Amount == 500m, "Wrong conversion direction or nominal");
        Check(prices.All(p => p.Comparator == TriggerComparator.GreaterOrEqual), "Conversion changed comparator");
        seed.Currency = "USD"; seed.Amount = 1; seed.Comparator = TriggerComparator.Exact;
        prices = DonationCurrencyPriceCalculator.CalculateAll(seed, Rates());
        Check(prices.Single(p => p.Currency == "RUB").Amount == 80 && prices.All(p => p.Comparator == TriggerComparator.Exact), "Non-RUB seed lost comparator or value");
        seed.Currency = "RUB"; seed.Amount = 0.01m;
        Check(DonationCurrencyPriceCalculator.CalculateAll(seed, Rates()).All(p => p.Amount >= 0.01m), "Small prices rounded to zero");
        var action = Preset("change_health");
        action.ReplaceTriggers(prices);
        Check(action.PriceItems.Count() == 9 && ReferenceEquals(action.PriceItems.Last(), action), "Add-price button must wrap after every price");
        action.ReplaceTriggers([new TriggerRule { Amount = 7 }]);
        Check(action.Triggers.Count == 1 && action.Model.Triggers.Count == 1, "Recalculation appended stale prices");
        return Task.CompletedTask;
    }

    public static Task ParseRatesAsync()
    {
        var xml = "<ValCurs Date='05.09.2026'><Valute><CharCode>USD</CharCode><Nominal>1</Nominal><Value>80,00</Value></Valute><Valute><CharCode>KZT</CharCode><Nominal>100</Nominal><Value>20,00</Value></Valute><Valute><CharCode>EUR</CharCode><VunitRate>100,5</VunitRate></Valute></ValCurs>";
        var parsed = CbrExchangeRateProvider.Parse(xml);
        Check(parsed.RublesPerUnit["RUB"] == 1 && parsed.RublesPerUnit["USD"] == 80 && parsed.RublesPerUnit["KZT"] == .2m && parsed.RublesPerUnit["EUR"] == 100.5m, "CBR parser lost decimal or nominal");
        try { CbrExchangeRateProvider.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///never-read'>]><ValCurs Date='05.09.2026'>&e;</ValCurs>"); throw new Exception("DTD accepted"); } catch (XmlException) { }
        try { DonationCurrencyPriceCalculator.CalculateAll(new TriggerRule { Amount = 100 }, parsed); throw new Exception("Missing rates accepted"); } catch (InvalidDataException) { }
        return Task.CompletedTask;
    }

    public static async Task PricePreviewAsync()
    {
        var calls = 0;
        var model = new TriggerEditorViewModel(null, ["RUB"], new Provider(() => { calls++; return Task.FromResult(Rates()); }));
        Check(calls == 0, "Opening editor must not request rates");
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            model.AmountText = "1,25";
            Check(model.Build()?.SingleRule.Amount == 1.25m, "Price comma interpreted as thousands separator");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        model.AmountText = "100";
        await model.CalculateAllAsync();
        Check(calls == 1 && model.HasCalculatedPrices && model.Build() is { ReplaceAllPrices: true, Rules.Count: 8 }, "Calculation did not produce an explicit preview");
        model.AmountText = "200";
        Check(!model.HasCalculatedPrices && model.Build() is { ReplaceAllPrices: false }, "Changed amount reused stale conversion");
        var gate = new TaskCompletionSource<ExchangeRateSnapshot>();
        model = new TriggerEditorViewModel(null, ["RUB"], new Provider(() => gate.Task));
        var pending = model.CalculateAllAsync();
        Check(model.IsCalculating && model.Build() is null, "Saved a half-loaded conversion");
        model.Currency = "EUR";
        gate.SetResult(Rates());
        await pending;
        Check(!model.HasCalculatedPrices && model.Error.Length > 0, "Racing currency edit reused stale prices");
        model = new TriggerEditorViewModel(null, [], new Provider(() => throw new HttpRequestException("offline")));
        await model.CalculateAllAsync();
        Check(!model.HasCalculatedPrices && model.Error.Contains("offline") && model.Build() is { ReplaceAllPrices: false }, "Offline failure prevented normal single-price editing");
        model = new TriggerEditorViewModel(null, [], new Provider(() => throw new OperationCanceledException("feed timeout")));
        await model.CalculateAllAsync();
        Check(!model.IsCalculating && model.Error.Length > 0, "Internal feed timeout escaped the editor");
    }

    public static async Task RateHttpAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Check(request.RequestUri == CbrExchangeRateProvider.DailyRatesUri && request.Headers.Authorization is null, "Rates request leaked authorization or used wrong host");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<ValCurs Date='05.09.2026' />", Encoding.UTF8, "application/xml") };
        }));
        await new CbrExchangeRateProvider(http).GetLatestAsync();
        Check(calls == 1, "One click must make one rates request");
        using var large = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1_048_577)) }));
        try { await new CbrExchangeRateProvider(large).GetLatestAsync(); throw new Exception("Oversized feed accepted"); } catch (InvalidDataException) { }
    }

    private sealed class Provider(Func<Task<ExchangeRateSnapshot>> load) : IExchangeRateProvider
    {
        public Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken = default) => load();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private static Task OnSta(Action test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { test(); done.SetResult(); } catch (Exception e) { done.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
