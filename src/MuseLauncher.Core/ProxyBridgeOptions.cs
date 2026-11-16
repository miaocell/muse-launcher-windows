using System.Security.Cryptography.X509Certificates;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MuseLauncher.Core.Tests")]

namespace MuseLauncher.Core;

/// <summary>Internal only: production always uses default platform certificate validation.</summary>
internal sealed class ProxyBridgeOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int HeaderLimit { get; init; } = 16 * 1024;
    public int MaxConnections { get; init; } = 64;
    public X509Certificate2Collection? TrustedRoots { get; init; }

    public void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero || HeaderTimeout <= TimeSpan.Zero || HeaderLimit is < 1024 or > 32768
            || MaxConnections is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(ProxyBridgeOptions));
    }
}
