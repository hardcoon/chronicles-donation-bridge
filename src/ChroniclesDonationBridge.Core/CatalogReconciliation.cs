namespace ChroniclesDonationBridge.Core;

public static class CatalogReconciliation
{
    public static void RetainDeclaredActions(
        IList<ActionDefinition> catalog,
        IReadOnlySet<string> declaredActionIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(declaredActionIds);
        for (var index = catalog.Count - 1; index >= 0; index--)
        {
            if (!declaredActionIds.Contains(catalog[index].HandlerId))
            {
                catalog.RemoveAt(index);
            }
        }
    }

    public static string ResolveSavedParameterValue(
        ParameterDefinition definition,
        IReadOnlyDictionary<string, string> savedParameters)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(savedParameters);
        if (!savedParameters.TryGetValue(definition.Key, out var saved))
        {
            return definition.DefaultValue;
        }

        if (definition.Kind == ParameterKind.MultiChoice)
        {
            var selected = definition.SelectedOptions(saved)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return string.Join(',', definition.Options.Where(selected.Contains));
        }

        return definition.Validate(saved) is null ? saved : definition.DefaultValue;
    }
}
