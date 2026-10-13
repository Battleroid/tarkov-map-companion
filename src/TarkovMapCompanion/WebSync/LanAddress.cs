using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TarkovMapCompanion.WebSync;

/// <summary>
/// A single IPv4 address offered for selection, together with the adapter facts the ranking needs.
/// </summary>
/// <remarks>
/// The ranking is not about the address alone: whether its adapter is routed to a real network (an
/// IPv4 gateway in the same subnet) and what kind of adapter it is both matter. Carrying those
/// facts next to the address lets <see cref="LanAddress.Select(IEnumerable{LanCandidate})"/> be
/// exercised with synthetic input and no dependency on the test machine's adapter layout.
/// </remarks>
internal readonly record struct LanCandidate
{
    /// <summary>Creates a candidate from its address and the owning adapter's facts.</summary>
    /// <param name="address">The IPv4 address being ranked.</param>
    /// <param name="interfaceType">The kind of adapter that owns the address.</param>
    /// <param name="hasSameSubnetGateway">
    /// True when the adapter has an IPv4 gateway in the same subnet as <paramref name="address"/>.
    /// </param>
    /// <param name="hasAnyGateway">True when the adapter advertises any IPv4 gateway at all.</param>
    public LanCandidate(
        IPAddress address,
        NetworkInterfaceType interfaceType,
        bool hasSameSubnetGateway,
        bool hasAnyGateway)
    {
        Address = address;
        InterfaceType = interfaceType;
        HasSameSubnetGateway = hasSameSubnetGateway;
        HasAnyGateway = hasAnyGateway;
    }

    /// <summary>The IPv4 address being ranked.</summary>
    public IPAddress Address { get; }

    /// <summary>The kind of adapter that owns the address.</summary>
    public NetworkInterfaceType InterfaceType { get; }

    /// <summary>
    /// True when the owning adapter has an IPv4 gateway inside this address's subnet, so the
    /// address sits on a real routed LAN rather than an isolated virtual switch.
    /// </summary>
    public bool HasSameSubnetGateway { get; }

    /// <summary>True when the owning adapter advertises any IPv4 gateway at all.</summary>
    public bool HasAnyGateway { get; }
}

/// <summary>
/// Picks this machine's most likely LAN address, for building the URL a tablet scans.
/// </summary>
/// <remarks>
/// <para>
/// The address is only ever shown to a human: it goes into a URL and a QR code. A phone or tablet on
/// the same Wi-Fi cannot reach <c>127.0.0.1</c>, so a loopback address is worse than useless there --
/// the QR code scans, the browser opens, and nothing loads. Link-local <c>169.254.x.x</c> is the
/// address a machine gives itself when DHCP has failed; it is, by definition, not routable to
/// another device either. Both are excluded for that reason, as are non-IPv4 addresses.
/// </para>
/// <para>
/// A multi-homed machine offers several private candidates, and the private range alone cannot say
/// which one the tablet can reach: virtual switches (WSL, Hyper-V, Docker) hand out RFC1918
/// addresses too. The real discriminator is the IPv4 gateway. A routed LAN always has a gateway on
/// the same subnet as the adapter's address; an isolated virtual switch has none. Candidates are
/// therefore tiered first: a same-subnet gateway beats a gateway in a different subnet (VPN-like),
/// which beats no gateway at all. <see cref="NetworkInterfaceType"/> cannot make this distinction,
/// because .NET reports a Hyper-V vEthernet adapter as <see cref="NetworkInterfaceType.Ethernet"/>,
/// identical to a physical NIC, so the enum is not the discriminator.
/// </para>
/// <para>
/// Within a tier the adapter kind is the next tiebreak -- <see cref="NetworkInterfaceType.Wireless80211"/>
/// before <see cref="NetworkInterfaceType.Ethernet"/> before anything else -- because the tablet is
/// essentially always on Wi-Fi. Only after that does the old private range order (10.0.0.0/8, then
/// 172.16.0.0/12, then 192.168.0.0/16) apply: it is a tiebreak now, not the primary rule. The
/// lowest address by raw bytes breaks any remaining tie so the URL is stable from one call to the
/// next rather than whatever order the OS happened to enumerate adapters in.
/// </para>
/// </remarks>
internal static class LanAddress
{
    /// <summary>
    /// The preferred LAN address of this machine, or null when none was found.
    /// </summary>
    /// <remarks>Convenience wrapper over <see cref="Select(IEnumerable{LanCandidate})"/> using the real network interfaces.</remarks>
    public static IPAddress? Best() => Select(LocalCandidates());

