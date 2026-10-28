namespace LoLKarmaBook.Scoring;

/// <summary>
/// Raised when scoring input is malformed, out of range, contradictory, or not permitted
/// by the module's source/scope rules. <see cref="Code"/> is stable and machine-readable.
/// </summary>
public sealed class ScoringValidationException : Exception
{
    public ScoringValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public ScoringValidationException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
