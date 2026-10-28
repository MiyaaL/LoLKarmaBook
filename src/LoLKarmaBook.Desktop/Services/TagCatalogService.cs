using System.IO;
using LoLKarmaBook.Client;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>
/// Owns the searchable official tag catalog. It starts from the last client
/// snapshot cached on disk (or the catalog bundled with the app), and replaces it
/// with the live client lists when they can be read. A live read is best-effort:
/// no client, no login and a broken asset endpoint all simply keep the fallback.
/// </summary>
public sealed class TagCatalogService
{
    private readonly string _cachePath;
    private IReadOnlyList<TagEntry> _entries;

    public TagCatalogService(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        // dataRoot is the app's userdata root; the catalog lives in <root>/tags.
        _cachePath = Path.Combine(dataRoot, "tags", "catalog.json");
        TagSeedCatalog seeds = TagSeedCatalog.LoadFile(_cachePath);
        if (seeds.IsEmpty)
        {
            seeds = TagSeedCatalog.LoadBuiltIn();
        }

        _entries = TagCatalog.Build(seeds);
    }

    public IReadOnlyList<TagEntry> Entries => _entries;

    public bool HasLiveCatalog { get; private set; }

    public DateTimeOffset? LastUpdated { get; private set; }

    public string CachePath => _cachePath;

    public (int Champions, int Items) Counts => TagCatalog.Counts(_entries);

    public IReadOnlyList<TagEntry> Search(string? query, int limit = 8) =>
        TagSearch.Search(_entries, query, limit);

    /// <summary>Reads the client catalog; returns false when it could not be refreshed.</summary>
    public async Task<bool> RefreshAsync(ILcuTagSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        TagSeedCatalog? seeds;
        try
        {
            seeds = await source.ReadTagSeedsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }

        if (seeds is null || seeds.IsEmpty)
        {
            return false;
        }

        IReadOnlyList<TagEntry> built = TagCatalog.Build(seeds);
        if (built.Count == 0)
        {
            return false;
        }

        _entries = built;
        HasLiveCatalog = true;
        LastUpdated = DateTimeOffset.UtcNow;
        seeds.TrySaveFile(_cachePath);
        return true;
    }
}