    /// <summary>
    /// Ranks address candidates by the LAN preference rule and returns the best one.
    /// </summary>
    /// <remarks>
    /// Split out from the network enumeration so it can be tested with synthetic input and no
    /// dependency on the test machine's adapter layout. Returns null for null or empty input.
    /// </remarks>
    /// <param name="candidates">Candidates, each carrying the adapter facts used by the ranking.</param>
    /// <returns>The chosen address, or null when no candidate is a usable private IPv4 address.</returns>
    internal static IPAddress? Select(IEnumerable<LanCandidate>? candidates)
    {
        if (candidates is null)
            return null;

        LanCandidate? best = null;

        foreach (var candidate in candidates)
        {
            if (candidate.Address is null || !IsUsable(candidate.Address))
                continue;

            // Non-private addresses are not LAN candidates, exactly as before.
            if (RangeRank(candidate.Address) < 0)
                continue;

            if (best is null || IsBetter(candidate, best.Value))
                best = candidate;
        }

        return best?.Address;
    }

    /// <summary>
    /// Ranks an arbitrary set of addresses as if none of them had a gateway.
    /// </summary>
    /// <remarks>
    /// Retains the original flat input shape for callers and tests that only have addresses.
    /// Every address lands in the no-gateway tier, so the choice reduces to the old rule:
    /// interface type is unknown for all of them and the private range order, then the raw bytes,
    /// decide.
    /// </remarks>
    /// <param name="addresses">Candidate addresses; converted to four-byte IPv4 before ranking.</param>
    /// <returns>The chosen address, or null when no candidate is a usable private IPv4 address.</returns>
    internal static IPAddress? Select(IEnumerable<IPAddress>? addresses)
    {
        if (addresses is null)
            return null;

        return Select(addresses
            .Where(address => address is not null)
            .Select(address => new LanCandidate(
                address,
                NetworkInterfaceType.Unknown,
                hasSameSubnetGateway: false,
                hasAnyGateway: false)));
    }

    /// <summary>
    /// Gatesway tier of a candidate: 0 same-subnet gateway, 1 any other gateway, 2 no gateway.
    /// </summary>
    /// <param name="candidate">The candidate to classify.</param>
    /// <returns>The tier, lower being preferred.</returns>
    private static int Tier(LanCandidate candidate)
    {
        if (candidate.HasSameSubnetGateway)
            return 0;

        if (candidate.HasAnyGateway)
            return 1;

        return 2;
    }

    /// <summary>
    /// Preference between adapter kinds within a tier: Wi-Fi, then Ethernet, then everything else.
    /// </summary>
    /// <param name="type">The adapter kind.</param>
    /// <returns>The rank, lower being preferred.</returns>
    private static int TypeRank(NetworkInterfaceType type)
    {
        if (type == NetworkInterfaceType.Wireless80211)
            return 0;

        if (type == NetworkInterfaceType.Ethernet)
            return 1;

        return 2;
    }

    /// <summary>True when <paramref name="candidate"/> outranks <paramref name="current"/>.</summary>
    /// <remarks>
    /// Ordered by gateway tier, then adapter kind, then private range, then raw bytes. Every step
    /// is a total order with no reliance on enumeration order.
    /// </remarks>
    /// <param name="candidate">The candidate under consideration.</param>
    /// <param name="current">The best candidate so far.</param>
    /// <returns>True when the candidate should replace the current best.</returns>
    private static bool IsBetter(LanCandidate candidate, LanCandidate current)
    {
        var tier = Tier(candidate);
        var currentTier = Tier(current);
        if (tier != currentTier)
            return tier < currentTier;

        var typeRank = TypeRank(candidate.InterfaceType);
        var currentTypeRank = TypeRank(current.InterfaceType);
        if (typeRank != currentTypeRank)
            return typeRank < currentTypeRank;

        var rangeRank = RangeRank(candidate.Address);
        var currentRangeRank = RangeRank(current.Address);
        if (rangeRank != currentRangeRank)
            return rangeRank < currentRangeRank;

        return BytesAreLower(candidate.Address, current.Address);
    }

