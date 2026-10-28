using LoLKarmaBook.Core;

namespace LoLKarmaBook.Core.Tests;

internal static class ScoreTrendTests
{
    public static void OrdersSamplesAndNormalizesCoordinates()
    {
        DateTimeOffset start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        ScoreTrend trend = ScoreTrend.Build(new[]
        {
            new ScoreSample(start.AddDays(2), 1500),
            new ScoreSample(start, 1000),
            new ScoreSample(start.AddDays(1), 1250)
        });

        Check.Equal(3, trend.Points.Count);
        Check.Equal(start, trend.Points[0].At);
        Check.Equal(0d, trend.Points[0].X);
        Check.Equal(0d, trend.Points[0].Y);
        Check.Equal(0.5, trend.Points[1].X);
        Check.Equal(0.5, trend.Points[1].Y);
        Check.Equal(1d, trend.Points[2].X);
        Check.Equal(1d, trend.Points[2].Y);
        Check.Equal(1000d, trend.Minimum);
        Check.Equal(1500d, trend.Maximum);
        Check.Equal(1500d, trend.Latest);
    }

    public static void FlatSeriesStaysInTheMiddle()
    {
        DateTimeOffset start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        ScoreTrend trend = ScoreTrend.Build(new[]
        {
            new ScoreSample(start, 1234),
            new ScoreSample(start.AddHours(1), 1234)
        });

        Check.Equal(2, trend.Points.Count);
        Check.True(trend.Points.All(point => point.Y == 0.5), "a flat series must be drawn through the middle");
        Check.Equal(1234d, trend.Latest);
    }

    public static void SingleAndEmptySeriesAreSafe()
    {
        Check.True(ScoreTrend.Build(Array.Empty<ScoreSample>()).IsEmpty, "empty input must produce an empty trend");
        ScoreTrend one = ScoreTrend.Build(new[]
        {
            new ScoreSample(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), 900)
        });
        Check.Equal(1, one.Points.Count);
        Check.Equal(0.5, one.Points[0].X);
        Check.Equal(0.5, one.Points[0].Y);
        Check.Equal(900d, one.Latest);
    }

    public static void EqualTimestampsDoNotDivideByZero()
    {
        DateTimeOffset at = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        ScoreTrend trend = ScoreTrend.Build(new[]
        {
            new ScoreSample(at, 100),
            new ScoreSample(at, 200),
            new ScoreSample(at, 300)
        });

        Check.Equal(3, trend.Points.Count);
        Check.True(trend.Points.All(point => double.IsFinite(point.X)), "x must stay finite");
        Check.Equal(0d, trend.Points[0].X);
        Check.Equal(1d, trend.Points[2].X);
        Check.Equal(0d, trend.Points[0].Y);
        Check.Equal(1d, trend.Points[2].Y);
    }
}
