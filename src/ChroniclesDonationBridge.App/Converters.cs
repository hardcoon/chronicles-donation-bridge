using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ChroniclesDonationBridge.Core;

namespace ChroniclesDonationBridge.App;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var inverted = value is not true;
        return targetType == typeof(Visibility) ? (inverted ? Visibility.Visible : Visibility.Collapsed) : inverted;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool boolean && !boolean;
}

public sealed class ResponsiveItemWidthConverter : IValueConverter
{
    public double MinimumColumnWidth { get; set; } = 750;

    public double CalculateItemWidth(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return double.NaN;

        var columns = availableWidth >= MinimumColumnWidth * 2 ? 2 : 1;
        return Math.Floor(availableWidth / columns);
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width ? CalculateItemWidth(width) : double.NaN;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class FriendlyValueConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["weak"] = "слабый",
        ["medium"] = "средний",
        ["strong"] = "сильный",
        ["dog"] = "собака",
        ["boar"] = "кабан",
        ["flesh"] = "плоть",
        ["tushkano"] = "тушкан",
        ["pseudodog"] = "псевдособака",
        ["psy_dog"] = "пси-собака",
        ["bloodsucker"] = "кровосос",
        ["snork"] = "снорк",
        ["burer"] = "бюрер",
        ["controller"] = "контролёр",
        ["poltergeist"] = "полтергейст",
        ["chimera"] = "химера",
        ["pseudogiant"] = "псевдогигант",
        ["gravitational"] = "гравитационная",
        ["electric"] = "электрическая",
        ["thermal"] = "термическая",
        ["acidic"] = "кислотная",
        ["stalker"] = "сталкеры",
        ["bandit"] = "бандиты",
        ["duty"] = "Долг",
        ["freedom"] = "Свобода",
        ["military"] = "военные",
        ["monolith"] = "Монолит",
        ["killer"] = "наёмники",
        ["outfit"] = "броню",
        ["helmet"] = "шлем",
        ["both"] = "броню и шлем",
        ["bandage"] = "бинт",
        ["medkit"] = "аптечка",
        ["antirad"] = "антирад",
        ["bread"] = "хлеб",
        ["conserva"] = "консервы",
        ["vodka"] = "водка",
        ["ammo_9x18_fmj"] = "патроны 9×18 FMJ",
        ["ammo_5.45x39_fmj"] = "патроны 5,45×39 FMJ",
        ["ammo_12x70_buck"] = "картечь 12×70",
        ["weapons"] = "Оружие",
        ["ammo_explosives"] = "Патроны",
        ["armor"] = "Броня и шлемы",
        ["weapon_addons"] = "Оружейные дополнения",
        ["electronics"] = "Электроника и приборы",
        ["artefacts"] = "Артефакты",
        ["medicine"] = "Медицина",
        ["food"] = "Еда и напитки",
        ["repair_tools"] = "Ремонт и инструменты",
        ["fuel"] = "Топливо",
        ["pf_vehicle_fuel_canister"] = "Канистра с бензином (20 л)",
        ["veh_dcp_niva"] = "Нива 2121 — белая ржавая",
        ["veh_dcp_niva_green"] = "Нива 2121, зелёная",
        ["veh_dcp_zaz968_2"] = "ЗАЗ-968М, белый",
        ["veh_dcp_lada_2101"] = "ВАЗ-2101",
        ["veh_dcp_lada_dead"] = "ВАЗ-2101, разбитая",
        ["veh_dcp_uaz_van"] = "УАЗ-452 «Буханка»",
        ["veh_dcp_uaz_van_broken"] = "УАЗ-452 «Буханка», ржавая",
        ["veh_dcp_uaz_broken"] = "ГАЗ-69",
        ["veh_dcp_gaz24"] = "ГАЗ-24 «Волга»",
        ["veh_dcp_moskvich_412"] = "ИЖ Москвич-412",
        ["veh_dcp_raf"] = "РАФ-2203",
        ["veh_dcp_moskvich_2715"] = "Москвич-2715",
        ["veh_dcp_uaz"] = "УАЗ, чёрный",
        ["veh_dcp_zil"] = "ЗИЛ-130",
        ["veh_dcp_zil131"] = "ЗИЛ-131",
        ["veh_dcp_mercedes_w123"] = "Mercedes W123",
        ["veh_dcp_lada_2107"] = "ВАЗ-2107",
        ["veh_dcp_lada_2108"] = "ВАЗ-2108",
        ["veh_dcp_niva_2329"] = "Нива-2329, тюнингованная",
        ["veh_dcp_brdm"] = "БРДМ с башней",
        ["veh_dcp_btr"] = "БТР-60 с башней",
        ["veh_dcp_kamaz_army_tent"] = "Военный КамАЗ с тентом",
        ["veh_dcp_kamaz_army"] = "Военный КамАЗ без тента",
        ["veh_btr"] = "(legacy) БТР",
        ["veh_kamaz"] = "(legacy) КамАЗ",
        ["veh_niva_g"] = "(legacy) Нива, зелёная",
        ["veh_niva_w"] = "(legacy) Нива, белая",
        ["veh_tr13"] = "(legacy) Трактор",
        ["veh_uaz"] = "(legacy) УАЗ",
        ["veh_zaz"] = "(legacy) ЗАЗ"
    };

    public static string Display(object? value) => value switch
    {
        TriggerComparator.Exact => "= точная сумма",
        TriggerComparator.GreaterOrEqual => "≥ сумма или больше",
        QueueMode.Skip => "Сразу пропустить",
        QueueMode.WaitForDuration => "Ждать N минут",
        QueueMode.WaitIndefinitely => "Ждать без ограничения",
        DispatchState.Queued => "В очереди",
        DispatchState.Sent => "Отправлено",
        DispatchState.Executed => "Выполнено",
        DispatchState.Deferred => "Отложено",
        DispatchState.Rejected => "Отклонено",
        DispatchState.Uncertain => "Не подтверждено",
        DispatchState.Skipped => "Пропущено",
        DispatchState.Cancelled => "Удалено из очереди",
        DispatchState.Expired => "Истекло",
        DispatchState.Duplicate => "Повтор",
        DispatchState.NoRule => "Нет правила",
        string text when Labels.TryGetValue(text, out var label) => label,
        string text when text.Contains('_') => text.Replace('_', ' '),
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Display(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HistoryStatusMarkerConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DispatchState.Rejected or DispatchState.Uncertain or DispatchState.Expired => "!",
        DispatchState.Executed => "✓",
        DispatchState.Queued or DispatchState.Deferred => "…",
        DispatchState.Sent => "↗",
        _ => "–"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