    /// <summary>True for an IPv4 address that is neither loopback nor link-local.</summary>
    /// <param name="address">The address to test.</param>
    /// <returns>True when the address is worth ranking.</returns>
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
    /// <remarks>
    /// This is only a tiebreak among candidates already in the same gateway tier and adapter kind;
    /// the earlier RFC1918-order-is-preference claim was wrong on multi-homed machines.
    /// </remarks>
    /// <param name="address">The address to rank.</param>
    /// <returns>The range rank, or -1 when the address is not in a private range.</returns>
    private static int RangeRank(IPAddress address)
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
    /// <param name="candidate">The address under consideration.</param>
    /// <param name="current">The best address so far, or null.</param>
    /// <returns>True when the candidate sorts lower.</returns>
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
    /// True when <paramref name="address"/> and <paramref name="gateway"/> share a subnet under
    /// <paramref name="prefixLength"/>, compared over the four IPv4 bytes.
    /// </summary>
    /// <remarks>
    /// A missing or zero prefix is treated as "not the same subnet" rather than matching
    /// everything: an adapter whose mask we cannot read must not be promoted to the top tier.
    /// </remarks>
    /// <param name="address">The adapter's IPv4 address.</param>
    /// <param name="gateway">The adapter's IPv4 gateway address.</param>
    /// <param name="prefixLength">The adapter's IPv4 prefix length, or 0 when unknown.</param>
    /// <returns>True when both fall in the same subnet.</returns>
    private static bool IsSameSubnet(IPAddress address, IPAddress gateway, int prefixLength)
    {
        if (prefixLength <= 0 || prefixLength > 32)
            return false;

        var a = address.GetAddressBytes();
        var g = gateway.GetAddressBytes();
        if (a.Length != 4 || g.Length != 4)
            return false;

        var fullBytes = prefixLength / 8;
        var remainder = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != g[i])
                return false;
        }

        if (remainder != 0)
        {
            var mask = (byte)(0xFF << (8 - remainder));
            if ((a[fullBytes] & mask) != (g[fullBytes] & mask))
                return false;
        }

        return true;
    }

    /// <summary>IPv4 gateway addresses advertised by one adapter.</summary>
    /// <param name="properties">The adapter's IP properties.</param>
    /// <returns>The IPv4 gateways, empty when the adapter has none.</returns>
    private static IReadOnlyList<IPAddress> IPv4Gateways(IPInterfaceProperties properties)
    {
        var gateways = new List<IPAddress>();

        foreach (var gateway in properties.GatewayAddresses)
        {
            var address = gateway?.Address;
            if (address is not null && address.AddressFamily == AddressFamily.InterNetwork)
                gateways.Add(address);
        }

        return gateways;
    }

    /// <summary>
    /// IPv4 address candidates from every operational, non-loopback interface on this machine.
    /// </summary>
    /// <remarks>
    /// Reads adapter configuration directly rather than calling <see cref="Dns.GetHostAddresses(string)"/>:
    /// a DNS lookup can block on a slow resolver, and this is called on the UI path and in tests. Each
    /// candidate carries the adapter kind, its gateway addresses and their prefix, which the gateway
    /// tier needs. The enumeration can throw on unusual adapter states, and a missing address is a
    /// display problem rather than a fatal one, so failures degrade to skipping the adapter or
    /// returning an empty list.
    /// </remarks>
    /// <returns>The candidates found, possibly empty.</returns>
    private static IReadOnlyList<LanCandidate> LocalCandidates()
    {
        var found = new List<LanCandidate>();

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

                try
                {
                    var gateways = IPv4Gateways(properties);

                    foreach (var unicast in properties.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        // PrefixLength is per unicast address (IPv4InterfaceProperties has none);
                        // 0 means unset and can never be a same-subnet match.
                        var prefixLength = unicast.PrefixLength;
                        var hasSameSubnetGateway = prefixLength > 0
                                                   && gateways.Any(gateway => IsSameSubnet(unicast.Address, gateway, prefixLength));

                        found.Add(new LanCandidate(
                            unicast.Address,
                            adapter.NetworkInterfaceType,
                            hasSameSubnetGateway,
                            gateways.Count > 0));
                    }
                }
                catch (NetworkInformationException)
                {
                    // This adapter's configuration is unreadable; skip it and keep the others.
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
