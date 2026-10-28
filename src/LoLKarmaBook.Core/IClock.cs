namespace LoLKarmaBook.Core;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
