using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LoLKarmaBook.Core;

/// <summary>
/// Domain-facing notebook API. It validates input, binds notes to a session
/// participant, applies reminder policy, and delegates persistence to an
/// injected profile-scoped <see cref="INotebookStore"/>.
/// </summary>
public sealed class NotebookService
{
    private const int MaxBodyLength = 500;
    private const int MaxRequestIdLength = 128;
    private const int MaxReminderDays = 3650;
    private const int MaxTags = 12;
    private const int MaxTagNameLength = 60;
    private static readonly TimeSpan FutureEventTolerance = TimeSpan.FromMinutes(10);

    private readonly INotebookStore _store;
    private readonly IClock _clock;

    public NotebookService(INotebookStore store, IClock? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? SystemClock.Instance;
    }

    public async Task<StoredNote> SaveNoteAsync(
        MatchSession session,
        string participantKey,
        NoteDraft draft,
        NoteSaveMode mode = NoteSaveMode.Active,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(draft);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown note save mode.");
        }

        string normalizedParticipantKey = NormalizeParticipantKey(participantKey);
        Participant participant = session.FindParticipant(normalizedParticipantKey)
            ?? throw new InvalidOperationException(
                $"Participant '{normalizedParticipantKey}' is not part of session '{session.LocalSessionId}'.");

        EnsureNoteTargetEligible(participant);

        string requestId = NormalizeRequestId(draft.RequestId);
        NoteCategory[] categories = NormalizeCategories(draft.Categories);
        string body = NormalizeBody(draft.Body);
        NoteTag[] tags = NormalizeTags(draft.Tags);
        int reminderDays = NormalizeReminderDays(draft.ReminderDays);
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset eventTime = ResolveEventTime(draft.EventTime, now);

        PlayerIdentity? identity = participant.IdentityStrength == IdentityStrength.VerifiedStable
            ? participant.Identity
            : null;

        if (participant.IdentityStrength == IdentityStrength.VerifiedStable && identity is null)
        {
            throw new InvalidOperationException("A verified-stable participant is missing its identity.");
        }

        // Manual data is user input only. It must never be promoted into a stable
        // identity mapping that a later roster could auto-match.
        if (session.SourceKind == SourceKind.Manual)
        {
            identity = null;
        }

        string fingerprint = ComputeFingerprint(
            session,
            normalizedParticipantKey,
            identity,
            categories,
            body,
            draft.EventTime,
            reminderDays,
            mode,
            tags);

        NoteCategoryEntry[] expiries = categories
            .Select(category => new NoteCategoryEntry(category, now.AddDays(reminderDays)))
            .ToArray();

        var record = new NewNoteRecord(
            requestId,
            fingerprint,
            identity,
            session.LocalSessionId,
            session.ExternalMatchId,
            session.SourceKind,
            eventTime,
            body,
            expiries,
            now,
            mode == NoteSaveMode.Draft ? NoteStatus.Draft : NoteStatus.Active,
            reminderDays,
            participant.CurrentDisplayId,
            tags);

