using ChroniclesDonationBridge.GameIpc;

namespace ChroniclesDonationBridge.App.Services;

public static class GameStatusPresentation
{
    public static string Connection(GameConnectionSnapshot snapshot)
    {
        if (!snapshot.Connected) return snapshot.Reason;
        if (snapshot.Reason == "game_suspended") return "Игра не отвечает · очередь ожидает";
        if (snapshot.Reason == "result_delayed") return "Ответ задерживается · обновите события";
        if (snapshot.Ready) return $"Готова · аддон {snapshot.AddonVersion}";
        return snapshot.Reason switch
        {
            "game_loading" => "Загрузка сохранения · очередь ожидает",
            "game_paused" or "game_menu_open" => "Игра на паузе · очередь ожидает",
            "actor_in_dialog" => "Диалог · очередь ожидает",
            "actor_in_cutscene" => "Катсцена · очередь ожидает",
            "actor_sleeping" => "Персонаж спит · очередь ожидает",
            "actor_dead" => "Персонаж погиб · очередь ожидает",
            "actor_in_vehicle" => "Персонаж в машине · очередь ожидает",
            "actor_unavailable" or "level_unavailable" => "Ожидание загрузки игры",
            _ => snapshot.Reason
        };
    }
}
