using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Infrastructure;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Storage;

internal static class RelocationTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("独立路径覆盖往返保存并兼容旧 dataRoot 字段", Overrides);
        yield return ("根目录修改包含选择后新增笔记与活动 WAL，源数据保留", RootWithLateWrites);
        yield return ("数据库设置和日志独立生效，保持空间隔离且单次提交", IndependentFiles);
        yield return ("目标后来出现文件时拒绝覆盖，旧位置和待应用计划保留", FailedCommit);
        yield return ("拒绝嵌套目录与不同空间共用同一数据库文件", InvalidTargets);
        yield return ("根目录中的独立设置和数据库目标在同一暂存树中提交", NestedTargets);
        yield return ("删除原空间目录后仍发现并重新打开外部账号数据库", ExternalProfileDiscovery);
        yield return ("隔离 Root 退出不应用另一 Root 的计划或争用其锁", ForeignPendingIsIgnored);
        yield return ("计划被另一 Root 替换后原实例不使用过期归属判断", ReplacedPendingIsIgnored);
        if (OperatingSystem.IsWindows()) yield return ("多路径发布后指针提交失败回滚副本，解除文件锁后可直接重试", RetryAfterPublishFailure);
    }

    private static Task Overrides()
    {
        using var temp = new TestDirectory();
        string pointer = temp.PathOf("pointer", "location.json");
        var selected = new PathOverrides
        {
            DataRoot = temp.PathOf("root"), SettingsFile = temp.PathOf("preferences", "desktop-settings.json"),
            LogDirectory = temp.PathOf("logs"), ProfileDatabases = new() { ["default"] = temp.PathOf("notes", "lolkarmabook.db") }
        };
        DataRootLocation.Write(pointer, selected);
        DataRootLocation.WriteSelectedRoot(pointer, temp.PathOf("changed-root"));
        var read = DataRootLocation.Read(pointer);
        Check(read.SettingsFile == selected.SettingsFile && read.ProfileDatabases["default"] == selected.ProfileDatabases["default"], "更新根目录丢失了独立覆盖。");
        AppPaths paths = AppPaths.FromOverrides(read);
        Check(paths.SettingsFile == selected.SettingsFile && paths.LogDirectory == selected.LogDirectory, "独立路径未实际解析。");
        Check(paths.GetDatabasePath("other") != paths.GetDatabasePath("default"), "不同空间共用数据库。");
        DataRootLocation.ClearSelectedRoot(pointer);
        Check(DataRootLocation.Read(pointer).DataRoot is null && DataRootLocation.Read(pointer).SettingsFile == selected.SettingsFile, "恢复根目录清掉了其他覆盖。");
        File.WriteAllText(pointer, "{\"schemaVersion\":1,\"dataRoot\":" + System.Text.Json.JsonSerializer.Serialize(selected.DataRoot) + "}");
        Check(DataRootLocation.ReadSelectedRoot(pointer) == selected.DataRoot, "旧 dataRoot 指针不兼容。");
        return Task.CompletedTask;
    }

    private static async Task RootWithLateWrites()
    {
        using var temp = new TestDirectory();
        using var protector = SyntheticTestProtector.CreateForTestsOnly(RandomNumberGenerator.GetBytes(32));
        var source = new AppPaths(temp.PathOf("source")); source.EnsureDirectories();
        string pointer = temp.PathOf("pointer", "location.json");
        await using var store = await SqliteProfileStore.OpenAsync(source.DataRoot, "default", protector);
        await Save(store, "before", "选择位置之前");
        using var plan = new PathRelocation(source, pointer);
        PathOverrides target = source.GetOverrides(); target.DataRoot = temp.PathOf("new-root");
        plan.Queue(target);
        Check(DataRootLocation.ReadSelectedRoot(pointer) is null, "复制完成前不应激活新位置。");
        await Save(store, "after", "选择位置之后");
        new DesktopSettings { DefaultReminderDays = 91 }.Save(source.SettingsFile);
        File.WriteAllText(Path.Combine(source.LogDirectory, "desktop.log"), "latest log");
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
        await writer.OpenAsync();
        await using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE path_probe(value TEXT); INSERT INTO path_probe VALUES('late-wal');";
            await command.ExecuteNonQueryAsync();
        }
        await plan.ApplyAsync();
        AppPaths next = AppPaths.FromOverrides(DataRootLocation.Read(pointer));
        await using var migrated = await SqliteProfileStore.OpenAsync(next.DataRoot, "default", protector);
        Check((await new NotebookService(migrated).ListNotesAsync()).Count == 2, "丢失选择位置后继续写入的笔记。");
        Check(DesktopSettings.Load(next.SettingsFile).DefaultReminderDays == 91, "设置不是退出前最新版本。");
        Check(File.ReadAllText(Path.Combine(next.LogDirectory, "desktop.log")) == "latest log", "日志未复制。");
        await using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = migrated.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await copy.OpenAsync(); await using var query = copy.CreateCommand(); query.CommandText = "SELECT value FROM path_probe";
        Check((string?)await query.ExecuteScalarAsync() == "late-wal", "在线快照遗漏活动 WAL。");
        Check(File.Exists(store.DatabasePath) && (await new NotebookService(store).ListNotesAsync()).Count == 2, "原数据被移动或损坏。");
        Check(!File.Exists(PathRelocation.PendingPath(pointer)), "成功后仍留下待应用记录。");
    }

    private static async Task IndependentFiles()
    {
        using var temp = new TestDirectory();
        using var protector = SyntheticTestProtector.CreateForTestsOnly(RandomNumberGenerator.GetBytes(32));
        var source = new AppPaths(temp.PathOf("source")); source.EnsureDirectories();
        string pointer = temp.PathOf("pointer", "location.json");
        await using var store = await SqliteProfileStore.OpenAsync(source.DataRoot, "default", protector);
        await using var other = await SqliteProfileStore.OpenAsync(source.DataRoot, "other", protector);
        new DesktopSettings { DefaultReminderDays = 180 }.Save(source.SettingsFile);
        File.WriteAllText(Path.Combine(source.LogDirectory, "desktop.log"), "independent log");
        using var plan = new PathRelocation(source, pointer);
        var target = source.GetOverrides();
        target.ProfileDatabases["default"] = temp.PathOf("separate-notes", "lolkarmabook.db");
        target.SettingsFile = temp.PathOf("separate-settings", "desktop-settings.json");
        target.LogDirectory = temp.PathOf("separate-logs");
        plan.Queue(target);
        await Save(store, "later", "分别放置");
        Task first = plan.ApplyAsync(), second = plan.ApplyAsync();
        Check(ReferenceEquals(first, second), "重复提交没有复用同一任务。");
        await Task.WhenAll(first, second);
        var next = AppPaths.FromOverrides(DataRootLocation.Read(pointer));
        Check(next.Root == source.Root && next.GetDatabasePath("other") == other.DatabasePath, "单个空间修改影响了其他空间或根目录。");
        await using var reopened = await ProfileWorkspace.OpenAsync(next.DataRoot, "default", protector, CancellationToken.None, next.GetDatabasePath("default"));
        Check(reopened.DatabasePath == target.ProfileDatabases["default"] && (await reopened.Notebook.ListNotesAsync()).Single().Body == "分别放置", "实际工作区没有使用覆盖数据库。");
        Check(DesktopSettings.Load(next.SettingsFile).DefaultReminderDays == 180 && File.Exists(Path.Combine(next.LogDirectory, "desktop.log")), "设置或日志覆盖未生效。");
        try
        {
            await using var wrong = await ProfileWorkspace.OpenAsync(next.DataRoot, "other", protector, CancellationToken.None, next.GetDatabasePath("default"));
            throw new Exception("打开了其他空间的数据库。");
        }
        catch (InvalidOperationException) { }
    }

    private static async Task FailedCommit()
    {
        using var temp = new TestDirectory();
        var source = new AppPaths(temp.PathOf("source")); source.EnsureDirectories();
        string pointer = temp.PathOf("pointer", "location.json");
        DataRootLocation.WriteSelectedRoot(pointer, source.Root);
        string pointerBefore = File.ReadAllText(pointer);
        File.WriteAllText(source.SettingsFile, "old settings");
        using var plan = new PathRelocation(source, pointer);
        var target = source.GetOverrides(); target.SettingsFile = temp.PathOf("new-settings", "desktop-settings.json");
        plan.Queue(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target.SettingsFile)!);
        File.WriteAllText(target.SettingsFile, "existing target");
        try { await plan.ApplyAsync(); throw new Exception("覆盖了已有目标。"); }
        catch (IOException) { }
        Check(File.ReadAllText(pointer) == pointerBefore && File.ReadAllText(target.SettingsFile) == "existing target", "失败改变了旧位置或覆盖目标。");
        Check(File.ReadAllText(source.SettingsFile) == "old settings" && File.Exists(PathRelocation.PendingPath(pointer)), "失败丢失原数据或待应用计划。");
        using var retry = new PathRelocation(source, pointer);
        Check(retry.Target.SettingsFile == target.SettingsFile, "重启后未保留待应用选择。");
    }

    private static Task InvalidTargets()
    {
        using var temp = new TestDirectory();
        var source = new AppPaths(temp.PathOf("source")); source.EnsureDirectories();
        using var plan = new PathRelocation(source, temp.PathOf("pointer", "location.json"));
        var target = source.GetOverrides(); target.DataRoot = Path.Combine(source.Root, "nested");
        try { plan.Queue(target); throw new Exception("允许嵌套根目录。"); } catch (IOException) { }
        target = source.GetOverrides();
        target.ProfileDatabases["default"] = temp.PathOf("same", "db.db"); target.ProfileDatabases["other"] = temp.PathOf("same", "db.db");
        try { plan.Queue(target); throw new Exception("允许两个空间共用文件。"); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private static async Task NestedTargets()
    {
        using var temp = new TestDirectory();
        using var protector = SyntheticTestProtector.CreateForTestsOnly(RandomNumberGenerator.GetBytes(32));
        var source = new AppPaths(temp.PathOf("source")); source.EnsureDirectories();
        new DesktopSettings { DefaultReminderDays = 90 }.Save(source.SettingsFile);
        await using var store = await SqliteProfileStore.OpenAsync(source.DataRoot, "default", protector);
        await Save(store, "nested", "嵌套目标");
        string pointer = temp.PathOf("pointer", "location.json");
        using var plan = new PathRelocation(source, pointer);
        var target = source.GetOverrides(); target.DataRoot = temp.PathOf("target");
        target.SettingsFile = Path.Combine(target.DataRoot, "preferences", "desktop-settings.json");
        target.ProfileDatabases["default"] = Path.Combine(target.DataRoot, "notes", "lolkarmabook.db");
        plan.Queue(target); await plan.ApplyAsync();
        var paths = AppPaths.FromOverrides(DataRootLocation.Read(pointer));
        await using var opened = await ProfileWorkspace.OpenAsync(paths.DataRoot, "default", protector, CancellationToken.None, paths.GetDatabasePath("default"));
        Check((await opened.Notebook.ListNotesAsync()).Single().Body == "嵌套目标", "嵌套数据库未生效。");
        Check(DesktopSettings.Load(paths.SettingsFile).DefaultReminderDays == 90, "嵌套设置未生效。");
    }

    private static async Task RetryAfterPublishFailure()
    {
        using var temp = new TestDirectory();
        using var protector = SyntheticTestProtector.CreateForTestsOnly(RandomNumberGenerator.GetBytes(32));
        var source = new AppPaths(temp.PathOf("source")); source.EnsureDirectories();
        new DesktopSettings { DefaultReminderDays = 365 }.Save(source.SettingsFile);
        File.WriteAllText(Path.Combine(source.LogDirectory, "desktop.log"), "last log");
        await using var store = await SqliteProfileStore.OpenAsync(source.DataRoot, "default", protector);
        await Save(store, "rollback", "重试保留笔记");
        string pointer = temp.PathOf("pointer", "location.json");
        DataRootLocation.WriteSelectedRoot(pointer, source.Root);
        string originalPointer = File.ReadAllText(pointer);
        using var plan = new PathRelocation(source, pointer);
        var target = source.GetOverrides(); target.DataRoot = temp.PathOf("new-root");
        target.SettingsFile = temp.PathOf("preferences", "desktop-settings.json");
        target.LogDirectory = temp.PathOf("separate-logs");
        Directory.CreateDirectory(target.DataRoot);
        plan.Queue(target);
        using (var locked = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await plan.ApplyAsync(); throw new Exception("已锁定指针仍被替换。"); }
            catch (IOException) { }
            Check(File.ReadAllText(pointer) == originalPointer, "提交失败改动旧指针。");
            Check(Directory.Exists(target.DataRoot) && !Directory.EnumerateFileSystemEntries(target.DataRoot).Any(), "已发布根目录没有回滚为空目录。");
            Check(!File.Exists(target.SettingsFile) && !Directory.Exists(target.LogDirectory), "独立设置或日志副本未回滚。");
            Check(File.Exists(PathRelocation.PendingPath(pointer)), "失败丢失待应用计划。");
        }
        await PathRelocation.ApplyPendingAsync(pointer);
        var paths = AppPaths.FromOverrides(DataRootLocation.Read(pointer));
        await using var reopened = await ProfileWorkspace.OpenAsync(paths.DataRoot, "default", protector, CancellationToken.None, paths.GetDatabasePath("default"));
        Check((await reopened.Notebook.ListNotesAsync()).Single().Body == "重试保留笔记", "解除锁后未能从原数据重试。");
        Check(!File.Exists(PathRelocation.PendingPath(pointer)) && DesktopSettings.Load(paths.SettingsFile).DefaultReminderDays == 365, "重试未完成提交。");
    }

    private static async Task ExternalProfileDiscovery()
    {
        using var temp = new TestDirectory();
        using var protector = SyntheticTestProtector.CreateForTestsOnly(RandomNumberGenerator.GetBytes(32));
        var source = new AppPaths(temp.PathOf("source")); source.EnsureDirectories();
        const string profileId = "account-0123456789abcdef01234567";
        new DesktopSettings { LastProfileId = profileId }.Save(source.SettingsFile);
        await using (var store = await SqliteProfileStore.OpenAsync(source.DataRoot, profileId, protector))
            await Save(store, "external", "原文件夹已删除仍保留");
        string pointer = temp.PathOf("pointer", "location.json");
        using var plan = new PathRelocation(source, pointer);
        var target = source.GetOverrides();
        target.ProfileDatabases[profileId] = temp.PathOf("outside", "lolkarmabook.db");
        plan.Queue(target); await plan.ApplyAsync();
        string oldProfileDirectory = ProfilePaths.GetProfileDirectory(source.DataRoot, profileId);
        ProfilePaths.EnsureUnderRoot(source.DataRoot, oldProfileDirectory, nameof(oldProfileDirectory));
        Directory.Delete(oldProfileDirectory, true);
        var next = AppPaths.FromOverrides(DataRootLocation.Read(pointer));
        string lastProfile = DesktopSettings.Load(next.SettingsFile).LastProfileId!;
        var catalog = ProfileCatalog.Discover(next.DataRoot, next.GetOverrides().ProfileDatabases);
        Check(catalog.Any(profile => profile.Id == lastProfile && profile.Title == "账号空间"), "外部账号数据库没有进入启动空间列表。");
        await using var reopened = await ProfileWorkspace.OpenAsync(next.DataRoot, lastProfile, protector, CancellationToken.None, next.GetDatabasePath(lastProfile));
        Check((await reopened.Notebook.ListNotesAsync()).Single().Body == "原文件夹已删除仍保留", "重开时没有使用外部数据库。");
    }

    private static async Task ForeignPendingIsIgnored()
    {
        using var temp = new TestDirectory();
        var sourceA = new AppPaths(temp.PathOf("source-a")); sourceA.EnsureDirectories();
        var sourceB = new AppPaths(temp.PathOf("source-b")); sourceB.EnsureDirectories();
        new DesktopSettings { DefaultReminderDays = 90 }.Save(sourceA.SettingsFile);
        string pointer = temp.PathOf("shared", "location.json");
        DataRootLocation.WriteSelectedRoot(pointer, sourceA.Root);
        string originalPointer = File.ReadAllText(pointer);
        using var ownerA = new PathRelocation(sourceA, pointer);
        var targetA = sourceA.GetOverrides(); targetA.DataRoot = temp.PathOf("target-a");
        ownerA.Queue(targetA);
        string originalPending = File.ReadAllText(PathRelocation.PendingPath(pointer));
        using (var isolatedB = new PathRelocation(sourceB, pointer))
        {
            Check(!isolatedB.HasPending && isolatedB.Target.DataRoot == sourceB.Root, "隔离实例继承了其他 Root 的计划。");
            await isolatedB.ApplyAsync(); // A still holds the shared relocation lock.
        }
        Check(!Directory.Exists(targetA.DataRoot) && File.ReadAllText(pointer) == originalPointer
            && File.ReadAllText(PathRelocation.PendingPath(pointer)) == originalPending, "隔离实例退出修改了其他 Root 的计划或指针。");
        ownerA.Dispose();
        using (var unlockedB = new PathRelocation(sourceB, pointer)) await unlockedB.ApplyAsync();
        Check(File.ReadAllText(PathRelocation.PendingPath(pointer)) == originalPending && !Directory.Exists(targetA.DataRoot), "锁解除后隔离实例仍应用了其他 Root 的计划。");
        await PathRelocation.ApplyPendingAsync(pointer);
        Check(DataRootLocation.ReadSelectedRoot(pointer) == targetA.DataRoot
            && DesktopSettings.Load(AppPaths.FromOverrides(DataRootLocation.Read(pointer)).SettingsFile).DefaultReminderDays == 90,
            "正常启动的全局恢复不再可用。");
    }

    private static async Task ReplacedPendingIsIgnored()
    {
        using var temp = new TestDirectory();
        var sourceA = new AppPaths(temp.PathOf("source-a")); sourceA.EnsureDirectories();
        var sourceB = new AppPaths(temp.PathOf("source-b")); sourceB.EnsureDirectories();
        string pointer = temp.PathOf("shared", "location.json");
        DataRootLocation.WriteSelectedRoot(pointer, sourceA.Root);
        using var originalB = new PathRelocation(sourceB, pointer);
        var targetB = sourceB.GetOverrides(); targetB.DataRoot = temp.PathOf("target-b");
        originalB.Queue(targetB);
        Check(originalB.HasPending, "未识别自身计划。");
        originalB.Dispose();
        using var replacementA = new PathRelocation(sourceA, pointer);
        var targetA = sourceA.GetOverrides(); targetA.DataRoot = temp.PathOf("target-a");
        replacementA.Queue(targetA);
        string pending = File.ReadAllText(PathRelocation.PendingPath(pointer));
        string location = File.ReadAllText(pointer);
        await originalB.ApplyAsync();
        Check(!originalB.HasPending && File.ReadAllText(pointer) == location
            && File.ReadAllText(PathRelocation.PendingPath(pointer)) == pending
            && !Directory.Exists(targetA.DataRoot) && !Directory.Exists(targetB.DataRoot), "实例使用了构造或前次检查时的过期归属。");
    }

    private static Task Save(SqliteProfileStore store, string request, string body) =>
        new NotebookService(store).SaveNoteAsync(new SyntheticScenario().MatchA, SyntheticScenario.MatchAXParticipantKey,
            new NoteDraft(request, [NoteCategory.PassivePlay], body));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
