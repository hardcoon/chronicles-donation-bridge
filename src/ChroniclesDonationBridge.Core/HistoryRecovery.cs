namespace ChroniclesDonationBridge.Core;

public static class HistoryRecovery
{
    public static EventHistoryEntry CreateInterruptedSendEntry(
        DonationEvent donation,
        ProcessedDonationRecord sentRecord,
        EventHistoryEntry? commandSnapshot,
        ActionDefinition? sourceAction,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(donation);
        ArgumentNullException.ThrowIfNull(sentRecord);

        var snapshotIsExact = commandSnapshot is not null &&
            commandSnapshot.State is DispatchState.Queued or DispatchState.Sent &&
            string.Equals(commandSnapshot.DonationId, sentRecord.DonationId, StringComparison.Ordinal) &&
            string.Equals(commandSnapshot.CommandId, sentRecord.CommandId, StringComparison.Ordinal) &&
            Validation.InstanceId().IsMatch(commandSnapshot.ActionId) &&
            Validation.ActionId().IsMatch(commandSnapshot.HandlerId) &&
            !string.IsNullOrWhiteSpace(commandSnapshot.ActionName);

        var sourceMatchesSnapshot = snapshotIsExact && sourceAction is not null &&
            string.Equals(sourceAction.Id, commandSnapshot!.ActionId, StringComparison.Ordinal) &&
            (string.Equals(sourceAction.HandlerId, commandSnapshot.HandlerId, StringComparison.Ordinal) ||
             string.Equals(sourceAction.HandlerId, DonationDispatcher.RandomAllPresetsHandlerId, StringComparison.Ordinal));
        var isRandomAll = sourceMatchesSnapshot &&
            string.Equals(sourceAction!.HandlerId, DonationDispatcher.RandomAllPresetsHandlerId, StringComparison.Ordinal);

        return new EventHistoryEntry
        {
            Timestamp = timestamp,
            DonationId = donation.Id,
            DonationCreatedAt = donation.CreatedAt,
            CommandId = sentRecord.CommandId,
            Donor = donation.Username,
            Amount = donation.Amount,
            Currency = donation.Currency,
            ActionId = snapshotIsExact ? commandSnapshot!.ActionId : string.Empty,
            HandlerId = snapshotIsExact ? commandSnapshot!.HandlerId : string.Empty,
            ActionName = snapshotIsExact ? commandSnapshot!.ActionName : string.Empty,
            State = DispatchState.Uncertain,
            Detail = snapshotIsExact
                ? "Приложение было закрыто до подтверждения игры; автоматический повтор запрещён"
                : "Приложение было закрыто до подтверждения игры; снимок действия не найден, ручной повтор запрещён",
            CanRetryManually = sourceMatchesSnapshot && !isRandomAll
        };
    }
}
