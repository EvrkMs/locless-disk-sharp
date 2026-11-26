using System.Net;
using System.Net.Sockets;
using System.Text;
using DhcpDotNet;
using static System.Buffers.Binary.BinaryPrimitives;

namespace LoclessDisk.Server.Boot;

internal sealed class ProxyDhcp(IPAddress address, PxeOptions options, HashSet<string> allowed,
    ILogger log)
{
    private static ReadOnlySpan<byte> Cookie => [99, 130, 83, 99];

    public async Task ServeAsync(Socket socket, bool discovery, CancellationToken ct)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        var broadcast = new IPEndPoint(IPAddress.Broadcast, 68);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, any, ct);
                var reply = BuildReply(buffer.AsSpan(0, received.ReceivedBytes), discovery);
                if (reply is not null)
                    await socket.SendToAsync(reply.AsMemory(), SocketFlags.None,
                        discovery ? broadcast : received.RemoteEndPoint, ct);
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset
                or SocketError.ConnectionRefused or SocketError.MessageSize)
            {
                log.LogDebug(error, "ProxyDHCP ignored a transient UDP error");
                await Task.Delay(50, ct);
            }
        }
    }

    internal byte[]? BuildReply(ReadOnlySpan<byte> request, bool discovery)
    {
        if (request.Length < 240 || request[0] != 1 || request[1] != 1 || request[2] != 6 ||
            !request.Slice(236, 4).SequenceEqual(Cookie))
            return null;

        var packet = new DHCPv4Packet();
        if (!packet.parsePacket(request.ToArray()))
            return null;

        var received = ReadOptions(packet.dhcpOptions);
        if (received is null)
            return null;

        byte expectedType = discovery ? (byte)1 : (byte)3;
        if (!received.TryGetValue(53, out var type) || type.Length != 1 || type[0] != expectedType ||
            !received.TryGetValue(60, out var vendor) || !vendor.AsSpan().StartsWith("PXEClient"u8) ||
            !received.TryGetValue(93, out var arch) || arch.Length < 2 ||
            ReadUInt16BigEndian(arch) is not (7 or 9))
            return null;

        string mac = Convert.ToHexString(packet.chaddr);
        if (!allowed.Contains(mac))
            return null;
        bool ipxe = received.TryGetValue(77, out var userClass) && userClass.AsSpan().IndexOf("iPXE"u8) >= 0;
        string formattedMac = string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)))
            .ToLowerInvariant();
        string file = ipxe ? options.BootScriptUrl.Replace("{mac}", formattedMac) : options.BootFile;

        byte[] server = address.GetAddressBytes();
        var reply = new DHCPv4Packet
        {
            op = 2,
            htype = packet.htype,
            hlen = packet.hlen,
            xid = packet.xid,
            flags = packet.flags,
            siaddr = server,
            chaddr = packet.chaddr,
            chaddrPadding = packet.chaddrPadding
        };
        if (discovery)
            reply.flags[0] |= 0x80;
        Encoding.ASCII.GetBytes(address.ToString(), reply.sname);
        Encoding.ASCII.GetBytes(file, reply.file);

        var replyOptions = new List<byte>();
        AddOption(replyOptions, 53, [discovery ? (byte)2 : (byte)5]);
        if (ipxe)
            AddOption(replyOptions, 43, [6, 1, 8, 255]);
        AddOption(replyOptions, 54, server);
        AddOption(replyOptions, 60, "PXEClient"u8);
        if (received.TryGetValue(97, out var uuid) && uuid.Length <= 255)
            AddOption(replyOptions, 97, uuid);

        reply.dhcpOptions = replyOptions.ToArray();
        var bytes = reply.buildPacket();
        int wireLength = Math.Max(300, 240 + replyOptions.Count + 1);
        if (bytes.Length != wireLength)
            Array.Resize(ref bytes, wireLength);
        return bytes;
    }

    private static Dictionary<byte, byte[]>? ReadOptions(ReadOnlySpan<byte> options)
    {
        // DhcpDotNet 2.0.5 expects TLVs without interspersed pad bytes.
        var tlvs = new byte[options.Length + 1];
        int written = 0;
        for (int offset = 0; offset < options.Length;)
        {
            byte code = options[offset];
            if (code == 255)
                break;
            if (code == 0)
            {
                offset++;
                continue;
            }
            if (offset + 1 >= options.Length)
                return null;
            int length = options[offset + 1] + 2;
            if (length > options.Length - offset)
                return null;
            options.Slice(offset, length).CopyTo(tlvs.AsSpan(written));
            written += length;
            offset += length;
        }

        var received = new Dictionary<byte, byte[]>();
        tlvs[written++] = 255;
        foreach (var option in new DHCPv4Option().parseDhcpOptions(tlvs[..written]))
        {
            byte code = option.optionIdBytes;
            if (code is 53 or 60 or 77 or 93 or 97)
            {
                byte[] value = option.optionValue;
                received[code] = received.TryGetValue(code, out var prior) ? [.. prior, .. value] : value;
            }
        }
        return received;
    }

    private static void AddOption(List<byte> reply, byte code, ReadOnlySpan<byte> value)
    {
        var option = new DHCPv4Option
        {
            optionId = (DHCPv4OptionIds)code,
            optionLength = (byte)value.Length,
            optionValue = value.ToArray()
        };
        byte[] encoded = option.buildDhcpOption();
        // The package maps unknown option IDs to pad, including PXE option 97.
        encoded[0] = code;
        reply.AddRange(encoded);
    }
}