        return await _store.CreateNoteAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredNote> UpdateNoteAsync(
        Guid noteId,
        int expectedRevision,
        NoteEdit edit,
        CancellationToken cancellationToken = default)
    {
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("A note id is required.", nameof(noteId));
        }

        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "Revision must be at least 1.");
        }

        ArgumentNullException.ThrowIfNull(edit);
        NoteCategory[] categories = NormalizeCategories(edit.Categories);
        string body = NormalizeBody(edit.Body);
        NoteTag[] tags = NormalizeTags(edit.Tags);
        int reminderDays = NormalizeReminderDays(edit.ReminderDays);
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset eventTime = ResolveEventTime(edit.EventTime, now);

        NoteCategoryEntry[] expiries = categories
            .Select(category => new NoteCategoryEntry(category, now.AddDays(reminderDays)))
            .ToArray();

        var record = new NoteUpdateRecord(
            noteId,
            expectedRevision,
            eventTime,
            body,
            expiries,
            now,
            reminderDays,
            tags);

        return await _store.UpdateNoteAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredNote> PublishDraftAsync(
        Guid noteId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return await SetStatusAsync(noteId, expectedRevision, NoteStatus.Active, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoredNote> ArchiveNoteAsync(
        Guid noteId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return await SetStatusAsync(noteId, expectedRevision, NoteStatus.Archived, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoredNote> RetractNoteAsync(
        Guid noteId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return await SetStatusAsync(noteId, expectedRevision, NoteStatus.Retracted, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoredNote?> GetNoteAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("A note id is required.", nameof(noteId));
        }

        return await _store.GetNoteAsync(noteId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredNote>> ListNotesAsync(
        NoteQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        NoteQuery effective = query ?? NoteQuery.All;

        if (effective.Categories is not null)
        {
            if (effective.Categories.Count == 0)
            {
                throw new ArgumentException("A category filter must not be empty.", nameof(query));
            }

            foreach (NoteCategory category in effective.Categories)
            {
                if (!NoteCategories.IsDefined(category))
                {
                    throw new ArgumentOutOfRangeException(nameof(query), category, "Unknown note category.");
                }
            }
        }

        if (effective.Status is { } status && !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(query), status, "Unknown note status.");
        }

        if (effective.PlayerId == Guid.Empty)
        {
            throw new ArgumentException("Player id filter must not be empty.", nameof(query));
        }

        if (effective.EventFrom is { } from && effective.EventTo is { } to && from > to)
        {
            throw new ArgumentException("EventFrom must not be after EventTo.", nameof(query));
        }

        return await _store.ListNotesAsync(effective, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteNoteAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("A note id is required.", nameof(noteId));
        }

        return await _store.DeleteNoteAsync(noteId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Preview only: does not create or consume alert receipts. It returns the
    /// reminders that are eligible from history for this session, including for a
    /// post-game roster, so the end-of-match window can show what it met.
    /// </summary>
    public async Task<IReadOnlyList<Reminder>> FindRemindersAsync(
        MatchSession session,
        ReminderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SourceKind is not (SourceKind.Live or SourceKind.Synthetic)
            || !ReminderPolicy.IsRosterPhase(session.Phase))
        {
            return Array.Empty<Reminder>();
        }

        return await BuildRemindersAsync(
                session,
                options ?? ReminderOptions.Default,
                deliverReceipts: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers reminders: creates one receipt per (match key, player), so a
    /// repeated call/restart/reconnect does not show the same player again.
    /// Manual sessions do not supply automatically matched account identities.
    /// </summary>
    public async Task<ReminderEvaluation> EvaluateRemindersAsync(
        MatchSession session,
        ReminderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        switch (session.SourceKind)
        {
            case SourceKind.Manual:
                return ReminderEvaluation.DisabledManual();
            case SourceKind.Live:
            case SourceKind.Synthetic:
                break;
            default:
                return ReminderEvaluation.Unsupported();
        }

        // Sessions without an active match roster do not produce notifications
        // or consume receipts for a later active snapshot.
        if (!ReminderPolicy.IsActiveRosterPhase(session.Phase))
        {
            return ReminderEvaluation.DisabledForPhase(session.Phase);
        }

        IReadOnlyList<Reminder> reminders = await BuildRemindersAsync(
                session,
                options ?? ReminderOptions.Default,
                deliverReceipts: true,
                cancellationToken)
            .ConfigureAwait(false);

        return ReminderEvaluation.Enabled(reminders);
    }

    public async Task<bool> IgnoreForSessionAsync(
        MatchSession session,
        PlayerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identity);

        if (!ReminderPolicy.CanRemind(session))
        {
            return false;
        }

        Guid? playerId = await _store
            .FindPlayerIdAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (playerId is null)
        {
            return false;
        }

        return await _store.SetAlertReceiptStateAsync(
                session.MatchKey,
                session.LocalSessionId,
                playerId.Value,
                ReminderReceiptState.Ignored,
                _clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await _store.ClearAllAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Reminder>> BuildRemindersAsync(
        MatchSession session,
        ReminderOptions options,
        bool deliverReceipts,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        HashSet<Guid>? muted = options.MutedPlayerIds is null
            ? null
            : new HashSet<Guid>(options.MutedPlayerIds);

        var results = new List<Reminder>();
        var seenPlayers = new HashSet<Guid>();

        foreach (Participant participant in session.Participants)
        {
            if (!IsEligibleForReminder(participant, options.Audience))
            {
                continue;
            }

            PlayerIdentity identity = participant.Identity!;
            IReadOnlyList<StoredNote> notes = await _store
                .ListReminderCandidatesAsync(
                    identity,
                    now,
                    session.ExternalMatchId,
                    session.LocalSessionId,
                    cancellationToken)
                .ConfigureAwait(false);

            Guid? playerId = notes
                .Where(note => note.PlayerId.HasValue)
                .Select(note => note.PlayerId)
                .FirstOrDefault();
            if (playerId is null)
            {
                continue;
            }

            if (muted is not null && muted.Contains(playerId.Value))
            {
                continue;
            }

            if (!seenPlayers.Add(playerId.Value))
            {
                continue;
            }

            // A session-ignored player is hidden from both the preview and
            // delivery. A "shown" receipt only suppresses new notifications (see
            // the delivery branch below); previews still show normal history.
            ReminderReceiptState? receiptState = await _store
                .GetAlertReceiptStateAsync(session.MatchKey, playerId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (receiptState == ReminderReceiptState.Ignored)
            {
                continue;
            }

            var reminderNotes = new List<ReminderNote>();
            var activeCategories = new HashSet<NoteCategory>();
            var sessionKeys = new HashSet<string>(StringComparer.Ordinal);
            DateTimeOffset mostRecent = DateTimeOffset.MinValue;

            foreach (StoredNote note in notes)
            {
                if (note.PlayerId != playerId || note.Status != NoteStatus.Active)
                {
                    continue;
                }

                IReadOnlyList<NoteCategory> active = note.GetActiveCategories(now);
                if (active.Count == 0)
                {
                    continue;
                }

                reminderNotes.Add(new ReminderNote(
                    note.NoteId,
                    active,
                    note.EventTime,
                    note.DeduplicationSessionKey));

                foreach (NoteCategory category in active)
                {
                    activeCategories.Add(category);
                }

                sessionKeys.Add(note.DeduplicationSessionKey);
                if (note.EventTime > mostRecent)
                {
                    mostRecent = note.EventTime;
                }
            }

            if (reminderNotes.Count == 0)
            {
                continue;
            }

            if (deliverReceipts)
            {
                bool created = await _store
                    .TryCreateAlertReceiptAsync(
                        session.MatchKey,
                        session.LocalSessionId,
                        playerId.Value,
                        ReminderReceiptState.Shown,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!created)
                {
                    continue;
                }
            }

            results.Add(new Reminder(
                playerId.Value,
                identity,
                participant.ParticipantKey,
                participant.Side,
                participant.CurrentDisplayId,
                activeCategories,
                reminderNotes,
                mostRecent,
                sessionKeys.Count));
        }

        return results;
    }

    private async Task<StoredNote> SetStatusAsync(
        Guid noteId,
        int expectedRevision,
        NoteStatus status,
        CancellationToken cancellationToken)
    {
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("A note id is required.", nameof(noteId));
        }

        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "Revision must be at least 1.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown note status.");
        }

        return await _store
            .SetNoteStatusAsync(noteId, status, expectedRevision, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsEligibleForReminder(Participant participant, ReminderAudience audience)
    {
        if (participant.Visibility != RosterVisibility.Visible)
        {
            return false;
        }

        if (participant.IsSelf)
        {
            return false;
        }

        if (participant.IdentityStrength != IdentityStrength.VerifiedStable || participant.Identity is null)
        {
            return false;
        }

        if (audience == ReminderAudience.AlliesOnly && participant.Side != ParticipantSide.Ally)
        {
            return false;
        }

        return true;
    }

    private static void EnsureNoteTargetEligible(Participant participant)
    {
        if (participant.Visibility != RosterVisibility.Visible)
        {
            throw new InvalidOperationException(
                "Notes cannot be bound to hidden or unknown-visibility participants.");
        }

        if (participant.IsSelf)
        {
            throw new InvalidOperationException("Notes cannot be bound to the local player.");
        }
    }

    private static string NormalizeParticipantKey(string participantKey)
    {
        if (string.IsNullOrWhiteSpace(participantKey))
        {
            throw new ArgumentException("A participant key is required.", nameof(participantKey));
        }

        string trimmed = participantKey.Trim();
        if (trimmed.Length > 128)
        {
            throw new ArgumentException("Participant key is too long (max 128).", nameof(participantKey));
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Participant key contains control characters.", nameof(participantKey));
        }

        return trimmed;
    }

    private static string NormalizeRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("A request id is required.", nameof(requestId));
        }

        string trimmed = requestId.Trim();
        if (trimmed.Length > MaxRequestIdLength)
        {
            throw new ArgumentException(
                $"Request id is too long (max {MaxRequestIdLength}).",
                nameof(requestId));
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Request id contains control characters.", nameof(requestId));
        }

        return trimmed;
    }

    private static NoteCategory[] NormalizeCategories(IReadOnlyList<NoteCategory>? categories)
    {
        if (categories is null)
        {
            throw new ArgumentException("At least one note category is required.", nameof(categories));
        }

        if (categories.Count == 0)
        {
            throw new ArgumentException("At least one note category is required.", nameof(categories));
        }

        var distinct = new HashSet<NoteCategory>();
        foreach (NoteCategory category in categories)
        {
            if (!NoteCategories.IsDefined(category))
            {
                throw new ArgumentOutOfRangeException(nameof(categories), category, "Unknown note category.");
            }

            distinct.Add(category);
        }

        return distinct.OrderBy(category => category).ToArray();
    }

    private static string NormalizeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim();
        if (trimmed.Length > MaxBodyLength)
        {
            throw new ArgumentException($"Note body is too long (max {MaxBodyLength}).", nameof(body));
        }

        foreach (char ch in trimmed)
        {
            if (char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t')
            {
                throw new ArgumentException("Note body contains unsupported control characters.", nameof(body));
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Validates tags structurally: only a known kind, a positive official id and
    /// a short official name are accepted. Free text is not representable, so a
    /// record can never carry a label the catalog does not define.
    /// </summary>
    private static NoteTag[] NormalizeTags(IReadOnlyList<NoteTag>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return Array.Empty<NoteTag>();
        }

        if (tags.Count > MaxTags)
        {
            throw new ArgumentException($"A note can carry at most {MaxTags} tags.", nameof(tags));
        }

        var seen = new HashSet<(TagKind Kind, int RefId)>();
        var result = new List<NoteTag>(tags.Count);
        foreach (NoteTag tag in tags)
        {
            if (!Enum.IsDefined(tag.Kind))
            {
                throw new ArgumentOutOfRangeException(nameof(tags), tag.Kind, "Unknown tag kind.");
            }

            if (tag.RefId <= 0)
            {
                throw new ArgumentException("A tag must reference a positive official id.", nameof(tags));
            }

            string name = (tag.OfficialName ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > MaxTagNameLength)
            {
                throw new ArgumentException(
                    $"A tag name is required and must be at most {MaxTagNameLength} characters.",
                    nameof(tags));
            }

            if (name.Any(char.IsControl))
            {
                throw new ArgumentException("A tag name contains control characters.", nameof(tags));
            }

            if (seen.Add((tag.Kind, tag.RefId)))
            {
                result.Add(new NoteTag(tag.Kind, tag.RefId, name));
            }
        }

        return result.ToArray();
    }

    private static int NormalizeReminderDays(int reminderDays)
    {
        if (reminderDays is < 0 or > MaxReminderDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderDays),
                reminderDays,
                $"Reminder days must be between 0 and {MaxReminderDays}.");
        }

        return reminderDays;
    }
    private static DateTimeOffset ResolveEventTime(DateTimeOffset? eventTime, DateTimeOffset now)
    {
        if (eventTime is null)
        {
            return now;
        }

        if (eventTime.Value == default)
        {
            throw new ArgumentException("Event time is invalid.", nameof(eventTime));
        }

        if (eventTime.Value > now.Add(FutureEventTolerance))
        {
            throw new ArgumentException("Event time is too far in the future.", nameof(eventTime));
        }

        return eventTime.Value;
    }

    private static string ComputeFingerprint(
        MatchSession session,
        string participantKey,
        PlayerIdentity? identity,
        NoteCategory[] categories,
        string body,
        DateTimeOffset? rawEventTime,
        int reminderDays,
        NoteSaveMode mode,
        IReadOnlyList<NoteTag> tags)
    {
        var builder = new StringBuilder();
        // Bind the request to the match it was authored for, so retrying the same
        // request id in a different session is rejected instead of silently
        // returning the first session's note.
        AppendFingerprintPart(builder, session.SourceKind.ToString());
        AppendFingerprintPart(builder, session.LocalSessionId);
        AppendFingerprintPart(builder, session.ExternalMatchId ?? string.Empty);
        AppendFingerprintPart(builder, participantKey);
        // Identity components are appended separately (length-prefixed) so the
        // canonical form is unambiguous and no "a|b" style collision is possible.
        AppendFingerprintPart(builder, identity is null ? "no-identity" : "identity");
        AppendFingerprintPart(builder, identity?.Operator ?? string.Empty);
        AppendFingerprintPart(builder, identity?.IdentityNamespace ?? string.Empty);
        AppendFingerprintPart(builder, identity?.RealmScope ?? string.Empty);
        AppendFingerprintPart(builder, identity?.Kind ?? string.Empty);
        AppendFingerprintPart(builder, identity?.Value ?? string.Empty);
        foreach (NoteCategory category in categories)
        {
            AppendFingerprintPart(builder, ((int)category).ToString(CultureInfo.InvariantCulture));
        }

        AppendFingerprintPart(builder, body);
        AppendFingerprintPart(
            builder,
            rawEventTime?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "default");
        AppendFingerprintPart(builder, reminderDays.ToString(CultureInfo.InvariantCulture));
        AppendFingerprintPart(builder, mode.ToString());
        foreach (NoteTag tag in tags)
        {
            AppendFingerprintPart(builder, ((int)tag.Kind).ToString(CultureInfo.InvariantCulture));
            AppendFingerprintPart(builder, tag.RefId.ToString(CultureInfo.InvariantCulture));
            AppendFingerprintPart(builder, tag.OfficialName);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendFingerprintPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }
}
