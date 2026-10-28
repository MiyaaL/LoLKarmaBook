using System.Security.Cryptography;
using System.Text;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Desktop.Security;

/// <summary>
/// Windows user-scope DPAPI protector used for every persisted note body and
/// stable identity value. There is deliberately no plaintext or in-process
/// fallback: on a non-Windows host every operation fails loudly instead of
/// storing readable text.
///
/// Format: <c>dpapi1:</c> + Base64(ProtectedData). The prefix lets a future
/// format change be detected instead of silently mis-decoded.
/// </summary>
public sealed class DpapiTextProtector : ITextProtector
{
    public const string Prefix = "dpapi1:";

    /// <summary>Optional per-purpose entropy; keeps notes bound to this app/user.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RiftNotes.Desktop.ProfileText.v1");

    public string Algorithm => "Windows DPAPI (CurrentUser)";

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows 用户级数据保护（DPAPI）仅在 Windows 上可用；当前平台不会以明文回退保存。");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("数据不是由本机 DPAPI 保护的内容，已拒绝读取。");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows 用户级数据保护（DPAPI）仅在 Windows 上可用；无法在此平台解密本机笔记。");
        }

        byte[] protectedBytes = Convert.FromBase64String(protectedText[Prefix.Length..]);
        byte[] bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Round-trip self check shown in Settings. It never writes to disk.
    /// </summary>
    public string DescribeAvailability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "不可用（当前不是 Windows）。本机笔记将无法保存，程序不会退化为明文保存。";
        }

        try
        {
            const string probe = "lolkarmabook-dpapi-probe";
            string roundTrip = Unprotect(Protect(probe));
            return string.Equals(roundTrip, probe, StringComparison.Ordinal)
                ? "可用（当前 Windows 用户作用域）"
                : "自检失败：加密往返结果不一致";
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or FormatException)
        {
            return "自检失败：" + ex.Message;
        }
    }
}
