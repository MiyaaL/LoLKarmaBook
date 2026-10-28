namespace LoLKarmaBook.Riot.Tests;

/// <summary>Minimal console assertion runner, matching the other suites.</summary>
internal static class Runner
{
    private static readonly List<(string Name, Func<Task> Run)> Tests = [];

    public static int Run()
    {
        var failed = 0;
        var passed = 0;

        foreach ((string name, Func<Task> run) in Tests)
        {
            try
            {
                run().GetAwaiter().GetResult();
                Console.WriteLine("PASS " + name);
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + "\n      " + ex.GetType().Name + ": " + ex.Message);
                failed++;
            }
        }

        Console.WriteLine($"Riot API 回归测试：{passed}/{passed + failed} 通过。");
        return failed == 0 ? 0 : 1;
    }

    public static void Add(string name, Func<Task> test) => Tests.Add((name, test));

    public static void Add(string name, Action test) => Tests.Add((name, () =>
    {
        test();
        return Task.CompletedTask;
    }));
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException((message ?? "值不相等") + $"：期望 {expected}，实际 {actual}");
        }
    }

    public static async Task ThrowsAsync(RiotApiFailure failure, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RiotApiException ex) when (ex.Failure == failure)
        {
            Assert.True(!ex.Message.Contains("RGAPI", StringComparison.Ordinal), "异常消息泄露了 API Key。");
            return;
        }

        throw new InvalidOperationException("没有抛出预期的 RiotApiException(" + failure + ")。");
    }
}
