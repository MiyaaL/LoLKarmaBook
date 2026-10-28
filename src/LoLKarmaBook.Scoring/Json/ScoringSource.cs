namespace LoLKarmaBook.Scoring.Json;

internal static class ScoringSource
{
    public static string Validate(string? source, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(source) || source != source.Trim()
            || source.Length > 120 || source.Any(char.IsControl))
        {
            throw new ScoringValidationException(
                errorCode,
                "Source must be a non-empty, trimmed label of at most 120 characters without control characters.");
        }

        return source;
    }
}
