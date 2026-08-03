using System.Collections.Concurrent;

namespace BasicFtpServer.Core.Diagnostics;

public enum LogKind
{
    Server,
    /// <summary>A verb received from the client.</summary>
    Command,
    /// <summary>A reply sent to the client.</summary>
    Reply,
    Transfer,
    Warning,
    Error,
}

public readonly record struct LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogKind Kind,
    int SessionId,
    string Message)
{
    public override string ToString()
    {
        var scope = SessionId > 0 ? $"[{SessionId}] " : "";
        var arrow = Kind switch
        {
            LogKind.Command => "<- ",
            LogKind.Reply => "-> ",
            _ => "",
        };
        return $"{Timestamp:HH:mm:ss.fff} {scope}{arrow}{Message}";
    }
}

/// <summary>
/// Bounded in-memory log buffer. The tray attaches to this for the live session view, which
/// is how a tech sees what the copier actually said instead of guessing. Deliberately
/// separate from file logging: this survives no restarts and never touches disk.
/// </summary>
public sealed class LogRing
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;
    private long _sequence;

    public LogRing(int capacity = 2000) => _capacity = capacity;

    /// <summary>Raised on every append. Handlers must not block; the caller is a session thread.</summary>
    public event Action<LogEntry>? EntryAdded;

    public void Add(LogKind kind, string message, int sessionId = 0)
    {
        var entry = new LogEntry(Interlocked.Increment(ref _sequence), DateTimeOffset.Now, kind, sessionId, message);
        _entries.Enqueue(entry);

        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }

        try
        {
            EntryAdded?.Invoke(entry);
        }
        catch
        {
            // A misbehaving subscriber (e.g. a tray that just died) must never break a transfer.
        }
    }

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    /// <summary>
    /// Entries newer than <paramref name="afterSequence"/>. The tray polls with the highest
    /// sequence it has already seen, so the live log stays cheap regardless of buffer size.
    /// </summary>
    public IReadOnlyList<LogEntry> SnapshotSince(long afterSequence) =>
        [.. _entries.Where(e => e.Sequence > afterSequence)];

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }
}
