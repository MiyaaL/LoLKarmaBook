using System.Security.Cryptography;
using System.Text;

namespace LoLKarmaBook.Core;

/// <summary>
/// EXPLICIT TEST/DEMO ONLY protector. It uses an in-memory AES-GCM key supplied by
/// the caller and provides no OS-user binding, no key storage and no protection
/// against someone who obtains the key/process. It must never be used for real
/// notes. Production Windows code must inject a DPAPI-backed ITextProtector.
/// </summary>
public sealed class SyntheticTestProtector : ITextProtector, IDisposable
{
    private const string Prefix = "stp1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly AesGcm _aes;
    private readonly byte[] _key;
    private readonly object _gate = new();
    private bool _disposed;

    private SyntheticTestProtector(byte[] key)
    {
        _key = key;
        _aes = new AesGcm(key, TagSize);
    }

    /// <summary>Always true. This type is not production data protection.</summary>
    public bool IsTestOnly => true;

    public static SyntheticTestProtector CreateForTestsOnly(ReadOnlySpan<byte> key32)
    {
        if (key32.Length != 32)
        {
            throw new ArgumentException("The test protector requires a 32-byte key.", nameof(key32));
        }

        return new SyntheticTestProtector(key32.ToArray());
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plainBytes.Length];
        byte[] tag = new byte[TagSize];

        lock (_gate)
        {
            _aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        byte[] payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);

        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown protected-text format.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedText[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Protected text is not valid base64.", ex);
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected text is truncated.");
        }

        ReadOnlySpan<byte> nonce = payload.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = payload.AsSpan(NonceSize, TagSize);
        ReadOnlySpan<byte> cipher = payload.AsSpan(NonceSize + TagSize);
        byte[] plain = new byte[cipher.Length];

        try
        {
            lock (_gate)
            {
                _aes.Decrypt(nonce, cipher, tag, plain);
            }
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException("Protected text authentication failed.", ex);
        }

        return Encoding.UTF8.GetString(plain);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_key);
    }
}
