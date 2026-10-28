namespace LoLKarmaBook.Core;

public enum ParticipantSide
{
    Ally = 1,
    Enemy = 2,
    Unknown = 3
}

public enum RosterVisibility
{
    Visible = 1,
    Hidden = 2,
    Unknown = 3
}

public enum IdentityStrength
{
    Unknown = 1,
    NameOnly = 2,
    VerifiedStable = 3
}

/// <summary>
/// One roster entry from a single observed session. Hidden/unknown participants
/// are constructed without an identity or display name: the factories drop those
/// fields at the adapter boundary so they cannot leak into logs, storage or the
/// matcher.
/// </summary>
public sealed class Participant
{
    private const int MaxDisplayIdLength = 128;
    private const int MaxChampionLength = 64;

    public string ParticipantKey { get; }
    public ParticipantSide Side { get; }
    public string? Champion { get; }
    public string? CurrentDisplayId { get; }
    public RosterVisibility Visibility { get; }
    public IdentityStrength IdentityStrength { get; }
    public PlayerIdentity? Identity { get; }
    public bool IsSelf { get; }

    private Participant(
        string participantKey,
        ParticipantSide side,
        string? champion,
        string? currentDisplayId,
        RosterVisibility visibility,
        IdentityStrength identityStrength,
        PlayerIdentity? identity,
        bool isSelf)
    {
        ParticipantKey = participantKey;
        Side = side;
        Champion = champion;
        CurrentDisplayId = currentDisplayId;
        Visibility = visibility;
        IdentityStrength = identityStrength;
        Identity = identity;
        IsSelf = isSelf;
    }

    public static Participant Visible(
        string participantKey,
        ParticipantSide side,
        string currentDisplayId,
        PlayerIdentity identity,
        string? champion = null,
        bool isSelf = false)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Create(
            participantKey,
            side,
            NormalizeDisplayId(currentDisplayId),
            NormalizeOptional(champion, MaxChampionLength, nameof(champion)),
            RosterVisibility.Visible,
            IdentityStrength.VerifiedStable,
            identity,
            isSelf);
    }

    public static Participant VisibleNameOnly(
        string participantKey,
        ParticipantSide side,
        string currentDisplayId,
        string? champion = null,
        bool isSelf = false)
    {
        return Create(
            participantKey,
            side,
            NormalizeDisplayId(currentDisplayId),
            NormalizeOptional(champion, MaxChampionLength, nameof(champion)),
            RosterVisibility.Visible,
            IdentityStrength.NameOnly,
            null,
            isSelf);
    }

    public static Participant VisibleUnknown(
        string participantKey,
        ParticipantSide side,
        string? currentDisplayId = null,
        string? champion = null,
        bool isSelf = false)
    {
        return Create(
            participantKey,
            side,
            currentDisplayId is null ? null : NormalizeDisplayId(currentDisplayId),
            NormalizeOptional(champion, MaxChampionLength, nameof(champion)),
            RosterVisibility.Visible,
            IdentityStrength.Unknown,
            null,
            isSelf);
    }

    public static Participant Hidden(
        string participantKey,
        ParticipantSide side,
        bool isSelf = false)
    {
        return Create(
            participantKey,
            side,
            null,
            null,
            RosterVisibility.Hidden,
            IdentityStrength.Unknown,
            null,
            isSelf);
    }

    public static Participant UnknownVisibility(
        string participantKey,
        ParticipantSide side,
        bool isSelf = false)
    {
        return Create(
            participantKey,
            side,
            null,
            null,
            RosterVisibility.Unknown,
            IdentityStrength.Unknown,
            null,
            isSelf);
    }

    private static Participant Create(
        string participantKey,
        ParticipantSide side,
        string? currentDisplayId,
        string? champion,
        RosterVisibility visibility,
        IdentityStrength strength,
        PlayerIdentity? identity,
        bool isSelf)
    {
        if (string.IsNullOrWhiteSpace(participantKey))
        {
            throw new ArgumentException("A participant key is required.", nameof(participantKey));
        }

        if (participantKey.Trim().Length > 128)
        {
            throw new ArgumentException("Participant key is too long (max 128).", nameof(participantKey));
        }

        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, "Unknown participant side.");
        }

        if (visibility != RosterVisibility.Visible &&
            (currentDisplayId is not null || champion is not null || identity is not null))
        {
            throw new ArgumentException("Hidden/unknown participants must not carry names, champions or identities.");
        }

        if (identity is not null && strength != IdentityStrength.VerifiedStable)
        {
            throw new ArgumentException("Only verified-stable participants may carry a stable identity.");
        }

        if (strength == IdentityStrength.VerifiedStable && identity is null)
        {
            throw new ArgumentException("A verified-stable participant requires a stable identity.");
        }

        return new Participant(
            participantKey.Trim(),
            side,
            champion,
            currentDisplayId,
            visibility,
            strength,
            identity,
            isSelf);
    }

    private static string NormalizeDisplayId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A visible display id is required.", nameof(value));
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxDisplayIdLength)
        {
            throw new ArgumentException($"Display id is too long (max {MaxDisplayIdLength}).", nameof(value));
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Display id contains control characters.", nameof(value));
        }

        return trimmed;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Value is too long (max {maxLength}).", parameterName);
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Value contains control characters.", parameterName);
        }

        return trimmed;
    }
}
