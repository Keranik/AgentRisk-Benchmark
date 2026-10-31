using System.Net.Sockets;
using System.Text;

namespace AgentRisk.Host;

/// <summary>
/// The spectator feed: every watcher attached here receives
/// the game's transcript entries live, in the exact text the session
/// writes beside the game record (<see cref="GameTranscriptWriter"/>),
/// framed as blocks ending with <see cref="HostConstants.EndMarker"/>
/// so a watcher parses the feed exactly as a seat parses its blocks.
/// On attach, a watcher is confirmed (<c>JOINED WATCH</c>) and sent the
/// most recent board entry, so it can render the table immediately;
/// after that it receives every entry as the game produces it, plus a
/// fresh board entry after every applied decision.
///
/// A watcher sees everything the host sees, including every seat's
/// cards in hand (each seat's prompts carry its own hand). Watching is
/// a position of trust; hosts should share the address accordingly.
///
/// Watchers are observers only: nothing flows back from a watcher into
/// the game, a watcher that disconnects or fails mid-stream is dropped
/// silently, and publishing never throws into the session — a watcher
/// can cost the feed a reader, never the game a beat.
/// </summary>
public sealed class SpectatorFeed : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Watcher> _watchers = new();
    private readonly Action? _onWatcherJoined;
    private string? _latestBoardEntry;
    private bool _disposed;

    /// <param name="onWatcherJoined">
    /// Optional host-side notice, invoked once per attached watcher
    /// (the console host prints it; tests may ignore it).
    /// </param>
    public SpectatorFeed(Action? onWatcherJoined = null) => _onWatcherJoined = onWatcherJoined;

    /// <summary>How many watchers are currently attached.</summary>
    public int WatcherCount
    {
        get { lock (_gate) return _watchers.Count; }
    }

    /// <summary>
    /// Attaches a connected client as a watcher: confirms the join and
    /// sends the current board entry (when one is known), then registers
    /// the connection for live entries. The whole attach runs under the
    /// feed's lock, so a live entry can never overtake the board
    /// snapshot a new watcher starts from. A client whose stream fails
    /// during the attach is closed and never registered.
    /// </summary>
    public void Attach(TcpClient client)
    {
        client.NoDelay = true; // the feed is a live view; never batch it behind Nagle
        lock (_gate)
        {
            if (_disposed)
            {
                client.Dispose();
                return;
            }
            var watcher = new Watcher(client);
            try
            {
                watcher.SendBlock($"JOINED {HostConstants.WatchSeatCode}");
                if (_latestBoardEntry is not null)
                    watcher.SendEntry(_latestBoardEntry);
            }
            catch (Exception)
            {
                watcher.Dispose();
                return;
            }
            _watchers.Add(watcher);
        }
        _onWatcherJoined?.Invoke();
    }

    /// <summary>
    /// Records the board block a watcher who connects before the game
    /// starts should see (the setup state), without publishing anything:
    /// nobody is watching yet is exactly the case, and a watcher who
    /// attaches afterwards picks the block up in <see cref="Attach"/>.
    /// Live board updates go through <see cref="PublishBoard"/>.
    /// </summary>
    public void SeedBoardBlock(string boardBlockText)
    {
        lock (_gate) _latestBoardEntry = GameTranscriptWriter.BoardEntry(boardBlockText);
    }

    /// <summary>
    /// Publishes a fresh board block: it becomes the board a late joiner
    /// starts from and goes out live to every current watcher as a
    /// <c>>>> BOARD</c> entry.
    /// </summary>
    public void PublishBoard(string boardBlockText)
    {
        string entry = GameTranscriptWriter.BoardEntry(boardBlockText);
        lock (_gate)
        {
            _latestBoardEntry = entry;
            PublishLocked(entry);
        }
    }

    /// <summary>Publishes one transcript entry text to every watcher.</summary>
    public void Publish(string entryText)
    {
        lock (_gate) PublishLocked(entryText);
    }

    private void PublishLocked(string entryText)
    {
        if (_disposed) return;
        for (int i = _watchers.Count - 1; i >= 0; i--)
        {
            try
            {
                _watchers[i].SendEntry(entryText);
            }
            catch (Exception)
            {
                // Dropped silently, by design: a dead watcher is removed
                // and the feed (and the game behind it) carries on.
                _watchers[i].Dispose();
                _watchers.RemoveAt(i);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
    }

    /// <summary>One watcher's stream. Entries are written whole, each closed by the EndMarker line.</summary>
    private sealed class Watcher : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamWriter _writer;

        public Watcher(TcpClient client)
        {
            _client = client;
            _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };
        }

        public void SendBlock(string text)
        {
            _writer.Write(text);
            if (!text.EndsWith('\n')) _writer.Write('\n');
            _writer.Write(HostConstants.EndMarker);
            _writer.Write('\n');
        }

        public void SendEntry(string entryText) => SendBlock(entryText.TrimEnd('\n'));

        public void Dispose()
        {
            _writer.Dispose();
            _client.Dispose();
        }
    }
}
