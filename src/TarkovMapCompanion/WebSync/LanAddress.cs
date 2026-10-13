using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TarkovMapCompanion.WebSync;

/// <summary>
/// Picks this machine's most likely LAN address, for building the URL a tablet scans.
/// </summary>
/// <remarks>
/// <para>
/// The address is only ever shown to a human: it goes into a URL and a QR code. A phone or tablet on
/// the same Wi-Fi cannot reach <c>127.0.0.1</c>, so a loopback address is worse than useless there --
/// the QR code scans, the browser opens, and nothing loads. Link-local <c>169.254.x.x</c> is the
/// address a machine gives itself when DHCP has failed; it is, by definition, not routable to
/// another device either. Both are excluded for that reason.
/// </para>
/// <para>
/// Among the remaining private addresses the preference order is 10.0.0.0/8, then 172.16.0.0/12,
/// then 192.168.0.0/16. That is a heuristic, not a guarantee: a machine with several adapters (VPN,
/// Hyper-V, Wi-Fi) has several candidates and only the user knows which one the tablet shares. When
/// more than one address falls in the winning range, the lowest address by raw bytes is chosen so
/// the URL is stable from one call to the next rather than whatever order the OS happened to
/// enumerate adapters in.
/// </para>
/// </remarks>
internal static class LanAddress
{
    /// <summary>
    /// The preferred LAN address of this machine, or null when none was found.
    /// </summary>
    /// <remarks>Convenience wrapper over <see cref="Select"/> using the real network interfaces.</remarks>
    public static IPAddress? Best() => Select(LocalIPv4());

    /// <summary>
    /// Applies the LAN preference order to an arbitrary set of addresses.
    /// </summary>
    /// <remarks>
    /// Split out from the network enumeration so it can be tested with synthetic input and no
    /// dependency on the test machine's adapter layout. Returns null for null or empty input.
    /// </remarks>
    /// <param name="addresses">Candidate addresses; converted to four-byte IPv4 before ranking.</param>
    /// <returns>The chosen address, or null when no candidate is a usable private IPv4 address.</returns>
    internal static IPAddress? Select(IEnumerable<IPAddress>? addresses)
    {
        if (addresses is null)
            return null;

        IPAddress? best = null;
        var bestRank = int.MaxValue;

        foreach (var address in addresses)
        {
            if (address is null || !IsUsable(address))
                continue;

            var rank = Rank(address);
            if (rank < 0)
                continue;

            // Same precedence range: the lower address by bytes wins, which keeps the choice
            // independent of enumeration order.
            if (rank < bestRank || (rank == bestRank && BytesAreLower(address, best)))
            {
                best = address;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>True for an IPv4 address that is neither loopback nor link-local.</summary>
    private static bool IsUsable(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (IPAddress.IsLoopback(address))
            return false;

        var bytes = address.GetAddressBytes();

        // 169.254.0.0/16: the self-assigned address a device uses when DHCP failed. Reachable from
        // the machine itself, invisible to anything else on the network.
        return bytes.Length == 4 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    /// <summary>
    /// Precedence of a private address: 0 for 10/8, 1 for 172.16/12, 2 for 192.168/16, -1 otherwise.
    /// </summary>
    private static int Rank(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return -1;

        if (bytes[0] == 10)
            return 0;

        // 172.16.0.0 through 172.31.255.255.
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return 1;

        if (bytes[0] == 192 && bytes[1] == 168)
            return 2;

        return -1;
    }

    /// <summary>True when <paramref name="candidate"/> sorts before <paramref name="current"/> by raw bytes.</summary>
    private static bool BytesAreLower(IPAddress candidate, IPAddress? current)
    {
        if (current is null)
            return true;

        var a = candidate.GetAddressBytes();
        var b = current.GetAddressBytes();
        var shared = Math.Min(a.Length, b.Length);

        for (var i = 0; i < shared; i++)
        {
            if (a[i] != b[i])
                return a[i] < b[i];
        }

        return a.Length < b.Length;
    }

    /// <summary>
    /// IPv4 addresses from every operational, non-loopback interface on this machine.
    /// </summary>
    /// <remarks>
    /// Reads adapter configuration directly rather than calling <see cref="Dns.GetHostAddresses(string)"/>:
    /// a DNS lookup can block on a slow resolver, and this is called on the UI path and in tests. The
    /// enumeration can throw on unusual adapter states, and a missing address is a display problem
    /// rather than a fatal one, so failures degrade to an empty list.
    /// </remarks>
    private static IReadOnlyList<IPAddress> LocalIPv4()
    {
        var found = new List<IPAddress>();

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                IPInterfaceProperties properties;
                try
                {
                    properties = adapter.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        found.Add(unicast.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No usable adapter information; the caller shows "address unknown".
        }
        catch (PlatformNotSupportedException)
        {
            // Network enumeration is unavailable; same outcome.
        }

        return found;
    }
}
