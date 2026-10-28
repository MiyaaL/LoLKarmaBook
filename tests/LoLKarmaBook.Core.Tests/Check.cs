namespace LoLKarmaBook.Core.Tests;

internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Assertion failed: " + message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Assertion failed: expected <{expected}> but found <{actual}>. {message}");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new InvalidOperationException(
                $"Assertion failed: value must differ from <{unexpected}>. {message}");
        }
    }

    public static void NotNull<T>(T? value, string? message = null)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException("Assertion failed: value is null. " + message);
        }
    }

    public static void NotNull<T>(T? value, string? message = null)
        where T : struct
    {
        if (value is null)
        {
            throw new InvalidOperationException("Assertion failed: value is null. " + message);
        }
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(
                $"Assertion failed: expected null but found <{value}>. {message}");
        }
    }

    public static void Contains(string expectedSubstring, string actual, string? message = null)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Assertion failed: <{actual}> does not contain <{expectedSubstring}>. {message}");
        }
    }

    public static TException Throws<TException>(Action action, string? message = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Assertion failed: expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}. {message}",
                ex);
        }

        throw new InvalidOperationException(
            $"Assertion failed: expected {typeof(TException).Name} but no exception was thrown. {message}");
    }

    public static async Task<TException> ThrowsAsync<TException>(
        Func<Task> action,
        string? message = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Assertion failed: expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}. {message}",
                ex);
        }

        throw new InvalidOperationException(
            $"Assertion failed: expected {typeof(TException).Name} but no exception was thrown. {message}");
    }

    public static async Task<TException> ThrowsAnyAsync<TException>(
        Func<Task> action,
        string? message = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Assertion failed: expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}. {message}",
                ex);
        }

        throw new InvalidOperationException(
            $"Assertion failed: expected {typeof(TException).Name} but no exception was thrown. {message}");
    }
}
