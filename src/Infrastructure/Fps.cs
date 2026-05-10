using System.Globalization;

namespace CompressMkv;

/// <summary>
/// Frame rate as an exact fraction.  Stored in canonical reduced form so that
/// equal rates compare equal regardless of how they were originally written
/// (e.g. 60/2 normalizes to 30/1, so it equals <see cref="Flat30"/>).
///
/// Two comparison flavors:
///   ==/Equals       — strict bit-for-bit fraction match.  Use for set/dictionary
///                     keys and for "is this exactly the canonical NTSC fraction".
///   IsApproximately — semantic "same rate" with a small fps tolerance.  Use when
///                     one side might be a float-rounded ffprobe output (e.g.
///                     "29.97" instead of "30000/1001").  Default tolerance is
///                     <see cref="DefaultApproxTolerance"/>, sized to discriminate
///                     all common rate pairs (30000/1001 vs 30/1, 24000/1001 vs
///                     24/1, etc.) while accepting ffprobe's rounding noise.
///
/// Construct via <see cref="FromRatio"/> (validated) or <see cref="TryParse"/>
/// (handles ffprobe's "num/den" and bare-float forms).  default(Fps) is invalid
/// and should not be used — wrap in Fps? for nullability.
/// </summary>
public readonly struct Fps : IEquatable<Fps>
{
    public int Numerator { get; }
    public int Denominator { get; }

    /// <summary>
    /// Default fps tolerance for <see cref="IsApproximately"/> — 0.01 fps.
    /// Comfortably below the smallest rate distinction we care about (30/1 vs
    /// 30000/1001 differ by 0.030 fps) and well above ffprobe's typical rounding
    /// noise (under 0.001 fps for 5-decimal-digit float reports).
    /// </summary>
    public const double DefaultApproxTolerance = 0.01;

    // ---- Common rate constants ----

    /// <summary>NTSC native — telecined or interlaced storage rate: 30000/1001 ≈ 29.97 fps.</summary>
    public static readonly Fps Ntsc30 = FromRatio(30000, 1001);

    /// <summary>NTSC progressive (after IVTC, or soft-pulldown stored): 24000/1001 ≈ 23.976 fps.</summary>
    public static readonly Fps Ntsc24 = FromRatio(24000, 1001);

    /// <summary>NTSC high-rate (60p Blu-rays, sports): 60000/1001 ≈ 59.94 fps.</summary>
    public static readonly Fps Ntsc60 = FromRatio(60000, 1001);

    /// <summary>PAL standard rate: 25 fps.</summary>
    public static readonly Fps Pal25 = FromRatio(25, 1);

    /// <summary>PAL high rate: 50 fps.</summary>
    public static readonly Fps Pal50 = FromRatio(50, 1);

    /// <summary>Cinema standard: 24 fps exact (distinct from NTSC 24000/1001).</summary>
    public static readonly Fps Film24 = FromRatio(24, 1);

    /// <summary>Web/screen-capture flat rate: 30 fps exact (distinct from NTSC 30000/1001).</summary>
    public static readonly Fps Flat30 = FromRatio(30, 1);

    /// <summary>Web/screen-capture flat rate: 60 fps exact (distinct from NTSC 60000/1001).</summary>
    public static readonly Fps Flat60 = FromRatio(60, 1);

    private Fps(int numerator, int denominator)
    {
        // Reduce to canonical form so equal rates compare equal under ==.
        int g = Gcd(numerator, denominator);
        Numerator = numerator / g;
        Denominator = denominator / g;
    }

    /// <summary>Constructs a frame rate from an integer ratio.  Both arguments must be positive.</summary>
    public static Fps FromRatio(int numerator, int denominator)
    {
        if (numerator <= 0)
            throw new ArgumentException("FPS numerator must be positive.", nameof(numerator));
        if (denominator <= 0)
            throw new ArgumentException("FPS denominator must be positive.", nameof(denominator));
        return new Fps(numerator, denominator);
    }

    /// <summary>Decimal value of the fraction.</summary>
    public double AsDouble => (double)Numerator / Denominator;

    /// <summary>True if this struct was properly constructed (default(Fps) is invalid).</summary>
    public bool IsValid => Denominator > 0 && Numerator > 0;

    // ---- Equality ----

    public bool Equals(Fps other) =>
        Numerator == other.Numerator && Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is Fps f && Equals(f);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public static bool operator ==(Fps a, Fps b) => a.Equals(b);
    public static bool operator !=(Fps a, Fps b) => !a.Equals(b);

    /// <summary>
    /// Approximate equality on the decimal value.  Use when one side might be a
    /// float-rounded ffprobe output (e.g. "29.97" instead of "30000/1001").
    /// </summary>
    public bool IsApproximately(Fps other, double tolerance = DefaultApproxTolerance) =>
        Math.Abs(AsDouble - other.AsDouble) < tolerance;

    // ---- Domain helpers ----

    /// <summary>True for any NTSC family rate: 30000/1001, 24000/1001, or 60000/1001.</summary>
    public bool IsNtscFamily(double tolerance = DefaultApproxTolerance) =>
        IsApproximately(Ntsc30, tolerance)
        || IsApproximately(Ntsc24, tolerance)
        || IsApproximately(Ntsc60, tolerance);

    /// <summary>True only for the NTSC telecine/interlaced storage rate (30000/1001).
    /// This is the gate for applying the IVTC chain — applying it to any other
    /// source rate forces a frame-dropping rate conversion.</summary>
    public bool IsNtscThirty(double tolerance = DefaultApproxTolerance) =>
        IsApproximately(Ntsc30, tolerance);

    // ---- Parsing ----

    /// <summary>
    /// Parses ffprobe's frame-rate strings.  Accepts "num/den" (preferred,
    /// e.g. "30000/1001") and bare floats (fallback, e.g. "29.97").
    /// Returns false for null, empty, or unparseable input.
    /// </summary>
    public static bool TryParse(string? s, out Fps fps)
    {
        fps = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Split('/');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) &&
            n > 0 && d > 0)
        {
            fps = new Fps(n, d);
            return true;
        }

        if (parts.Length == 1 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            v > 0)
        {
            fps = FromDouble(v);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Approximation of a float as an integer fraction.  Used only as a fallback
    /// when ffprobe returns a float ("29.97") instead of a "num/den" pair.
    /// 5 decimal digits of precision — well past ffprobe's typical output.
    /// </summary>
    private static Fps FromDouble(double v)
    {
        const int Scale = 100000;
        int n = (int)Math.Round(v * Scale);
        return new Fps(n, Scale);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }

    /// <summary>
    /// Format suitable for passing to ffmpeg's `-r` flag.  Whole rates render as
    /// integers ("30"), fractional rates as "num/den" ("30000/1001").
    /// </summary>
    public override string ToString() =>
        Denominator == 1
            ? Numerator.ToString(CultureInfo.InvariantCulture)
            : $"{Numerator}/{Denominator}";
}
