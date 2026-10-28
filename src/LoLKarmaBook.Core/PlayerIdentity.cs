namespace LoLKarmaBook.Core;

/// <summary>
/// A verified stable account identity. Exact matching uses the complete scope:
/// operator + identity namespace + realm scope + id kind + id value.
/// Display names are never part of this type.
/// </summary>
public sealed class PlayerIdentity : IEquatable<PlayerIdentity>
{
    private const int MaxComponentLength = 128;
    private const int MaxValueLength = 512;

    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "0",
        "unknown",
        "null",
        "none",
        "n/a",
        "na",
        "-",
        "?",
        "placeholder",
        "unavailable",
        "undefined",
        "hidden",
        "redacted",
        "00000000-0000-0000-0000-000000000000"
    };

    public string Operator { get; }
    public string IdentityNamespace { get; }
    public string RealmScope { get; }
    public string Kind { get; }
    public string Value { get; }

    /// <summary>
    /// Human-readable concatenation. This value is never used as the storage
    /// key by itself; the storage layer uses all five fields.
    /// </summary>
    public string ScopeKey =>
        string.Join('|', Operator, IdentityNamespace, RealmScope, Kind, Value);

    private PlayerIdentity(string @operator, string identityNamespace, string realmScope, string kind, string value)
    {
        Operator = @operator;
        IdentityNamespace = identityNamespace;
        RealmScope = realmScope;
        Kind = kind;
        Value = value;
    }

    public static PlayerIdentity Create(
        string @operator,
        string identityNamespace,
        string realmScope,
        string kind,
        string value)
    {
        if (!TryCreate(@operator, identityNamespace, realmScope, kind, value, out PlayerIdentity? identity, out string? error))
        {
            throw new ArgumentException(error ?? "Invalid player identity.", nameof(value));
        }

        return identity!;
    }

    public static bool TryCreate(
        string? @operator,
        string? identityNamespace,
        string? realmScope,
        string? kind,
        string? value,
        out PlayerIdentity? identity,
        out string? error)
    {
        identity = null;

        if (!TryNormalizeComponent(@operator, "operator", out string? normalizedOperator, out error))
        {
            return false;
        }

        if (!TryNormalizeComponent(identityNamespace, "identity namespace", out string? normalizedNamespace, out error))
        {
            return false;
        }

        if (!TryNormalizeComponent(realmScope, "realm scope", out string? normalizedRealm, out error))
        {
            return false;
        }

        if (!TryNormalizeComponent(kind, "id kind", out string? normalizedKind, out error))
        {
            return false;
        }

        if (!TryNormalizeValue(value, out string? normalizedValue, out error))
        {
            return false;
        }

        identity = new PlayerIdentity(
            normalizedOperator!,
            normalizedNamespace!,
            normalizedRealm!,
            normalizedKind!,
            normalizedValue!);
        error = null;
        return true;
    }

    public static bool IsPlaceholderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string trimmed = value.Trim();
        if (PlaceholderValues.Contains(trimmed))
        {
            return true;
        }

        // "0", "0000", ... and GUID-like all-zero values are not stable IDs.
        bool allZero = true;
        foreach (char ch in trimmed)
        {
            if (ch is '-' or '{' or '}')
            {
                continue;
            }

            if (ch != '0')
            {
                allZero = false;
                break;
            }
        }

        return allZero;
    }

    private static bool TryNormalizeComponent(
        string? value,
        string label,
        out string? normalized,
        out string? error)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"The {label} is required.";
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxComponentLength)
        {
            error = $"The {label} is too long (max {MaxComponentLength}).";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = $"The {label} contains control characters.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    private static bool TryNormalizeValue(string? value, out string? normalized, out string? error)
    {
        normalized = null;
        if (IsPlaceholderValue(value))
        {
            error = "The id value is missing, a placeholder, or all zeroes.";
            return false;
        }

        string trimmed = value!.Trim();
        if (trimmed.Length > MaxValueLength)
        {
            error = $"The id value is too long (max {MaxValueLength}).";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = "The id value contains control characters.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    public bool Equals(PlayerIdentity? other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(Operator, other.Operator, StringComparison.Ordinal)
            && string.Equals(IdentityNamespace, other.IdentityNamespace, StringComparison.Ordinal)
            && string.Equals(RealmScope, other.RealmScope, StringComparison.Ordinal)
            && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
            && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as PlayerIdentity);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Operator),
        StringComparer.Ordinal.GetHashCode(IdentityNamespace),
        StringComparer.Ordinal.GetHashCode(RealmScope),
        StringComparer.Ordinal.GetHashCode(Kind),
        StringComparer.Ordinal.GetHashCode(Value));

    public override string ToString() => ScopeKey;
}
