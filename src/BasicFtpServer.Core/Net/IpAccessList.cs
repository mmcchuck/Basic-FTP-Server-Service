using System.Net;
using System.Net.Sockets;

namespace BasicFtpServer.Core.Net;

/// <summary>
/// Optional allow-list of client addresses. Empty means allow everything.
///
/// Worth turning on wherever the copiers have fixed addresses: FTP credentials travel in
/// the clear, so limiting who may even reach the login prompt is the cheapest meaningful
/// hardening available.
/// </summary>
public sealed class IpAccessList
{
    private readonly List<(IPAddress Network, int PrefixLength)> _entries = [];

    public IpAccessList(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns ?? [])
        {
            var pattern = raw?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            var slash = pattern.IndexOf('/');
            if (slash < 0)
            {
                if (IPAddress.TryParse(pattern, out var single))
                {
                    _entries.Add((single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
                }

                continue;
            }

            if (IPAddress.TryParse(pattern[..slash], out var network) &&
                int.TryParse(pattern[(slash + 1)..], out var prefix))
            {
                _entries.Add((network, prefix));
            }
        }
    }

    public bool IsEmpty => _entries.Count == 0;

    public bool IsAllowed(IPAddress address)
    {
        if (_entries.Count == 0)
        {
            return true;
        }

        // A dual-stack listener reports IPv4 peers as ::ffff:a.b.c.d, which would never
        // match a plain IPv4 rule.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return _entries.Any(entry => Matches(address, entry.Network, entry.PrefixLength));
    }

    private static bool Matches(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();

        if (prefixLength < 0 || prefixLength > addressBytes.Length * 8)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
