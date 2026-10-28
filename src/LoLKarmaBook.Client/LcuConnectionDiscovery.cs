using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LoLKarmaBook.Client;

public sealed partial class LcuConnectionDiscovery : ILcuConnectionDiscovery
{
    public Task<LcuDiscoveryResult> DiscoverAsync(string? clientDirectory, CancellationToken cancellationToken) =>
        Task.Run(() => Discover(clientDirectory, cancellationToken), cancellationToken);

    private static LcuDiscoveryResult Discover(string? clientDirectory, CancellationToken cancellationToken)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDirectory(directories, clientDirectory);
        bool running = false;
        foreach (string name in new[] { "LeagueClientUx", "LeagueClient" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    running = true;
                    string? path = TryGetExecutablePath(process.Id);
                    if (path is not null) AddDirectory(directories, Path.GetDirectoryName(path));
                    string? commandLine = TryGetCommandLine(process.Id);
                    if (commandLine is not null && TryParseCommandLine(commandLine, process.Id, path is null ? clientDirectory : Path.GetDirectoryName(path), out var connection))
                        return Found(connection!);
                }
            }
        }
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            AddDirectory(directories, Path.Combine(drive.RootDirectory.FullName, "Riot Games", "League of Legends"));
            AddDirectory(directories, Path.Combine(drive.RootDirectory.FullName, "WeGameApps", "英雄联盟", "LeagueClient"));
        }
        foreach (string directory in directories)
        {
            foreach (string candidate in new[] { Path.Combine(directory, "lockfile"), Path.Combine(directory, "LeagueClient", "lockfile") })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > 16 * 1024) continue;
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    if (TryParseLockfile(reader.ReadToEnd(), Path.GetDirectoryName(candidate), out var connection)) return Found(connection!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        return running
            ? new(ClientConnectionStatus.CredentialsUnavailable, null, "检测到 LOL 客户端，但未能读取连接参数。请确认两者权限一致，或在设置中选择客户端目录。")
            : new(ClientConnectionStatus.NotRunning, null, "等待 LOL 客户端启动。");
    }

    private static LcuDiscoveryResult Found(LcuConnection connection) => new(ClientConnectionStatus.Connected, connection, "已找到本机 LOL 客户端。");

    private static void AddDirectory(HashSet<string> directories, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
            directories.Add(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    public static bool TryParseLockfile(string text, string? clientDirectory, out LcuConnection? connection)
    {
        connection = null;
        string[] parts = text.Trim().Split(':');
        if (parts.Length != 5 || !parts[0].StartsWith("LeagueClient", StringComparison.OrdinalIgnoreCase) || parts[4] != "https"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int port)) return false;
        try { connection = new LcuConnection(pid, port, parts[3], clientDirectory); return true; }
        catch (ArgumentException) { return false; }
    }

    public static bool TryParseCommandLine(string commandLine, int processId, string? clientDirectory, out LcuConnection? connection)
    {
        connection = null;
        Match portMatch = PortPattern().Match(commandLine);
        Match tokenMatch = TokenPattern().Match(commandLine);
        if (!portMatch.Success || !tokenMatch.Success || !int.TryParse(portMatch.Groups[1].Value, out int port)) return false;
        try { connection = new LcuConnection(processId, port, tokenMatch.Groups[1].Value, clientDirectory); return true; }
        catch (ArgumentException) { return false; }
    }

    [GeneratedRegex("--app-port(?:=|\\s+)\"?(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();
    [GeneratedRegex("--remoting-auth-token(?:=|\\s+)\"?([^\"\\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    private static string? TryGetExecutablePath(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            int length = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Queries the operating-system command-line metadata, without reading process memory.</summary>
    private static string? TryGetCommandLine(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            _ = NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out int length);
            if (length is <= 0 or > 131072) return null;
            IntPtr memory = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, 60, memory, length, out _) < 0) return null;
                UnicodeString value = Marshal.PtrToStructure<UnicodeString>(memory);
                long offset = value.Buffer.ToInt64() - memory.ToInt64();
                if (value.Length % 2 != 0 || offset < 0 || offset + value.Length > length) return null;
                return Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder path, ref int length);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int informationClass, IntPtr information, int informationLength, out int returnLength);
}
