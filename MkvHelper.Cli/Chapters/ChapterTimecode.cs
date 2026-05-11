namespace MkvHelper;

/// <summary>
/// Parsing and normalisation helpers for Matroska chapter timestamps,
/// which use the <c>HH:MM:SS.fffffffff</c> format (with up to nine
/// fractional digits).  These strings flow through the XML round-trip in
/// both directions, and we normalise on the read side so the same chapter
/// always produces the same string regardless of how mkvextract printed it.
/// </summary>
public static class ChapterTimecode
{
    /// <summary>
    /// Parse a Matroska timestamp into a count of seconds.  Throws
    /// <see cref="FormatException"/> on unparseable input — chapter XML
    /// from mkvextract is always well-formed, so anything that fails here
    /// indicates a real problem upstream and shouldn't be silently swallowed.
    /// </summary>
    public static double ToSeconds(string timecode)
    {
        if (TimeSpan.TryParse(timecode, out var ts))
            return ts.TotalSeconds;
        throw new FormatException($"Not a valid chapter timecode: '{timecode}'");
    }

    /// <summary>
    /// Strip trailing zeros (and a dangling decimal point) so that
    /// <c>00:01:23.450000000</c> becomes <c>00:01:23.45</c> and
    /// <c>00:01:23.000000000</c> becomes <c>00:01:23</c>.  Pure formatting
    /// — value is unchanged.
    /// </summary>
    public static string Normalize(string timecode) =>
        string.IsNullOrEmpty(timecode) ? "" : timecode.TrimEnd('0').TrimEnd('.');
}
