using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core.FileTransfer;

// The LAN addresses the other device can reach, and the rendezvous JSON both sides exchange (wx-ime-sdk's LinkInfo and GrpcInfo).
public static class WeTypeFileNetwork
{
    public const int MaxAddresses = 32;

    // The default route's address first: the other device is most likely on that network, and some clients only try the
    // first address. Virtual adapters of VMware, Hyper-V and VPN clients are never advertised.
    public static IReadOnlyList<IPAddress> LanAddresses()
    {
        var physical = new List<IPAddress>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up) continue;
            if (network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            string text = (network.Name + " " + network.Description).ToLowerInvariant();
            if (VirtualAdapters.Any(text.Contains)) continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                IPAddress address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any)) continue;
                if (IsPrivateOrVirtual(address)) continue;
                if (!physical.Contains(address)) physical.Add(address);
            }
        }
        IPAddress? preferred = DefaultRouteAddress();
        if (preferred is not null)
        {
            physical.Remove(preferred);
            physical.Insert(0, preferred);
        }
        return physical.Take(MaxAddresses).ToArray();
    }

    private static readonly string[] VirtualAdapters =
        ["vethernet", "vmware", "virtualbox", "hyper-v", "wsl", "tap-", "tailscale", "zerotier", "atrust", "sangfor", "loopback"];

    // A connection attempt that is never sent: the socket's local address is the one the default route would use.
    private static IPAddress? DefaultRouteAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { return null; }
    }

    // Documents such as the one of the SDK: proxy and VPN adapters and the address ranges they hand out are not advertised.
    private static bool IsPrivateOrVirtual(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] is 0 or 127) return true;
        if (bytes[0] == 169 && bytes[1] == 254) return true;
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;
        if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return true;
        return false;
    }

    public static string LinkJson(IReadOnlyList<IPAddress> lan, string deviceName, bool preferredClient) =>
        JsonSerializer.Serialize(new LinkInfo
        {
            Lan = [.. lan.Select(address => address.ToString())],
            DeviceName = deviceName,
            PreferredClient = preferredClient,
        });

    public static string GrpcJson(string serverName, string deviceName, IReadOnlyList<IPAddress> lan, int port) =>
        JsonSerializer.Serialize(new GrpcInfo
        {
            Platform = 1,
            ServerName = serverName,
            DeviceName = deviceName,
            AvailableNets = new GrpcNets { Lan = [.. lan.Select(address => new GrpcAddress { Ip = address.ToString(), Port = port })] },
        });

    // The LAN endpoints and certificate name the joined device published. Only available_nets.lan is used: the other
    // networks (awdl, wfd, hotspot) are direct-link transports that this client does not implement, so they are ignored
    // rather than attempted. An empty answer is retried by the caller while the receiver starts listening.
    public static bool TryReadGrpc(string json, out string serverName, out IReadOnlyList<(IPAddress Address, int Port)> endpoints)
    {
        serverName = "";
        var found = new List<(IPAddress, int)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            serverName = Text(root, "server_name") ?? Text(root, "serverName") ?? "";
            if (serverName.Length > 253 || serverName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))) serverName = "";
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("available_nets", out var nets) &&
                nets.ValueKind == JsonValueKind.Object && nets.TryGetProperty("lan", out var lan))
            {
                if (lan.ValueKind == JsonValueKind.Array)
                    foreach (var entry in lan.EnumerateArray()) Collect(entry, found);
                else
                    Collect(lan, found);
            }
        }
        catch (JsonException) { }
        endpoints = [.. found.Distinct().Take(MaxAddresses)];
        return endpoints.Count > 0;
    }

    private static void Collect(JsonElement element, List<(IPAddress, int)> found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? ip = Text(element, "ip") ?? Text(element, "host") ?? Text(element, "address");
            int? port = Number(element, "port");
            if (ip is not null && port is > 0 and <= 65535 && IPAddress.TryParse(ip, out var address) &&
                !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.None) && !address.IsIPv6Multicast)
                found.Add((address, port.Value));
            foreach (var property in element.EnumerateObject()) Collect(property.Value, found);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Collect(item, found);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private sealed class LinkInfo
    {
        [JsonPropertyName("lan")] public List<string> Lan { get; set; } = [];
        [JsonPropertyName("device_name")] public string DeviceName { get; set; } = "";
        [JsonPropertyName("preferred_client")] public bool PreferredClient { get; set; }
    }

    private sealed class GrpcInfo
    {
        [JsonPropertyName("platform")] public uint Platform { get; set; }
        [JsonPropertyName("server_name")] public string ServerName { get; set; } = "";
        [JsonPropertyName("device_name")] public string DeviceName { get; set; } = "";
        [JsonPropertyName("available_nets")] public GrpcNets AvailableNets { get; set; } = new();
    }

    private sealed class GrpcNets
    {
        [JsonPropertyName("lan")] public List<GrpcAddress> Lan { get; set; } = [];
    }

    private sealed class GrpcAddress
    {
        [JsonPropertyName("ip")] public string Ip { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; }
    }
}
