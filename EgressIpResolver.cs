using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gnip;

/// <summary>
/// Resolves this host's public egress IP by asking OpenDNS for <c>myip.opendns.com</c> — a
/// special name that returns the querying client's own public IP, but only when asked directly
/// of the OpenDNS resolvers. We send a minimal DNS/UDP query to those resolvers (no third-party
/// HTTP call, no NuGet dependency) and read the A record from the answer.
/// </summary>
public static class EgressIpResolver
{
    private static readonly IPAddress[] Resolvers =
    {
        IPAddress.Parse("208.67.222.222"), // resolver1.opendns.com
        IPAddress.Parse("208.67.220.220"), // resolver2.opendns.com
    };
    private const string Name = "myip.opendns.com";

    /// <summary>Returns the public egress IPv4, or null if the lookup failed against all resolvers.</summary>
    public static async Task<IPAddress?> ResolveAsync(ushort queryId, TimeSpan timeout, CancellationToken ct)
    {
        var query = BuildAQuery(queryId, Name);
        foreach (var resolver in Resolvers)
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Connect(resolver, 53);
                await udp.SendAsync(query, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var resp = await udp.ReceiveAsync(cts.Token);

                var ip = ParseFirstA(resp.Buffer, queryId);
                if (ip is not null) return ip;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine shutdown
            }
            catch
            {
                // timeout / unreachable / malformed — try the next resolver
            }
        }
        return null;
    }

    private static byte[] BuildAQuery(ushort id, string name)
    {
        using var ms = new MemoryStream();
        void U16(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        U16(id);
        U16(0x0100); // flags: standard query, recursion desired
        U16(1);      // QDCOUNT
        U16(0);      // ANCOUNT
        U16(0);      // NSCOUNT
        U16(0);      // ARCOUNT
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0); // root label
        U16(1);          // QTYPE = A
        U16(1);          // QCLASS = IN
        return ms.ToArray();
    }

    private static IPAddress? ParseFirstA(byte[] r, ushort expectId)
    {
        if (r.Length < 12) return null;
        if (((ushort)((r[0] << 8) | r[1])) != expectId) return null;

        int qd = (r[4] << 8) | r[5];
        int an = (r[6] << 8) | r[7];
        if (an < 1) return null;

        int pos = 12;
        for (int q = 0; q < qd; q++)
        {
            pos = SkipName(r, pos);
            pos += 4; // QTYPE + QCLASS
        }
        for (int a = 0; a < an && pos + 10 <= r.Length; a++)
        {
            pos = SkipName(r, pos);
            if (pos + 10 > r.Length) break;
            int type = (r[pos] << 8) | r[pos + 1];
            int rdlen = (r[pos + 8] << 8) | r[pos + 9];
            int rdata = pos + 10;
            if (type == 1 && rdlen == 4 && rdata + 4 <= r.Length)
                return new IPAddress(new[] { r[rdata], r[rdata + 1], r[rdata + 2], r[rdata + 3] });
            pos = rdata + rdlen;
        }
        return null;
    }

    // Advance past a DNS name (label sequence), honoring a compression pointer (which always ends a name).
    private static int SkipName(byte[] r, int pos)
    {
        while (pos < r.Length)
        {
            int len = r[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2; // pointer
            pos += 1 + len;
        }
        return pos;
    }
}
