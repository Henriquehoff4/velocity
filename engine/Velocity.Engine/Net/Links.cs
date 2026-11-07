using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Velocity.Engine.Net;

public sealed record NetLink
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Kind { get; init; } = "other";
    public IPAddress? LocalV4 { get; init; }
    public IPAddress? LocalV6 { get; init; }
    public int IfIndexV4 { get; init; }
    public int IfIndexV6 { get; init; }
    public string Gateway { get; init; } = "";
    public string Subnet { get; init; } = "";
    public long NominalBps { get; init; }
    public string Group { get; init; } = "";
    public bool Up { get; init; } = true;
    public bool Virtual { get; init; }
}

public static class NetworkMonitor
{
    public static IPAddress[]? TestAddresses { get; set; }

    public static List<NetLink> Enumerate()
    {
        if (TestAddresses is { } test)
        {
            var list = new List<NetLink>();
            for (var i = 0; i < test.Length; i++)
                list.Add(new NetLink { Id = $"test-{i}", Name = $"Teste {i + 1} ({test[i]})", Kind = "test", LocalV4 = test[i], Gateway = $"test-gw-{i}", Subnet = test[i].ToString(), Virtual = true });
            return AssignGroups(list);
        }
        var links = new List<NetLink>();
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return links;
        }
        foreach (var nic in all)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            var gateway = properties.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any));
            if (gateway == null) continue;
            IPAddress? v4 = null, v6 = null;
            var subnet = "";
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && v4 == null)
                {
                    v4 = unicast.Address;
                    subnet = NetworkOf(unicast.Address, unicast.IPv4Mask);
                }
                else if (unicast.Address.AddressFamily == AddressFamily.InterNetworkV6 && v6 == null && !unicast.Address.IsIPv6LinkLocal && !unicast.Address.IsIPv6Teredo)
                {
                    v6 = unicast.Address;
                }
            }
            if (v4 == null && v6 == null) continue;
            var ipv4Index = 0;
            var ipv6Index = 0;
            try
            {
                ipv4Index = properties.GetIPv4Properties()?.Index ?? 0;
            }
            catch (NetworkInformationException)
            {
            }
            try
            {
                ipv6Index = properties.GetIPv6Properties()?.Index ?? 0;
            }
            catch (NetworkInformationException)
            {
            }
            links.Add(new NetLink
            {
                Id = nic.Id,
                Name = nic.Name,
                Kind = KindOf(nic),
                LocalV4 = v4,
                LocalV6 = v6,
                IfIndexV4 = ipv4Index,
                IfIndexV6 = ipv6Index,
                Gateway = gateway.ToString(),
                Subnet = subnet,
                NominalBps = nic.Speed > 0 ? nic.Speed / 8 : 0,
                Virtual = IsVirtual(nic),
            });
        }
        return AssignGroups(links);
    }

    public static List<NetLink> AssignGroups(List<NetLink> links)
    {
        var groups = new Dictionary<string, string>();
        var result = new List<NetLink>(links.Count);
        foreach (var link in links.OrderByDescending(l => l.NominalBps))
        {
            var key = link.Gateway.Length > 0 ? $"{link.Subnet}|{link.Gateway}" : link.Id;
            if (!groups.TryGetValue(key, out var group))
            {
                group = $"g{groups.Count + 1}";
                groups[key] = group;
            }
            result.Add(link with { Group = group });
        }
        return result;
    }

    public static List<NetLink> SelectForDownload(IReadOnlyList<NetLink> links, IReadOnlyCollection<string>? allow, bool forceShared)
    {
        var usable = links.Where(l => l.Up && (allow == null || allow.Count == 0 || allow.Contains(l.Id))).ToList();
        if (forceShared) return usable;
        var chosen = new List<NetLink>();
        foreach (var group in usable.GroupBy(l => l.Group))
            chosen.Add(group.OrderByDescending(l => l.NominalBps).ThenBy(l => l.Name, StringComparer.Ordinal).First());
        return chosen;
    }

    public static long TryReceivedBytes(string id)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (nic.Id == id) return nic.GetIPStatistics().BytesReceived;
        }
        catch (NetworkInformationException)
        {
        }
        return -1;
    }

    private static string KindOf(NetworkInterface nic) => nic.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Wireless80211 => "wifi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => IsVirtual(nic) ? "virtual" : "ethernet",
        NetworkInterfaceType.Ppp => "vpn",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "mobile",
        _ => "other",
    };

    private static bool IsVirtual(NetworkInterface nic)
    {
        var text = $"{nic.Description} {nic.Name}";
        return text.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VPN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP", StringComparison.Ordinal)
            || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase);
    }

    private static string NetworkOf(IPAddress address, IPAddress? mask)
    {
        if (mask == null) return address.ToString();
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < a.Length; i++) a[i] &= m[i];
        return new IPAddress(a).ToString();
    }
}

public sealed class HttpPool : IDisposable
{
    private const int IpUnicastIf = 31;
    private readonly object _gate = new();
    private readonly Dictionary<string, HttpMessageInvoker> _clients = new();

    public HttpMessageInvoker Get(NetLink? link)
    {
        var key = link == null || !IsBound(link) ? "" : link.Id;
        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var existing)) return existing;
            var client = new HttpMessageInvoker(Build(key.Length == 0 ? null : link), true);
            _clients[key] = client;
            return client;
        }
    }

    public void Forget(string linkId)
    {
        lock (_gate)
        {
            if (_clients.Remove(linkId, out var client)) client.Dispose();
        }
    }

    private static bool IsBound(NetLink link) => link.LocalV4 != null || link.LocalV6 != null;

    private static SocketsHttpHandler Build(NetLink? link)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = int.MaxValue,
            ResponseDrainTimeout = TimeSpan.FromMilliseconds(200),
            MaxResponseDrainSize = 64 * 1024,
            UseProxy = true,
            ActivityHeadersPropagator = null,
        };
        if (link != null) handler.ConnectCallback = (context, ct) => ConnectBoundAsync(link, context, ct);
        return handler;
    }

    private static async ValueTask<Stream> ConnectBoundAsync(NetLink link, SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var local = address.AddressFamily == AddressFamily.InterNetworkV6 ? link.LocalV6 : link.LocalV4;
            if (local == null) continue;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                ApplyInterface(socket, address.AddressFamily, link);
                socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, true);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = e;
                if (ct.IsCancellationRequested) throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.AddressFamilyNotSupported);
    }

    public static void ApplyInterface(Socket socket, AddressFamily family, NetLink link)
    {
        if (family == AddressFamily.InterNetwork && link.IfIndexV4 > 0)
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, IPAddress.HostToNetworkOrder(link.IfIndexV4));
        else if (family == AddressFamily.InterNetworkV6 && link.IfIndexV6 > 0)
            socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)IpUnicastIf, link.IfIndexV6);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }
}
