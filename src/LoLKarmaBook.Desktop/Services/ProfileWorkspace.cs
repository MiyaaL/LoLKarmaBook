using LoLKarmaBook.Core;
using LoLKarmaBook.Storage;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>
/// One open profile: a real SQLite profile database (one file per space) plus the
/// <see cref="NotebookService"/> bound to it. Profiles are never shared: the
/// instance is disposed and replaced when the user switches space.
/// </summary>
public sealed class ProfileWorkspace : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _closing;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    private ProfileWorkspace(string profileId, SqliteProfileStore store, NotebookService notebook)
    {
        ProfileId = profileId;
        Store = store;
        Notebook = notebook;
    }

    public string ProfileId { get; }

    public SqliteProfileStore Store { get; }

    public NotebookService Notebook { get; }

    public string DatabasePath => Store.DatabasePath;

    public static async Task<ProfileWorkspace> OpenAsync(
        string dataRoot,
        string profileId,
        ITextProtector protector,
        CancellationToken cancellationToken,
        string? databasePath = null)
    {
        SqliteProfileStore store = databasePath is null
            ? await SqliteProfileStore.OpenAsync(dataRoot, profileId, protector, cancellationToken).ConfigureAwait(false)
            : await SqliteProfileStore.OpenDatabaseAsync(databasePath, profileId, protector, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ProfileWorkspace(profileId, store, new NotebookService(store));
    }

    /// <summary>
    /// Serialises store access from the UI thread so a backup/clear/restore is
    /// not interleaved with an in-flight save. The returned disposable releases
    /// the gate even if the operation throws. Callers must use the workspace they
    /// took the lease on, never re-read a mutable "current workspace" property.
    /// </summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_closing)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(ProfileWorkspace), "The profile workspace was closed.");
        }

        return new Releaser(_gate);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new ValueTask(_disposeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        _closing = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await Store.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        // Leave the managed semaphore alive: waiters already queued before closing
        // must wake, observe _closing, and release their turn instead of hanging.
        // No native wait handle is allocated by this class.
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
