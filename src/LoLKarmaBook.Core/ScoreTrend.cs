namespace LoLKarmaBook.Core;

/// <summary>One dated score, e.g. a saved Hexdata report.</summary>
public sealed record ScoreSample(DateTimeOffset At, double Score);

/// <summary>A normalized chart point: X and Y are both in [0, 1].</summary>
public sealed record ScoreTrendPoint(DateTimeOffset At, double Score, double X, double Y);

/// <summary>Canvas coordinates for one trend point (WPF-free so it stays testable).</summary>
public readonly record struct ChartPlotPoint(double X, double Y);

/// <summary>
/// Normalizes dated scores for a line chart. The newest sample is the latest
/// score; a flat series is drawn through the vertical middle instead of dividing
/// by zero. This is pure math so the chart can be unit-tested without WPF.
/// </summary>
public sealed class ScoreTrend
{
    private ScoreTrend(
        IReadOnlyList<ScoreTrendPoint> points,
        double minimum,
        double maximum,
        double? latest)
    {
        Points = points;
        Minimum = minimum;
        Maximum = maximum;
        Latest = latest;
    }

    public static ScoreTrend Empty { get; } = new(Array.Empty<ScoreTrendPoint>(), 0, 0, null);

    public IReadOnlyList<ScoreTrendPoint> Points { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public double? Latest { get; }

    public bool IsEmpty => Points.Count == 0;

    public static ScoreTrend Build(IEnumerable<ScoreSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ScoreSample[] ordered = samples
            .Where(sample => double.IsFinite(sample.Score))
            .OrderBy(sample => sample.At)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Empty;
        }

        double minimum = ordered.Min(sample => sample.Score);
        double maximum = ordered.Max(sample => sample.Score);
        DateTimeOffset first = ordered[0].At;
        DateTimeOffset last = ordered[^1].At;
        TimeSpan span = last - first;
        var points = new List<ScoreTrendPoint>(ordered.Length);
        for (int index = 0; index < ordered.Length; index++)
        {
            ScoreSample sample = ordered[index];
            double x = span > TimeSpan.Zero
                ? (sample.At - first).TotalSeconds / span.TotalSeconds
                : ordered.Length == 1 ? 0.5 : index / (double)(ordered.Length - 1);
            double y = maximum - minimum <= double.Epsilon
                ? 0.5
                : (sample.Score - minimum) / (maximum - minimum);
            points.Add(new ScoreTrendPoint(sample.At, sample.Score, Clamp01(x), Clamp01(y)));
        }

        return new ScoreTrend(points, minimum, maximum, ordered[^1].Score);
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
