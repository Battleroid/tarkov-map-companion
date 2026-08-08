using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using TarkovMapCompanion.Diagnostics;

namespace TarkovMapCompanion.Party;

/// <summary>What a port-mapping attempt achieved.</summary>
public sealed record PortMapping(IPAddress ExternalAddress, int Port, bool Mapped);

/// <summary>
/// Asks the router to let the squad in, using UPnP.
/// </summary>
/// <remarks>
/// <para>
/// Hosting needs one inbound port. Without this the host would have to find their router's admin
/// page, forward a port by hand, and then separately look up their public address -- which is the
/// difference between a feature people use and one they abandon halfway through setting up.
/// </para>
/// <para>
/// Written out longhand rather than taken from a package. It is three requests: a UDP broadcast to
/// find the gateway, an HTTP GET for its service description, and a SOAP call to add the mapping.
/// The gateway also reports the external address, so a successful mapping answers "where should
/// people connect" at the same time, with no third-party lookup involved.
/// </para>
/// <para>
/// Every failure here is expected rather than exceptional: plenty of routers ship with UPnP off,
/// and carrier-grade NAT has no inbound path to offer at all. Nothing throws; the caller is told it
/// did not work and the UI explains the alternatives.
/// </para>
/// </remarks>
public sealed class PortMapper : IDisposable
{
    private static readonly string[] ServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    /// <summary>
    /// Plain-text public-address echoes, tried in order.
    /// </summary>
    /// <remarks>
    /// Only reached when UPnP could not answer. A host who has forwarded a port by hand still needs
    /// to know what address to put in the code, and without this the app would have to refuse to
    /// host on a network it could perfectly well host on -- which is the situation on the first
    /// router I tried it against.
    /// </remarks>
    private static readonly string[] AddressEchoes =
    [
        "https://checkip.amazonaws.com",
        "https://api.ipify.org",
        "https://icanhazip.com",
    ];

    private string? _controlUrl;
    private string? _serviceType;
    private int _mappedPort;

    /// <summary>
    /// Maps <paramref name="port"/> to this machine, and reports the address peers should use.
    /// </summary>
    public async Task<PortMapping?> MapAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            var description = await DiscoverAsync(cancellationToken).ConfigureAwait(false);

            if (description is not null
                && await LoadServiceAsync(description, cancellationToken).ConfigureAwait(false)
                && LocalAddressFor(description) is { } local)
            {
                var added = await AddMappingAsync(port, local, cancellationToken).ConfigureAwait(false);
                var external = await ExternalAddressAsync(cancellationToken).ConfigureAwait(false);

                if (added && external is not null)
                {
                    _mappedPort = port;
                    return new PortMapping(external, port, Mapped: true);
                }
            }

            Log.Info("UPnP could not open a port; falling back to looking up the public address");

            // Not a failure yet. Plenty of people have a port forwarded already, or are willing to
            // add one; all they are missing is the address to hand out.
            var echoed = await EchoedAddressAsync(cancellationToken).ConfigureAwait(false);

            return echoed is null ? null : new PortMapping(echoed, port, Mapped: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"UPnP failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Finds the gateway's description URL over SSDP.</summary>
    private static async Task<Uri?> DiscoverAsync(CancellationToken cancellationToken)
    {
        const string Request =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        var payload = Encoding.ASCII.GetBytes(Request);

        // Routers drop the occasional discovery packet, and asking twice is cheaper than failing.
        await socket.SendAsync(payload, payload.Length, target).ConfigureAwait(false);
        await socket.SendAsync(payload, payload.Length, target).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var response = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(response.Buffer);

                foreach (var line in text.Split("\r\n"))
                {
                    if (!line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var location))
                        return location;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // No gateway answered in time, which is a normal outcome.
        }

        return null;
    }

    /// <summary>Reads the gateway's description and remembers where to send commands.</summary>
    private async Task<bool> LoadServiceAsync(Uri description, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var xml = XDocument.Parse(await http.GetStringAsync(description, cancellationToken).ConfigureAwait(false));
        var ns = xml.Root?.GetDefaultNamespace() ?? XNamespace.None;

        foreach (var service in xml.Descendants(ns + "service"))
        {
            var type = service.Element(ns + "serviceType")?.Value;
            var control = service.Element(ns + "controlURL")?.Value;

            if (type is null || control is null || !ServiceTypes.Contains(type))
                continue;

            _serviceType = type;
            _controlUrl = new Uri(description, control).ToString();

            return true;
        }

        return false;
    }

    /// <summary>Which of this machine's addresses the router can actually reach.</summary>
    private static IPAddress? LocalAddressFor(Uri gateway)
    {
        try
        {
            // Connecting a UDP socket sends nothing, but it makes the OS pick the route -- and
            // therefore the source address -- it would use to reach the gateway. More reliable
            // than guessing from the interface list on a machine with a VPN or Hyper-V adapter.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(gateway.Host, gateway.Port == -1 ? 80 : gateway.Port);

            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task<bool> AddMappingAsync(int port, IPAddress local, CancellationToken cancellationToken)
    {
        var body =
            $"<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            $"<NewProtocol>TCP</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort>" +
            $"<NewInternalClient>{local}</NewInternalClient>" +
            $"<NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>Tarkov Map Companion</NewPortMappingDescription>" +
            $"<NewLeaseDuration>0</NewLeaseDuration>";

        var response = await SoapAsync("AddPortMapping", body, cancellationToken).ConfigureAwait(false);
        return response is not null;
    }

    private async Task<IPAddress?> ExternalAddressAsync(CancellationToken cancellationToken)
    {
        var response = await SoapAsync("GetExternalIPAddress", "", cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        var value = XDocument.Parse(response)
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;

        return IPAddress.TryParse(value, out var address) && !Equals(address, IPAddress.Any)
            ? address
            : null;
    }

    /// <summary>Asks a public echo what address the internet sees us as.</summary>
    private static async Task<IPAddress?> EchoedAddressAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        foreach (var url in AddressEchoes)
        {
            try
            {
                var text = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

                if (IPAddress.TryParse(text.Trim(), out var address)
                    && address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Try the next one.
            }
        }

        return null;
    }

    private async Task<string?> SoapAsync(string action, string body, CancellationToken cancellationToken)
    {
        if (_controlUrl is null || _serviceType is null)
            return null;

        var envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:{action} xmlns:u=\"{_serviceType}\">{body}</u:{action}>" +
            "</s:Body></s:Envelope>";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");

        content.Headers.Add("SOAPACTION", $"\"{_serviceType}#{action}\"");

        try
        {
            var response = await http.PostAsync(_controlUrl, content, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes the mapping back down. Leaving it behind would quietly hold a port open on the
    /// router long after the app has closed.
    /// </summary>
    public void Dispose()
    {
        if (_mappedPort == 0)
            return;

        var port = _mappedPort;
        _mappedPort = 0;

        try
        {
            SoapAsync(
                    "DeletePortMapping",
                    $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn($"could not remove the UPnP mapping for port {port}: {ex.Message}");
        }
    }
}
