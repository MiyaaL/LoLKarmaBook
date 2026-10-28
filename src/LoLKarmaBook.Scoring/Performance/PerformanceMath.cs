namespace LoLKarmaBook.Scoring.Performance;

/// <summary>Small, auditable numeric helpers used by the performance score.</summary>
public static class PerformanceMath
{
    /// <summary>
    /// Returns <paramref name="x"/> when it is finite and strictly positive; otherwise 0.
    /// Malformed JSON is rejected at the import boundary before this helper is reached.
    /// </summary>
    public static double PositiveClean(double x)
        => double.IsFinite(x) && x > 0 ? x : 0;

    /// <summary>
    /// Returns <paramref name="part"/> / <paramref name="total"/> when the part is positive and
    /// strictly smaller than the total. Returns null for zero, negative, non-finite, equal or
    /// over-total inputs.
    /// </summary>
    /// <remarks>
    /// Excluding <c>part == total</c> is intentional parity-required behaviour: a share of exactly
    /// 100% is treated as unavailable rather than as a perfect value. Callers must handle null as
    /// "missing", never as zero.
    /// </remarks>
    public static double? Ratio(double part, double total)
    {
        if (!double.IsFinite(part) || !double.IsFinite(total))
        {
            return null;
        }

        if (part <= 0 || total <= part)
        {
            return null;
        }

        return part / total;
    }

    /// <summary>
    /// Faithful reproduction of the legacy clamp
    /// <c>function c(e){return !Number.isFinite(e) || e &lt;= 0 ? 0 : e &gt;= 1 ? 1 : e}</c>:
    /// any non-finite value, including +Infinity, maps to 0; values &lt;= 0 map to 0; values
    /// &gt;= 1 map to 1; otherwise the value passes through.
    /// </summary>
    internal static double Clamp01(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return 0.0;
        }

        return value >= 1 ? 1.0 : value;
    }
}
