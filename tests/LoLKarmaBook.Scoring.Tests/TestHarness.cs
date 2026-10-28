using System.Globalization;

namespace LoLKarmaBook.Scoring.Tests;

internal sealed class TestRunner
{
    private readonly List<(string Name, Action Test)> _tests = [];

    public void Add(string name, Action test) => _tests.Add((name, test));

    public int Run()
    {
        var failed = 0;
        var passed = 0;

        foreach (var (name, test) in _tests)
        {
            TestAssert.CurrentTest = name;
            TestAssert.ResetCounter();
            try
            {
                test();
                passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                var assertion = TestAssert.LastAssertion is { } a ? $" [assertion #{a}]" : string.Empty;
                Console.WriteLine($"FAIL  {name}{assertion}");
                Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed}/{_tests.Count} tests passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}

internal static class TestAssert
{
    internal static string? CurrentTest { get; set; }

    internal static int? LastAssertion { get; private set; }

    // Counts assertions inside the current test, so a bare "Expected true" message can still be
    // traced back to the exact check that failed.
    private static void Mark() => LastAssertion = (LastAssertion ?? 0) + 1;

    internal static void ResetCounter() => LastAssertion = 0;

    public static void True(bool condition, string? message = null)
    {
        Mark();
        if (!condition)
        {
            throw new InvalidOperationException(message ?? $"Expected true, got false (assertion #{LastAssertion} in {CurrentTest}).");
        }
    }

    public static void False(bool condition, string? message = null)
        => True(!condition, message ?? "Expected false, got true.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                message ?? $"Expected {Format(expected)}, got {Format(actual)}.");
        }
    }

    public static void EqualDouble(double expected, double actual, double tolerance = 1e-9, string? message = null)
    {
        if (double.IsNaN(expected) || double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                message ?? $"Expected {expected:R} ± {tolerance:R}, got {actual:R}.");
        }
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(message ?? $"Expected null, got {Format(value)}.");
        }
    }

    public static void NotNull(object? value, string? message = null)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message ?? "Expected a value, got null.");
        }
    }

    public static void Contains(string expectedSubstring, string? actual)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected text containing \"{expectedSubstring}\", got \"{actual}\".");
        }
    }

    public static ScoringValidationException Throws(string expectedCode, Action action)
    {
        try
        {
            action();
        }
        catch (ScoringValidationException ex)
        {
            if (!string.Equals(ex.Code, expectedCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected exception code \"{expectedCode}\", got \"{ex.Code}\" ({ex.Message}).");
            }

            return ex;
        }

        throw new InvalidOperationException($"Expected ScoringValidationException \"{expectedCode}\", nothing was thrown.");
    }

    public static void ThrowsArgumentOutOfRange(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException("Expected ArgumentOutOfRangeException, nothing (or another exception) was thrown.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        var expectedList = expected.ToArray();
        var actualList = actual.ToArray();
        if (!expectedList.SequenceEqual(actualList))
        {
            throw new InvalidOperationException(
                message ?? $"Expected [{string.Join(", ", expectedList)}], got [{string.Join(", ", actualList)}].");
        }
    }

    private static string Format<T>(T value)
        => value switch
        {
            null => "null",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null"
        };
}

internal static class TestPaths
{
    public static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "fixtures", "scoring")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate repository fixtures/scoring directory.");
        }
    }

    public static string Fixture(string name)
        => Path.Combine(RepoRoot, "fixtures", "scoring", name);
}
