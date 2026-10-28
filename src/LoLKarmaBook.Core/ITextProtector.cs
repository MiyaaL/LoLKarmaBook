namespace LoLKarmaBook.Core;

/// <summary>
/// Protects sensitive text at rest. The Windows Desktop agent must supply a
/// DPAPI-backed implementation. Core/Storage never silently substitutes a
/// plaintext protector and never claims that the whole SQLite database is
/// encrypted.
/// </summary>
public interface ITextProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedText);
}
