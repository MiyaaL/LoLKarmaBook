using System.Security.Cryptography;
using System.Text;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Client;

public enum ClientConnectionStatus { NotRunning, CredentialsUnavailable, NotLoggedIn, Connected, Disconnected, Error }

public sealed record ClientAccount(string Puuid, string DisplayName, string TagLine, string PlatformId, string ProfileKey, long SummonerId = 0)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(TagLine) ? DisplayName : DisplayName + "#" + TagLine;

    public override string ToString() => "LOL account " + ProfileKey;

    public static string CreateProfileKey(string stableAccount) =>
        "account-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableAccount)))[..24].ToLowerInvariant();
}

public sealed record ClientSnapshot(ClientConnectionStatus ConnectionStatus, string Message, ClientAccount? Account,
    MatchSession? Session, GamePhase Phase, DateTimeOffset ObservedAt, string? ClientDirectory = null)
{
    public bool IsConnected => ConnectionStatus == ClientConnectionStatus.Connected;
}

public interface ILcuClientService : IDisposable
{
    Task<ClientSnapshot> PollAsync(string? clientDirectory = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the official champion/item lists from the local client. Implemented by
/// <see cref="LcuClientService"/> and kept separate from the polling contract so
/// test fakes of <see cref="ILcuClientService"/> do not need to serve assets.
/// </summary>
public interface ILcuTagSource
{
    Task<TagSeedCatalog?> ReadTagSeedsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Transient connection material; ToString never reveals authentication data.</summary>
public sealed class LcuConnection
{
    public LcuConnection(int processId, int port, string password, string? clientDirectory = null)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024 || password.Any(char.IsControl))
            throw new ArgumentException("Invalid local client credential.", nameof(password));
        ProcessId = processId;
        Port = port;
        Password = password;
        ClientDirectory = clientDirectory;
    }
    public int ProcessId { get; }
    public int Port { get; }
    public string? ClientDirectory { get; }
    internal string Password { get; }
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{ProcessId}:{Port}:{Password}")));
    public override string ToString() => $"LCU process {ProcessId}, loopback port {Port}";
}

public sealed record LcuDiscoveryResult(ClientConnectionStatus Status, LcuConnection? Connection, string Message);

public interface ILcuConnectionDiscovery
{
    Task<LcuDiscoveryResult> DiscoverAsync(string? clientDirectory, CancellationToken cancellationToken);
}
