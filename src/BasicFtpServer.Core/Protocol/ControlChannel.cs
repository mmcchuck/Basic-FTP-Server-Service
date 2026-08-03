using System.Text;

namespace BasicFtpServer.Core.Protocol;

/// <summary>
/// Line-oriented reader/writer for the control connection with adaptive text encoding.
///
/// Rather than relying on the client to negotiate with OPTS UTF8 ON — which older MFPs
/// never send even when they emit UTF-8, and newer ones send even when they don't — this
/// decodes strictly as UTF-8 and falls back to the configured codepage only when that
/// fails. Pure-ASCII devices, which is most of them, are unaffected either way.
/// </summary>
public sealed class ControlChannel(Stream stream, Encoding fallbackEncoding)
{
    private const int MaxLineBytes = 8192;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _lineBuffer = new byte[MaxLineBytes];
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private int _readPos;
    private int _readLen;

    /// <summary>False once a line has arrived that was not valid UTF-8.</summary>
    public bool Utf8Mode { get; private set; } = true;

    /// <summary>Reads one CRLF-terminated command. Returns null when the peer closes.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var length = 0;

        while (true)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                return length == 0 ? null : Decode(_lineBuffer.AsSpan(0, length));
            }

            var b = (byte)next;
            if (b == (byte)'\n')
            {
                // Tolerate bare LF as well as CRLF; some devices send only LF.
                if (length > 0 && _lineBuffer[length - 1] == (byte)'\r')
                {
                    length--;
                }

                return Decode(_lineBuffer.AsSpan(0, length));
            }

            if (length >= MaxLineBytes)
            {
                throw new InvalidDataException("Command line exceeded the maximum length.");
            }

            _lineBuffer[length++] = b;
        }
    }

    /// <summary>Buffered single-byte read, so a short command costs one syscall rather than one per character.</summary>
    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_readPos >= _readLen)
        {
            _readLen = await stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
            _readPos = 0;
            if (_readLen == 0)
            {
                return -1;
            }
        }

        return _readBuffer[_readPos++];
    }

    private string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Utf8Mode = false;
            return fallbackEncoding.GetString(bytes);
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var encoding = Utf8Mode ? Encoding.UTF8 : fallbackEncoding;
        var payload = encoding.GetBytes(line + "\r\n");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Called when the client explicitly negotiates UTF-8.</summary>
    public void ForceUtf8() => Utf8Mode = true;

    public Encoding CurrentEncoding => Utf8Mode ? Encoding.UTF8 : fallbackEncoding;
}
