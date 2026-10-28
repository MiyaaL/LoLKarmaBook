using System.Security.Cryptography;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Desktop.Security;

// Test substitution only: never referenced by the shipped desktop assembly.
// This lets Linux execute real ViewModels and SQLite without claiming DPAPI coverage.
public sealed class DpapiTextProtector : ITextProtector, IDisposable
{
    private readonly SyntheticTestProtector _inner =
        SyntheticTestProtector.CreateForTestsOnly(RandomNumberGenerator.GetBytes(32));
    public string Protect(string plaintext) => _inner.Protect(plaintext);
    public Action<string>? BeforeUnprotect { get; set; }
    public string Unprotect(string protectedText)
    {
        BeforeUnprotect?.Invoke(protectedText);
        return _inner.Unprotect(protectedText);
    }
    public string DescribeAvailability() => "TEST ONLY: ephemeral AES protector; Windows DPAPI not tested";
    public void Dispose() => _inner.Dispose();
}
