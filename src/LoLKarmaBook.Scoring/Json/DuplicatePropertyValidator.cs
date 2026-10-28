using System.Text.Json;

namespace LoLKarmaBook.Scoring.Json;

/// <summary>
/// Rejects JSON objects that contain the same property name more than once at any depth
/// (compared case-insensitively, because both spellings bind to one member).
/// <see cref="JsonSerializer"/> keeps the last value for duplicate keys unless
/// <c>AllowDuplicateProperties</c> is disabled (.NET 10), which would let conflicting metadata
/// (for example two <c>source</c> or <c>schemaVersion</c> values) pass validation. This walker
/// inspects the raw document before deserialization so the conflict is an explicit, stable error
/// with a JSON path, independent of serializer defaults.
/// </summary>
internal static class DuplicatePropertyValidator
{
    /// <summary>
    /// Throws <see cref="ScoringValidationException"/> with <paramref name="errorCode"/> when the
    /// JSON is malformed or declares a duplicate property name in any object.
    /// </summary>
    public static void Validate(string json, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException ex)
        {
            throw new ScoringValidationException(errorCode, $"Malformed scoring JSON: {ex.Message}", ex);
        }

        using (document)
        {
            Walk(document.RootElement, "$", errorCode);
        }
    }

    private static void Walk(JsonElement element, string path, string errorCode)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Case-insensitive matching mirrors JsonSerializer's property binding (the module
                // sets PropertyNameCaseInsensitive = false, which only affects *matching*; "source"
                // and "Source" still bind to the same member and must not silently disagree).
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        throw new ScoringValidationException(
                            errorCode,
                            $"Duplicate JSON property \"{property.Name}\" at {path}; conflicting values are not allowed.");
                    }

                    Walk(property.Value, $"{path}.{property.Name}", errorCode);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index}]", errorCode);
                    index++;
                }

                break;
        }
    }

}
