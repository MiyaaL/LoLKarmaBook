namespace LoLKarmaBook.Core;

/// <summary>
/// Small, thread-safe current/previous session holder with generation safety.
/// The desktop should call <see cref="SwitchProvider"/> when the user explicitly
/// switches account/client instance; a late response from the old provider is
/// then rejected.
/// </summary>
public sealed class SessionRegistry
{
    private readonly object _gate = new();
    private MatchSession? _current;
    private MatchSession? _previous;
    private string? _activeProviderInstanceId;

    public MatchSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public MatchSession? Previous
    {
        get
        {
            lock (_gate)
            {
                return _previous;
            }
        }
    }

    public string? ActiveProviderInstanceId
    {
        get
        {
            lock (_gate)
            {
                return _activeProviderInstanceId;
            }
        }
    }

    public bool TryAccept(MatchSession session, out string? rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (_activeProviderInstanceId is not null &&
                !string.Equals(_activeProviderInstanceId, session.ProviderInstanceId, StringComparison.Ordinal))
            {
                rejectionReason =
                    "The session belongs to a different provider instance. Call SwitchProvider explicitly.";
                return false;
            }

            if (_current is not null && !session.IsNewerThan(_current))
            {
                rejectionReason = "The session is stale (generation/sequence did not advance).";
                return false;
            }

            if (_current is not null &&
                (!string.Equals(_current.LocalSessionId, session.LocalSessionId, StringComparison.Ordinal) ||
                 _current.Generation != session.Generation))
            {
                _previous = _current;
            }

            _activeProviderInstanceId = session.ProviderInstanceId;
            _current = session;
            rejectionReason = null;
            return true;
        }
    }

    /// <summary>
    /// Explicit account/client switch. Clears the current view but keeps the
    /// active provider id so late sessions from the old instance are rejected.
    /// </summary>
    public void SwitchProvider(string providerInstanceId)
    {
        if (string.IsNullOrWhiteSpace(providerInstanceId))
        {
            throw new ArgumentException("A provider instance id is required.", nameof(providerInstanceId));
        }

        lock (_gate)
        {
            _activeProviderInstanceId = providerInstanceId.Trim();
            if (_current is not null &&
                !string.Equals(_current.ProviderInstanceId, _activeProviderInstanceId, StringComparison.Ordinal))
            {
                _previous = _current;
            }

            _current = null;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _current = null;
            _previous = null;
            _activeProviderInstanceId = null;
        }
    }
}
