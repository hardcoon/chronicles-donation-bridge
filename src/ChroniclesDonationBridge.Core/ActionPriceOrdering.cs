namespace ChroniclesDonationBridge.Core;

public static class ActionPriceOrdering
{
    public static List<ActionDefinition> SortByMinimumAssignedAmount(IEnumerable<ActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        return actions
            .Select((action, index) => new
            {
                Action = action,
                OriginalIndex = index,
                MinimumAmount = action.Triggers
                    .Where(trigger => trigger.Enabled)
                    .Select(trigger => (decimal?)trigger.Amount)
                    .Min()
            })
            .OrderBy(item => item.MinimumAmount.HasValue ? 0 : 1)
            .ThenBy(item => item.MinimumAmount)
            .ThenBy(item => item.OriginalIndex)
            .Select(item => item.Action)
            .ToList();
    }
}
