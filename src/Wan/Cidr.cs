using System.Net;
using System.Net.Sockets;

namespace Gnip;

/// <summary>
/// Parses an IPv4 address or CIDR ("a.b.c.d" = /32, or "a.b.c.d/n") and tests membership.
/// Used to map a detected public egress IP to a configured WAN line.
/// </summary>
public sealed class Cidr
{
    private readonly uint _network;
    private readonly uint _mask;

    /// <summary>The original text this was parsed from.</summary>
    public string Text { get; }

    private Cidr(uint network, uint mask, string text)
    {
        _network = network;
        _mask = mask;
        Text = text;
    }

    public static bool TryParse(string? s, out Cidr? cidr)
    {
        cidr = null;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var text = s.Trim();
        var parts = text.Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        int bits = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > 32))
            return false;
        if (parts.Length > 2) return false;

        uint mask = bits == 0 ? 0u : 0xFFFFFFFFu << (32 - bits);
        cidr = new Cidr(ToUint(ip) & mask, mask, text);
        return true;
    }

    public bool Contains(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        return (ToUint(ip) & _mask) == _network;
    }

    private static uint ToUint(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
