using System.Net;
using System.Net.Sockets;

namespace MatBill.Core.Security;

/// <summary>
/// Which senders may tell MatBill, through <c>X-Forwarded-For</c> / <c>-Proto</c> / <c>-Host</c>, who the client really
/// is. The headers used to be believed from ANYONE, so a caller could name any address it liked: the per-address rate
/// limits (the API, the sign-in) then counted nothing, and the scheme/host links are built from could be chosen by the
/// caller. A reverse proxy in front of a self-hosted MatBill is on the same machine, the LAN or the Docker network, so by
/// default only those (loopback, RFC 1918, link-local, IPv6 ULA) are trusted. <c>MATBILL__Server__TrustedProxies</c>
/// replaces that list (addresses or CIDR ranges, separated by commas, semicolons or line breaks); <c>any</c> trusts every
/// sender again - for a proxy on a public address that cannot be named.
/// </summary>
public static class TrustedProxies
{
    public const string AnyKeyword = "any";

    public static readonly IReadOnlyList<string> PrivateRanges =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
        "::1/128", "fc00::/7", "fe80::/10"
    ];

    /// <summary>The networks to trust, or null for "any sender". Entries that do not parse come back in
    /// <paramref name="ignored"/> - the caller logs them; they never widen the list.</summary>
    public static IReadOnlyList<IPNetwork>? Resolve(string? configured, out IReadOnlyList<string> ignored)
    {
        var tokens = string.IsNullOrWhiteSpace(configured)
            ? PrivateRanges
            : configured.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Any(token => string.Equals(token, AnyKeyword, StringComparison.OrdinalIgnoreCase) || token == "*"))
        {
            ignored = [];
            return null;
        }

        var networks = new List<IPNetwork>();
        var bad = new List<string>();
        foreach (var token in tokens)
        {
            if (IPNetwork.TryParse(token, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(token, out var address))
            {
                networks.Add(new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                bad.Add(token);
            }
        }

        ignored = bad;
        return networks;
    }
}
