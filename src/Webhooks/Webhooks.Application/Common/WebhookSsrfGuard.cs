using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;

namespace Haworks.Webhooks.Application.Common;

/// <summary>
/// Shared SSRF guard utilities for webhook URL validation and DNS-time rebinding checks.
/// </summary>
public static class WebhookSsrfGuard
{
    public static readonly ImmutableHashSet<string> BlockedHosts =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "localhost", "127.0.0.1", "::1", "0.0.0.0");

    /// <summary>
    /// Returns true if the IP address falls within a private, loopback, link-local,
    /// CGNAT, multicast, or otherwise non-routable range.
    /// </summary>
    public static bool IsPrivateIp(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            var b6 = ip.GetAddressBytes();
            if ((b6[0] & 0xFE) == 0xFC) return true;                         // fc00::/7 unique-local
            if (b6[0] == 0xFE && (b6[1] & 0xC0) == 0x80) return true;        // fe80::/10 link-local
            return false;
        }

        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;

        return b[0] == 10 ||                                                   // 10.0.0.0/8
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||                   // 172.16.0.0/12
               (b[0] == 192 && b[1] == 168) ||                                 // 192.168.0.0/16
               b[0] == 127 ||                                                  // 127.0.0.0/8
               (b[0] == 169 && b[1] == 254) ||                                 // 169.254.0.0/16 link-local
               (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||                  // 100.64.0.0/10 CGNAT
               b[0] == 0 ||                                                    // 0.0.0.0/8
               (b[0] == 198 && b[1] >= 18 && b[1] <= 19) ||                   // 198.18.0.0/15 benchmark
               (b[0] == 192 && b[1] == 0 && b[2] == 0) ||                     // 192.0.0.0/24 IETF protocol
               (b[0] == 192 && b[1] == 0 && b[2] == 2) ||                     // 192.0.2.0/24 TEST-NET-1
               (b[0] == 198 && b[1] == 51 && b[2] == 100) ||                  // 198.51.100.0/24 TEST-NET-2
               (b[0] == 203 && b[1] == 0 && b[2] == 113) ||                   // 203.0.113.0/24 TEST-NET-3
               b[0] >= 224;                                                    // 224.0.0.0/4+ multicast+reserved
    }

    /// <summary>
    /// Overload that accepts a string; returns false (not private) when the string is not a valid IP.
    /// Used by synchronous FluentValidation rules.
    /// </summary>
    public static bool IsPrivateIp(string host)
        => IPAddress.TryParse(host, out var ip) && IsPrivateIp(ip);

    /// <summary>
    /// Resolves <paramref name="hostname"/> via DNS and returns true if ANY A/AAAA record
    /// is a private IP address. Throws <see cref="SocketException"/> on resolution failure.
    /// </summary>
    public static async Task<bool> ResolvesToPrivateIpAsync(string hostname, CancellationToken ct = default)
    {
        var addresses = await Dns.GetHostAddressesAsync(hostname, ct);
        return addresses.Length == 0 || addresses.Any(IsPrivateIp);
    }
}
