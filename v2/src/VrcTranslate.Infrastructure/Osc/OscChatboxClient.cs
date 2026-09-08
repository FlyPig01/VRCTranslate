using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VrcTranslate.Infrastructure.Osc;

public sealed class OscChatboxClient
{
    private readonly string _host;
    private readonly int _port;

    public OscChatboxClient(string host = "127.0.0.1", int port = 9000)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("OSC 主机不能为空。", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _host = host;
        _port = port;
    }

    public async Task SendChatboxAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        var packet = BuildPacket(message);
        using var client = new UdpClient();
        await client.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);
    }

    internal static byte[] BuildPacket(string message)
    {
        var address = OscString("/chatbox/input");
        var tags = OscString(",sTF");
        var value = OscString(message);
        var packet = new byte[address.Length + tags.Length + value.Length + 4];
        Buffer.BlockCopy(address, 0, packet, 0, address.Length);
        Buffer.BlockCopy(tags, 0, packet, address.Length, tags.Length);
        Buffer.BlockCopy(value, 0, packet, address.Length + tags.Length, value.Length);
        return packet;
    }

    private async Task<IPEndPoint> ResolveEndpointAsync(CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(_host, out var address)) return new IPEndPoint(address, _port);
        var addresses = await Dns.GetHostAddressesAsync(_host, cancellationToken).ConfigureAwait(false);
        var resolved = addresses.FirstOrDefault() ?? throw new InvalidOperationException($"无法解析 OSC 主机“{_host}”。");
        return new IPEndPoint(resolved, _port);
    }

    private static byte[] OscString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = ((bytes.Length + 1 + 3) / 4) * 4;
        var result = new byte[length];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }
}
