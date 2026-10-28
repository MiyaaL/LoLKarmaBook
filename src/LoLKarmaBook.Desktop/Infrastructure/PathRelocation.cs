using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using LoLKarmaBook.Storage;

namespace LoLKarmaBook.Desktop.Infrastructure;

/// <summary>Current-version location changes. Source data is never removed.</summary>
public sealed class PathRelocation : IDisposable
{
    private readonly string _pointer;
    private readonly AppPaths _source;
    private FileStream? _lock;
    private Task? _applyTask;
    public PathOverrides Target { get; private set; }
    public bool HasPending => ReadOwnedPending() is not null;

    public PathRelocation(AppPaths source, string pointer)
    {
        _source = source;
        _pointer = pointer;
        Target = source.GetOverrides();
        if (ReadOwnedPending() is { } pending)
        {
            pending.Target.Sanitize();
            Target = pending.Target;
        }
    }

    public void Queue(PathOverrides target)
    {
        target = target.Clone();
        target.Sanitize();
        Validate(_source, AppPaths.FromOverrides(target));
        _lock ??= AcquireLock(_pointer);
        WritePending(_pointer, new Request { Source = _source.GetOverrides(), Target = target });
        Target = target;
    }

    public Task ApplyAsync() => _applyTask ??= ApplyOwnedAsync();

    private async Task ApplyOwnedAsync()
    {
        Dispose();
        // Do not contend for another source root's held lock merely because
        // this instance shares the global pointer (for example --data-root QA).
        if (!HasPending) return;
        try { await ApplyCoreAsync(_pointer, _source.Root).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or SqliteException or InvalidOperationException or ArgumentException)
        {
            // A different root can replace the request between the preliminary
            // ownership check and lock acquisition. Its plan is not ours.
            if (!HasPending) return;
            throw new IOException("位置修改未完成，原数据与位置记录保持不变。", ex);
        }
    }

