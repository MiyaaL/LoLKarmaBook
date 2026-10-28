using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LoLKarmaBook.Hexdata;

/// <summary>
/// Opens the Hexdata page in a dedicated Chrome/Edge profile via the DevTools
/// protocol, lets the user sign in there once, then reads the site's own session
/// values from the page. Nothing is stored except the profile and the session the
/// app itself needs.
/// </summary>
public sealed class HexdataBrowserLogin : IDisposable
{
    private const string LoginUrl = "https://hexdata.com.cn/matchmaking-position";

    private readonly string _profileDirectory;
    private Process? _browser;

    public HexdataBrowserLogin(string profileDirectory) => _profileDirectory = profileDirectory;

    /// <summary>Launches the browser, waits for a signed-in page, and returns candidate tokens.</summary>
    public async Task<IReadOnlyList<string>> SignInAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        string? executable = FindBrowser();
        if (executable is null)
        {
            throw new HexdataException("没有找到 Chrome 或 Edge，无法完成登录。", 0, "browser_missing");
        }

        Directory.CreateDirectory(_profileDirectory);
        string portFile = Path.Combine(_profileDirectory, "DevToolsActivePort");
        try
        {
            if (File.Exists(portFile))
            {
                File.Delete(portFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _browser = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            Arguments = "--remote-debugging-port=0"
                        + " --user-data-dir=\"" + _profileDirectory + "\""
                        + " --no-first-run --no-default-browser-check"
                        + " --new-window \"" + LoginUrl + "\""
        }) ?? throw new HexdataException("无法启动浏览器。", 0, "browser_failed");

        int port = await WaitForPortAsync(portFile, timeout, cancellationToken).ConfigureAwait(false);
        string? pageSocket = await FindPageSocketAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        if (pageSocket is null)
        {
            throw new HexdataException("浏览器已启动，但没找到 Hexdata 页面。", 0, "page_missing");
        }

        IReadOnlyList<string> candidates = await ReadSessionValuesAsync(pageSocket, timeout, cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new HexdataException("还没有读到登录状态，请先在打开的窗口里完成登录，再回来重试。", 0, "not_signed_in");
        }

        return candidates;
    }

    private int _port;

    private const int DebugPort = 9222;

    /// <summary>
    /// Starts the dedicated browser window, or reuses the one already running on the
    /// fixed debug port (so a profile left logged in keeps working after a restart).
    /// </summary>
    public async Task LaunchAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (await IsPortAliveAsync(DebugPort, cancellationToken).ConfigureAwait(false))
        {
            _port = DebugPort;
            return;
        }

        string? executable = FindBrowser();
        if (executable is null)
        {
            throw new HexdataException("没有找到 Chrome 或 Edge，无法完成登录。", 0, "browser_missing");
        }

        Directory.CreateDirectory(_profileDirectory);
        _browser = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            Arguments = "--remote-debugging-port=" + DebugPort
                        + " --user-data-dir=\"" + _profileDirectory + "\""
                        + " --no-first-run --no-default-browser-check"
                        + " --new-window \"" + LoginUrl + "\""
        }) ?? throw new HexdataException("无法启动浏览器。", 0, "browser_failed");

        _port = DebugPort;
        await WaitForPortAsync(_port, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsPortAliveAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string version = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancellationToken)
                .ConfigureAwait(false);
            return version.Contains("webSocketDebuggerUrl", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsPortAliveAsync(port, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        throw new HexdataException(
            "浏览器窗口没有就绪。请关闭上一次打开的 Hexdata 登录窗口再试；若一直失败，可能是安全软件拦住了本地调试端口。",
            0, "devtools_timeout");
    }
    /// <summary>Reads the session values from the already opened page.</summary>
    public async Task<IReadOnlyList<string>> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_port <= 0)
        {
            throw new HexdataException("浏览器还没有启动。", 0, "browser_missing");
        }

        string? pageSocket = await FindPageSocketAsync(_port, timeout, cancellationToken).ConfigureAwait(false);
        if (pageSocket is null)
        {
            throw new HexdataException("没有找到 Hexdata 页面，请确认登录窗口已打开。", 0, "page_missing");
        }

        return await ReadSessionValuesAsync(pageSocket, timeout, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Closes the browser we launched (only this instance).</summary>
    public void Dispose()
    {
        try
        {
            if (_browser is { HasExited: false })
            {
                _browser.Kill(entireProcessTree: true);
                _browser.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            _browser?.Dispose();
            _browser = null;
        }
    }

    private static string? FindBrowser()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ];
        string[] relative =
        [
            @"Google\Chrome\Application\chrome.exe",
            @"Microsoft\Edge\Application\msedge.exe"
        ];
        foreach (string relativePath in relative)
        {
            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                string candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static async Task<int> WaitForPortAsync(string portFile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(portFile))
                {
                    string[] lines = await File.ReadAllLinesAsync(portFile, cancellationToken).ConfigureAwait(false);
                    if (lines.Length > 0 && int.TryParse(lines[0], out int port) && port > 0)
                    {
                        return port;
                    }
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        throw new HexdataException("浏览器调试端口没有就绪，请确认本机 Chrome/Edge 可以启动。", 0, "devtools_timeout");
    }

    private static async Task<string?> FindPageSocketAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken)
                    .ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(json);
                foreach (JsonElement target in document.RootElement.EnumerateArray())
                {
                    if (HexdataReport.Text(target, "type") == "page"
                        && HexdataReport.Text(target, "url") is { } url
                        && url.Contains("hexdata.com.cn", StringComparison.OrdinalIgnoreCase))
                    {
                        return HexdataReport.Text(target, "webSocketDebuggerUrl");
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> ReadSessionValuesAsync(string socketUrl, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await socket.ConnectAsync(new Uri(socketUrl), deadline.Token).ConfigureAwait(false);

        const string expression =
            "JSON.stringify((() => { const o = {}; try { for (let i = 0; i < localStorage.length; i++) { const k = localStorage.key(i); " +
            "o['l:' + k] = localStorage.getItem(k); } } catch (e) {} try { for (let i = 0; i < sessionStorage.length; i++) { " +
            "const k = sessionStorage.key(i); o['s:' + k] = sessionStorage.getItem(k); } } catch (e) {} return o; })())";

        string request = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true }
        });
        await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, deadline.Token)
            .ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, deadline.Token).ConfigureAwait(false);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        using JsonDocument response = JsonDocument.Parse(message.ToArray());
        if (!response.RootElement.TryGetProperty("result", out JsonElement resultElement)
            || !resultElement.TryGetProperty("result", out JsonElement value)
            || !value.TryGetProperty("value", out JsonElement text)
            || text.ValueKind != JsonValueKind.String)
        {
            return Array.Empty<string>();
        }

        string payload = text.GetString() ?? "{}";
        var values = new List<string>();
        using JsonDocument document = JsonDocument.Parse(payload);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                string? candidate = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length is >= 16 and <= 4096)
                {
                    values.Add(candidate);
                }
            }
        }

        return values.Distinct(StringComparer.Ordinal).Take(60).ToArray();
    }
}
