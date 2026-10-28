using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoLKarmaBook.Scoring.Json;

internal static class ScoringJson
{
    public const long MaxBytes = 5L * 1024 * 1024;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // .NET 10: duplicate property names are an error, never last-value-wins. The explicit
        // DuplicatePropertyValidator walk below is kept as defence in depth and for precise
        // path reporting.
        AllowDuplicateProperties = false
    };

    public static string ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes)
            {
                throw new ScoringValidationException(
                    "scoring.file-too-large",
                    $"Scoring import exceeds the 5 MiB limit ({stream.Length} bytes).");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            EnsureSize(text);
            return text;
        }
        catch (ScoringValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ScoringValidationException(
                "scoring.file-read",
                $"Could not read scoring import: {ex.Message}",
                ex);
        }
    }

    public static void EnsureSize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxBytes)
        {
            throw new ScoringValidationException(
                "scoring.file-too-large",
                $"Scoring import exceeds the 5 MiB limit ({bytes} bytes).");
        }
    }

    public static T Deserialize<T>(string json, string errorCode)
        where T : class
    {
        EnsureSize(json);

        // Reject duplicate property names at any depth before the serializer can silently pick
        // the last value. Malformed documents also surface here as ScoringValidationException.
        DuplicatePropertyValidator.Validate(json, errorCode);

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new ScoringValidationException(errorCode, "Scoring import produced no document.");
        }
        catch (ScoringValidationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ScoringValidationException(errorCode, $"Malformed scoring JSON: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ScoringValidationException(errorCode, $"Unsupported scoring JSON: {ex.Message}", ex);
        }
    }

    public static string Serialize<T>(T value, bool indented)
        where T : class
    {
        var options = indented
            ? new JsonSerializerOptions(Options) { WriteIndented = true }
            : Options;
        return JsonSerializer.Serialize(value, options);
    }
}
