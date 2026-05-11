namespace MkvHelper;

/// <summary>
/// Maps ffprobe's <c>field_order</c> string to <see cref="FieldParity"/>.
///
/// ffprobe emits one of <c>progressive | tt | bb | tb | bt | tff | bff | unknown</c>.
/// Only <c>tt/tff</c> and <c>bb/bff</c> represent a clean parity commitment;
/// <c>tb/bt</c> are mixed (top/bottom OR bottom/top transitions) and
/// <c>progressive/unknown</c> aren't parity claims at all — all of those
/// return <see cref="FieldParity.Auto"/> so the downstream parity detector
/// gets the final say from idet.
/// </summary>
public static class FieldOrderMapper
{
    public static FieldParity MapToParity(string fieldOrderLower)
    {
        if (string.IsNullOrWhiteSpace(fieldOrderLower)) return FieldParity.Auto;

        if (fieldOrderLower.Contains("tt") || fieldOrderLower.Contains("tff"))
            return FieldParity.Tff;

        if (fieldOrderLower.Contains("bb") || fieldOrderLower.Contains("bff"))
            return FieldParity.Bff;

        return FieldParity.Auto;
    }
}
