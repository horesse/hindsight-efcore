using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight.Tests;

/// <summary>
/// Resolves a temporal entity's history entity type the same way production code does — through the
/// source's <see cref="HindsightAnnotationNames.HistoryEntityType"/> annotation — rather than assuming
/// the history entity's identity in the model equals its physical table name. Since DESIGN.md D15 that
/// is only true by coincidence for a source's own current table name; the two diverge as soon as
/// either the source or the history table has a name different from what it started with.
/// </summary>
internal static class HistoryModelTestExtensions
{
    public static IEntityType HistoryEntityType(this IModel model, Type sourceClrType)
    {
        var source = model.FindEntityType(sourceClrType)
            ?? throw new InvalidOperationException($"'{sourceClrType}' is not in the model.");
        return model.HistoryEntityType(source);
    }

    public static IEntityType HistoryEntityType(this IModel model, IReadOnlyEntityType source)
    {
        var name = (string?)source[HindsightAnnotationNames.HistoryEntityType]
            ?? throw new InvalidOperationException($"'{source.DisplayName()}' is not temporal.");
        return model.FindEntityType(name)
            ?? throw new InvalidOperationException($"History entity type '{name}' is missing from the model.");
    }
}