    private Request? ReadOwnedPending()
    {
        try
        {
            using var stream = new FileStream(PendingPath(_pointer), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Request? request = JsonSerializer.Deserialize<Request>(stream);
            return request?.Target is not null && BelongsToRoot(request, _source.Root) ? request : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool BelongsToRoot(Request request, string root) =>
        DataRootLocation.Normalize(request.Source?.DataRoot) is { } sourceRoot && Same(sourceRoot, root);

    public void Dispose() { _lock?.Dispose(); _lock = null; }

    public static string PendingPath(string pointer) => pointer + ".pending.json";

    public static async Task ApplyPendingAsync(string pointer)
    {
        try { await ApplyCoreAsync(pointer).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or SqliteException or InvalidOperationException or ArgumentException)
        {
            throw new IOException("位置修改未完成，原数据与位置记录保持不变。", ex);
        }
    }

    private static async Task ApplyCoreAsync(string pointer, string? expectedSourceRoot = null)
    {
        string pending = PendingPath(pointer);
        if (!File.Exists(pending)) return;
        using FileStream held = AcquireLock(pointer);
        Request request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(pending).ConfigureAwait(false))
            ?? throw new IOException("位置记录无法读取，原位置保持不变。");
        // This check is intentionally repeated while holding the shared lock,
        // before validation, copies, pointer updates or pending-file cleanup.
        if (expectedSourceRoot is not null && !BelongsToRoot(request, expectedSourceRoot)) return;
        if (request.Source is null || request.Target is null)
            throw new IOException("位置记录无效，原位置保持不变。");
        request.Source.Sanitize(); request.Target.Sanitize();
        if (request.Source.DataRoot is null || request.Target.DataRoot is null)
            throw new IOException("位置记录无效，原位置保持不变。");
        if (Equivalent(DataRootLocation.Read(pointer), request.Target))
        {
            TryDeletePending(pending);
            return;
        }
        AppPaths source = AppPaths.FromOverrides(request.Source);
        AppPaths target = AppPaths.FromOverrides(request.Target);
        Validate(source, target);
        var changes = new List<Change>();
        if (!Same(source.Root, target.Root)) changes.Add(new(source.Root, target.Root, true));
        if (!Same(source.SettingsFile, target.SettingsFile) && !IsDerived(source.Root, source.SettingsFile, target.Root, target.SettingsFile))
            changes.Add(new(source.SettingsFile, target.SettingsFile, false));
        if (!Same(source.LogDirectory, target.LogDirectory) && !IsDerived(source.Root, source.LogDirectory, target.Root, target.LogDirectory))
            changes.Add(new(source.LogDirectory, target.LogDirectory, true));
        foreach (var pair in request.Target.ProfileDatabases)
        {
            string old = source.GetDatabasePath(pair.Key);
            if (Same(old, pair.Value) || IsDerived(source.Root, old, target.Root, pair.Value)) continue;
            changes.Add(new(old, pair.Value, false, pair.Key));
        }
        var staged = new List<Change>();
        bool committed = false;
        try
        {
            // Stage every location before publishing any of them. Nested target
            // locations are folded into their containing staged directory.
            foreach (Change change in changes.OrderBy(c => c.Target.Length))
            {
                Change? container = staged.FirstOrDefault(c => c.IsDirectory && IsWithin(c.Target, change.Target));
                if (container is not null)
                {
                    change.Stage = Path.Combine(container.Stage, Path.GetRelativePath(container.Target, change.Target));
                    ProfilePaths.EnsureUnderRoot(container.Stage, change.Stage, nameof(change.Stage));
                    if (Directory.Exists(change.Stage) || File.Exists(change.Stage)) RejectLink(change.Stage);
                    if (Directory.Exists(change.Stage)) Directory.Delete(change.Stage, true);
                    else if (File.Exists(change.Stage)) File.Delete(change.Stage);
                }
                else
                {
                    string parent = Path.GetDirectoryName(change.Target)!;
                    Directory.CreateDirectory(parent);
                    change.Stage = Path.Combine(parent, ".s-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");
                    RequireNewFile(change.Stage);
                    change.TargetWasEmptyDirectory = Directory.Exists(change.Target);
                    staged.Add(change);
                }
                if (change.IsDirectory) await CopyTreeAsync(change.Source, change.Stage, pointer).ConfigureAwait(false);
                else await CopyFileAsync(change.Source, change.Stage, change.ProfileId).ConfigureAwait(false);
            }
            foreach (Change change in staged)
            {
                change.Fingerprint = Fingerprint(change.Stage, change.IsDirectory);
                if (change.IsDirectory) RequireEmpty(change.Target); else RequireNewFile(change.Target);
            }
            foreach (Change change in staged)
            {
                if (change.IsDirectory)
                {
                    if (Directory.Exists(change.Target)) Directory.Delete(change.Target, false);
                    Directory.Move(change.Stage, change.Target);
                }
                else File.Move(change.Stage, change.Target, false);
                change.Published = true;
            }
            DataRootLocation.Write(pointer, request.Target);
            committed = true;
            TryDeletePending(pending);
        }
        finally
        {
            foreach (Change change in staged.AsEnumerable().Reverse())
            {
                try
                {
                if (!committed && change.Published)
                {
                    // Only remove the exact clone created here, and only while
                    // its complete file/directory manifest is still unchanged.
                    if (Fingerprint(change.Target, change.IsDirectory) == change.Fingerprint)
                    {
                        RemoveOwned(change.Target, change.IsDirectory);
                        if (change.TargetWasEmptyDirectory) Directory.CreateDirectory(change.Target);
                    }
                }
                if (Directory.Exists(change.Stage)) RemoveOwned(change.Stage, true);
                else if (File.Exists(change.Stage)) RemoveOwned(change.Stage, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A changed/locked target is retained. Keep the pending plan
                    // and continue rolling back the other independent targets.
                }
            }
        }
    }

    public static void Validate(AppPaths source, AppPaths target)
    {
        if (!Same(source.Root, target.Root))
        {
            RejectOverlap(source.Root, target.Root);
            RequireEmpty(target.Root);
        }
        if (!Same(source.SettingsFile, target.SettingsFile) && !IsDerived(source.Root, source.SettingsFile, target.Root, target.SettingsFile))
            RequireNewFile(target.SettingsFile);
        if (!Same(source.LogDirectory, target.LogDirectory) && !IsDerived(source.Root, source.LogDirectory, target.Root, target.LogDirectory))
        {
            RejectOverlap(source.LogDirectory, target.LogDirectory);
            RequireEmpty(target.LogDirectory);
        }
        foreach (var pair in target.GetOverrides().ProfileDatabases)
        {
            if (!Same(source.GetDatabasePath(pair.Key), pair.Value) && !IsDerived(source.Root, source.GetDatabasePath(pair.Key), target.Root, pair.Value))
                RequireNewFile(pair.Value);
        }
        string[] databases = target.GetOverrides().ProfileDatabases.Values.ToArray();
        if (databases.Distinct(PathComparer).Count() != databases.Length || databases.Any(path => Same(path, target.SettingsFile)))
            throw new IOException("不同空间和设置文件不能使用同一个文件。");
    }

    private static async Task CopyTreeAsync(string source, string target, string pointer)
    {
        RejectLink(source);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string name = Path.GetFileName(file);
            if (Same(file, pointer) || Same(file, PendingPath(pointer)) || Same(file, pointer + ".lock")) continue;
            if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            string? profileId = name == ProfilePaths.DatabaseFileName
                && Path.GetFileName(Path.GetDirectoryName(source)) == ProfilePaths.ProfilesDirectoryName ? Path.GetFileName(source) : null;
            await CopyFileAsync(file, Path.Combine(target, name), profileId).ConfigureAwait(false);
        }
        foreach (string child in Directory.EnumerateDirectories(source))
            if (!child.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                await CopyTreeAsync(child, Path.Combine(target, Path.GetFileName(child)), pointer).ConfigureAwait(false);
    }

    private static async Task CopyFileAsync(string source, string target, string? profileId = null)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("原文件无法读取，位置未修改。", source);
        RejectLink(source);
        RequireNewFile(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".f-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        RequireNewFile(temporary);
        try
        {
            if (profileId is not null)
                await SqliteProfileMaintenance.BackupAsync(source, temporary, profileId).ConfigureAwait(false);
            else if (IsSqlite(source))
            {
                await using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString());
                await from.OpenAsync().ConfigureAwait(false); await to.OpenAsync().ConfigureAwait(false);
                from.BackupDatabase(to);
            }
            else File.Copy(source, temporary, overwrite: false);
            if (IsSqlite(temporary))
            {
                await using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                await database.OpenAsync().ConfigureAwait(false);
                await using var command = database.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally
        {
            foreach (string path in new[] { temporary, temporary + "-wal", temporary + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static bool IsSqlite(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        return stream.ReadAtLeast(header, 16, false) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static void RequireEmpty(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("目标位置已有文件，请选择空文件夹。");
    }
    private static void RequireNewFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("目标文件已存在，未覆盖任何数据。");
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("位置修改不跟随链接目录或文件。");
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool Same(string a, string b) => PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)));
    private static bool IsDerived(string oldRoot, string old, string newRoot, string target) => !Same(oldRoot, newRoot)
        && !Path.GetRelativePath(oldRoot, old).StartsWith("..", StringComparison.Ordinal)
        && Same(Path.Combine(newRoot, Path.GetRelativePath(oldRoot, old)), target);
    private static void RejectOverlap(string source, string target)
    {
        if (IsWithin(source, target) || IsWithin(target, source)) throw new IOException("新旧目录不能互相包含。");
    }
    private static bool IsWithin(string root, string path) => Same(root, path) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Fingerprint(string path, bool directory)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Visit(string current, string relative)
        {
            RejectLink(current);
            foreach (string file in Directory.EnumerateFiles(current))
            {
                RejectLink(file);
                using var stream = File.OpenRead(file);
                entries["F:" + Path.Combine(relative, Path.GetFileName(file))] = Convert.ToHexString(SHA256.HashData(stream));
            }
            foreach (string child in Directory.EnumerateDirectories(current))
            {
                string key = Path.Combine(relative, Path.GetFileName(child));
                entries["D:" + key] = "";
                Visit(child, key);
            }
        }
        if (directory) Visit(path, "");
        else { RejectLink(path); using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        return JsonSerializer.Serialize(entries);
    }
    private static void RemoveOwned(string path, bool directory)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        ProfilePaths.EnsureUnderRoot(parent, path, nameof(path));
        RejectLink(path);
        if (directory) Directory.Delete(path, true); else File.Delete(path);
    }
    private static void TryDeletePending(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private sealed class Change(string source, string target, bool directory, string? profileId = null)
    {
        public string Source { get; } = source;
        public string Target { get; } = target;
        public bool IsDirectory { get; } = directory;
        public string? ProfileId { get; } = profileId;
        public string Stage { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public bool Published { get; set; }
        public bool TargetWasEmptyDirectory { get; set; }
    }
    private static FileStream AcquireLock(string pointer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        return new FileStream(pointer + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static void WritePending(string pointer, Request request)
    {
        string path = PendingPath(pointer), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(request)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool Equivalent(PathOverrides a, PathOverrides b) => a.DataRoot == b.DataRoot && a.SettingsFile == b.SettingsFile
        && a.LogDirectory == b.LogDirectory && a.ProfileDatabases.Count == b.ProfileDatabases.Count
        && a.ProfileDatabases.All(pair => b.ProfileDatabases.TryGetValue(pair.Key, out string? path) && Same(pair.Value, path));
    public sealed class Request
    {
        public PathOverrides Source { get; set; } = new();
        public PathOverrides Target { get; set; } = new();
    }
}
