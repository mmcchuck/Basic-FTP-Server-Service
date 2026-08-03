using System.Net;
using System.Net.Sockets;

namespace BasicFtpServer.Core.Protocol;

public enum DataMode
{
    None,
    Passive,
    Active,
}

/// <summary>
/// Holds the pending data connection for a session. Both directions are mandatory: copiers
/// are split between active and passive, and plenty of devices offer no choice at all.
/// </summary>
public sealed class DataChannel : IDisposable
{
    private TcpListener? _passiveListener;
    private IPEndPoint? _activeTarget;

    public DataMode Mode { get; private set; }

    /// <summary>The endpoint advertised to the client in the 227/229 reply.</summary>
    public IPEndPoint? AdvertisedEndpoint { get; private set; }

    public void SetPassive(TcpListener listener, IPEndPoint advertised)
    {
        Reset();
        _passiveListener = listener;
        AdvertisedEndpoint = advertised;
        Mode = DataMode.Passive;
    }

    public void SetActive(IPEndPoint target)
    {
        Reset();
        _activeTarget = target;
        Mode = DataMode.Active;
    }

    /// <summary>
    /// Produces the connected data stream. In passive mode this accepts the connection the
    /// client already initiated; in active mode the server dials out to the client.
    /// </summary>
    public async Task<TcpClient> OpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        switch (Mode)
        {
            case DataMode.Passive:
                if (_passiveListener is null)
                {
                    throw new InvalidOperationException("No passive listener is pending.");
                }

                return await _passiveListener.AcceptTcpClientAsync(timeoutCts.Token).ConfigureAwait(false);

            case DataMode.Active:
                if (_activeTarget is null)
                {
                    throw new InvalidOperationException("No active target was set.");
                }

                var client = new TcpClient(_activeTarget.AddressFamily);
                try
                {
                    await client.ConnectAsync(_activeTarget, timeoutCts.Token).ConfigureAwait(false);
                    return client;
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

            default:
                throw new InvalidOperationException("Use PORT or PASV first.");
        }
    }

    /// <summary>Releases the pending connection. A data setup is single-use per transfer.</summary>
    public void Reset()
    {
        try
        {
            _passiveListener?.Stop();
        }
        catch
        {
            // Already torn down.
        }

        _passiveListener = null;
        _activeTarget = null;
        AdvertisedEndpoint = null;
        Mode = DataMode.None;
    }

    public void Dispose() => Reset();
}
