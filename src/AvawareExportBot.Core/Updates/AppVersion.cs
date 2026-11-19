using System.Globalization;
using System.Text.RegularExpressions;

namespace AvawareExportBot.Core.Updates;

/// <summary>
/// Versions as the updater sees them: always three parts (major.minor.patch). Release tags are <c>vX.Y.Z</c>,
/// and the app's own version comes from its assembly informational version, which the release build sets from the tag.
/// </summary>
public static class AppVersion
{
    public static readonly Version Zero = new(0, 0, 0);

    private static readonly Regex Tag = new(@"^v([0-9]+)\.([0-9]+)\.([0-9]+)$", RegexOptions.CultureInvariant);

    /// <summary>The version in a release tag such as <c>v1.2.3</c>, or null for any other shape (no v, two parts, a suffix).</summary>
    public static Version? ParseTag(string? tag)
    {
        if (tag is null) return null;
        var m = Tag.Match(tag);
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            ? new Version(major, minor, patch)
            : null;
    }

    /// <summary>
    /// The version in an informational version such as <c>1.2.3</c> or <c>1.2.3+4f2a9c1</c>.
    /// Anything after '+' or '-' is ignored; anything unreadable is 0.0.0.
    /// </summary>
    public static Version ParseInformational(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return Zero;
        var core = informational.Split('+', '-')[0].Trim();
        return Version.TryParse(core, out var parsed) ? Normalize(parsed) : Zero;
    }

    /// <summary>Drops a fourth part and fills a missing third, so 1.2 and 1.2.0.0 both compare equal to 1.2.0.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>A version as written in a file or argument (1.2.3), or null.</summary>
    public static Version? TryParse(string? text) =>
        text is not null && Version.TryParse(text.Trim(), out var v) ? Normalize(v) : null;
}
