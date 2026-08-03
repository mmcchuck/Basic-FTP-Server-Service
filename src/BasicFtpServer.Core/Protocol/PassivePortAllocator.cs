using System.Net;
using System.Net.Sockets;

namespace BasicFtpServer.Core.Protocol;

/// <summary>
/// Hands out listeners from the configured passive port range.
///
/// Binding is the only reliable way to claim a port: Windows can have blocks inside a range
/// reserved by Hyper-V/WSL/WinNAT that look free but fail to bind, so we try and move on
/// rather than trusting any up-front availability check.
/// </summary>
public sealed class PassivePortAllocator(int minPort, int maxPort)
{
    private readonly object _cursorLock = new();
    private int _cursor = minPort;

    public int MinPort { get; } = minPort;
    public int MaxPort { get; } = maxPort;

    public bool TryAcquire(IPAddress bindAddress, out TcpListener? listener, out int port)
    {
        listener = null;
        port = 0;

        var span = MaxPort - MinPort + 1;
        if (span <= 0)
        {
            return false;
        }

        for (var attempt = 0; attempt < span; attempt++)
        {
            int candidate;
            lock (_cursorLock)
            {
                candidate = _cursor;
                _cursor = _cursor >= MaxPort ? MinPort : _cursor + 1;
            }

            try
            {
                var trial = new TcpListener(bindAddress, candidate);
                trial.Server.ExclusiveAddressUse = true;
                trial.Start(1);
                listener = trial;
                port = ((IPEndPoint)trial.LocalEndpoint).Port;
                return true;
            }
            catch (SocketException)
            {
                // In use, or inside an OS-reserved exclusion range. Try the next one.
            }
        }

        return false;
    }

    /// <summary>
    /// Counts how many ports in the range can actually be bound. Used at startup to warn
    /// when the configured range overlaps a reserved block, which otherwise only shows up
    /// as passive transfers mysteriously failing.
    /// </summary>
    public (int Available, int Total) ProbeAvailability(IPAddress bindAddress, int sampleLimit = 64)
    {
        var total = MaxPort - MinPort + 1;
        var toCheck = Math.Min(total, sampleLimit);
        var available = 0;

        for (var i = 0; i < toCheck; i++)
        {
            try
            {
                var trial = new TcpListener(bindAddress, MinPort + i);
                trial.Server.ExclusiveAddressUse = true;
                trial.Start(1);
                trial.Stop();
                available++;
            }
            catch (SocketException)
            {
            }
        }

        return (available, toCheck);
    }
}
