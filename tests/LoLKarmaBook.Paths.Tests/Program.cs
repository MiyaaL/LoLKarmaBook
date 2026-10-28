using LoLKarmaBook.Desktop.Infrastructure;

var tests = new (string Name, Func<Task> Run)[]
{
    ("默认根目录位于所选安装目录，不写 C 盘用户目录", () =>
    {
        using var temp = new TestDirectory();
        string installation = temp.PathOf("chosen-install");
        AppPaths paths = AppPaths.CreateDefault(installation);
        Require(paths.Root == Path.Combine(installation, "userdata"), "默认数据未放到安装目录下。");
        Require(!Directory.Exists(paths.Root), "路径构造不应提前创建数据。");
        string explicitRoot = temp.PathOf("custom-data");
        Require(new AppPaths(explicitRoot).Root == explicitRoot, "显式数据目录被覆盖。");
        paths.EnsureDirectories();
        Require(Directory.Exists(paths.DataRoot) && Directory.Exists(paths.LogDirectory), "可写目录未正常创建。");
        Require(!Directory.EnumerateFiles(paths.Root, ".write-check-*").Any(), "写入探针未清理。");
        return Task.CompletedTask;
    }),
    ("不可写根目录显式失败，不回退到其他位置", () =>
    {
        using var temp = new TestDirectory();
        string fileRoot = temp.PathOf("not-a-directory");
        File.WriteAllText(fileRoot, "keep");
        try
        {
            new AppPaths(fileRoot).EnsureDirectories();
            throw new InvalidOperationException("无效根目录应失败。");
        }
        catch (DataDirectoryUnavailableException ex)
        {
            Require(ex.Message.Contains("D 盘", StringComparison.Ordinal), "缺少可写目录指引。");
            Require(File.ReadAllText(fileRoot) == "keep", "原文件被覆盖。");
        }
        return Task.CompletedTask;
    }),
    ("数据根目录指针可往返保存并拒绝无效路径", () =>
    {
        using var temp = new TestDirectory();
        string pointer = temp.PathOf("appdata", "location.json");
        string chosen = temp.PathOf("chosen-data");
        Require(DataRootLocation.ReadSelectedRoot(pointer) is null, "没有指针文件时应使用默认目录。");
        DataRootLocation.WriteSelectedRoot(pointer, chosen);
        Require(DataRootLocation.ReadSelectedRoot(pointer) == Path.GetFullPath(chosen), "所选数据目录未往返保存。");
        Require(File.ReadAllText(pointer).Contains("dataRoot", StringComparison.Ordinal), "指针文件格式不符合预期。");

        File.WriteAllText(pointer, "{\"schemaVersion\":1,\"dataRoot\":\"bad\\npath\"}");
        Require(DataRootLocation.ReadSelectedRoot(pointer) is null, "控制字符路径未被拒绝。");
        File.WriteAllText(pointer, "not json");
        Require(DataRootLocation.ReadSelectedRoot(pointer) is null, "损坏的指针文件应被忽略。");

        DataRootLocation.WriteSelectedRoot(pointer, chosen);
        DataRootLocation.ClearSelectedRoot(pointer);
        Require(DataRootLocation.ReadSelectedRoot(pointer) is null, "清除指针后仍返回旧目录。");
        return Task.CompletedTask;
    }),
    ("最小化到托盘默认开启，但非 Windows 关闭", () =>
    {
        var settings = new DesktopSettings();
        settings.Sanitize();
        Require(settings.MinimizeToTray == OperatingSystem.IsWindows(), "托盘偏好与平台不符。");
        return Task.CompletedTask;
    }),
    ("客户端目录偏好可保存且不接收控制字符", () =>
    {
        using var temp = new TestDirectory();
        string settingsPath = temp.PathOf("settings.json");
        var settings = new DesktopSettings { ClientDirectory = "  D:\\Game\\LeagueClient  " };
        settings.Save(settingsPath);
        Require(DesktopSettings.Load(settingsPath).ClientDirectory == "D:\\Game\\LeagueClient", "客户端目录未往返保存。");
        settings.ClientDirectory = "bad\npath";
        settings.Sanitize();
        Require(settings.ClientDirectory is null, "控制字符目录未被清除。");
        return Task.CompletedTask;
    })
}.Concat(RelocationTests.Cases()).ToArray();

int failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex.GetType().Name + ": " + ex.Message); }
}
Console.WriteLine($"路径测试：{tests.Length - failures}/{tests.Length} 通过。");
return failures == 0 ? 0 : 1;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestDirectory : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LoLKarmaBook-path-tests-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(_root);
    public string PathOf(string path, params string[] more) => Path.Combine([_root, path, .. more]);
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
