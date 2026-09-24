using System.Globalization;
using Hindsight;

namespace HistoryViewerSample;

/// <summary>Formatting shared by the pages. All times are shown in UTC, which is how history stores them.</summary>
public static class Display
{
    public static string Time(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Round-trippable, for the <c>?at=</c> query string: history timestamps have microseconds.</summary>
    public static string Instant(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // `:C` needs a culture with a real currency symbol; InvariantGlobalization (Directory.Build.props) has
    // none, so it prints '¤' instead of '$'. Formatting the number ourselves sidesteps that.
    public static string Money(decimal amount) => $"${amount.ToString("F2", CultureInfo.InvariantCulture)}";

    public static string Value(object? value) => value switch
    {
        null => "—",
        decimal d => Money(d),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Who made a change. Writes that bypass SaveChanges have no change context at all.</summary>
    public static string Who<T>(Version<T> version) where T : class =>
        (version.ChangedByName, version.ChangedBy) switch
        {
            (null, null) => "",
            ({ } name, { } id) => $"{name} ({id})",
            ({ } name, null) => name,
            (null, { } id) => id,
        };

    public static bool HasNoContext<T>(Version<T> version) where T : class =>
        version.ChangedBy is null && version.ChangedByName is null && version.Reason is null;

    public static bool TryParseInstant(string? text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out at);
}
