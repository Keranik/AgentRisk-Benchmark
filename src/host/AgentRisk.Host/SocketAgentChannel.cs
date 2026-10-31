using System.Net.Sockets;
using System.Text;

namespace AgentRisk.Host;

/// <summary>
/// A seat reached over a plain TCP socket, line-based and telnet-style
/// (the MUD convention): prompts go out as text blocks and the
/// seat answers with one line. Framing is the transport's business: every
/// block ends with <see cref="HostConstants.EndMarker"/>, or with
/// <see cref="HostConstants.ReplyMarker"/> when a reply line is expected,
/// so a client always knows when a block is complete.
///
/// Replies are read through a small buffered line reader of this class's
/// own, not a StreamReader, for one reason: the stale-reply rule.
/// A seat can answer twice — most often after a host poke re-sends
/// its prompt — and the duplicate line must never answer the NEXT
/// decision. Before each prompt goes out, <see cref="Ask"/> harvests
/// every complete line already sitting in the receive buffer without
/// blocking and sets it aside as stale; the session records the discard
/// in the transcript (see <see cref="TakeStaleReplies"/>). Only complete
/// lines are drained: a partial line is not a reply anyone sent, and it
/// stays buffered to complete whatever line it belongs to.
///
/// The channel also knows when its seat has gone: a read that reaches
/// the end of the stream, or any transport failure, sets
/// <see cref="Disconnected"/> at once, so host telemetry can show a
/// dropped seat while its decision is still pending instead of finding
/// out at the next write.
/// </summary>
public sealed class SocketAgentChannel : IAgentChannel, IDisposable
{
    /// <summary>Read chunk size for the buffered line reader; a one-off buffer size, not a protocol value.</summary>
    private const int ReadBufferBytes = 4096;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly StreamWriter _writer;
    private readonly object _writeGate = new();

    // The buffered line reader: bytes not yet split into lines wait in
    // _partial; complete lines wait in _lines. Reads and drains happen
    // on the session thread (inside Ask); writes may also come from a
    // host thread (a poke), so writes take _writeGate.
    private readonly byte[] _readBuffer = new byte[ReadBufferBytes];
    private readonly List<byte> _partial = new();
    private readonly Queue<string> _lines = new();
    private readonly List<string> _staleReplies = new();
    private bool _eof;
    private volatile bool _disconnected;
    private bool _disposed;

    public SocketAgentChannel(TcpClient client, string seatCode)
    {
        _client = client;
        _client.NoDelay = true; // prompts are interactive; never batch them behind Nagle
        _stream = client.GetStream();
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        DisplayName = $"socket seat {seatCode} from {client.Client.RemoteEndPoint}";
    }

    public string DisplayName { get; }

    /// <summary>
    /// True once the seat's stream has ended or failed, as observed by a
    /// read or a write. Set the moment the channel knows, including while
    /// a decision is pending on the seat.
    /// </summary>
    public bool Disconnected => _disconnected;

    public void Send(string text)
    {
        try
        {
            WriteBlock(text, HostConstants.EndMarker);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _disconnected = true;
            throw new ChannelDisconnectedException($"Socket write failed: {ex.Message}", ex);
        }
    }

    public string? Ask(string promptText)
    {
        try
        {
            // The stale-reply rule, applied at the only moment it can
            // bite: any complete reply line already buffered belongs to
            // an earlier decision and is set aside before this prompt
            // goes out, so the line this Ask reads is the answer to
            // THIS prompt and no other.
            _staleReplies.AddRange(DrainBufferedLines());
            WriteBlock(promptText, HostConstants.ReplyMarker);
            string? reply = ReadLine();
            if (reply is null) _disconnected = true;
            return reply;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _disconnected = true;
            throw new ChannelDisconnectedException($"Socket exchange failed: {ex.Message}", ex);
        }
    }

    public void Resend(string promptText)
    {
        try
        {
            WriteBlock(promptText, HostConstants.ReplyMarker);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _disconnected = true;
            throw new ChannelDisconnectedException($"Socket poke failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The stale reply lines set aside by <see cref="Ask"/> since the
    /// last call, in arrival order; the list is cleared by taking it.
    /// The session calls this after each exchange and records every
    /// line in the transcript. Session-thread only, like Ask itself.
    /// </summary>
    public IReadOnlyList<string> TakeStaleReplies()
    {
        if (_staleReplies.Count == 0) return Array.Empty<string>();
        var taken = _staleReplies.ToArray();
        _staleReplies.Clear();
        return taken;
    }

    // ------------------------------------------------------------------
    // Writing: one lock, because a poke may write from the host thread
    // while the session thread is sending to the same seat.
    // ------------------------------------------------------------------

    private void WriteBlock(string text, string marker)
    {
        lock (_writeGate)
        {
            _writer.Write(text);
            if (!text.EndsWith('\n')) _writer.Write('\n');
            _writer.Write(marker);
            _writer.Write('\n');
        }
    }

    // ------------------------------------------------------------------
    // The buffered line reader
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the next complete line (terminator stripped), blocking
    /// until one arrives; null when the stream has ended with nothing
    /// left buffered. A final unterminated line is returned once, as a
    /// stream reader would.
    /// </summary>
    private string? ReadLine()
    {
        while (_lines.Count == 0)
        {
            if (_eof)
            {
                if (_partial.Count == 0) return null;
                string last = DecodePartial();
                _partial.Clear();
                return last;
            }
            int read = _stream.Read(_readBuffer, 0, _readBuffer.Length);
            if (read == 0)
            {
                _eof = true;
                _disconnected = true;
            }
            else
            {
                AppendBytes(read);
            }
        }
        return _lines.Dequeue();
    }

    /// <summary>
    /// Harvests every complete line already received, without ever
    /// blocking: reads only while the stream reports data available.
    /// Used by the stale-reply drain before a prompt goes out.
    /// </summary>
    private List<string> DrainBufferedLines()
    {
        if (!_eof)
        {
            while (_stream.DataAvailable)
            {
                int read = _stream.Read(_readBuffer, 0, _readBuffer.Length);
                if (read == 0)
                {
                    _eof = true;
                    _disconnected = true;
                    break;
                }
                AppendBytes(read);
            }
        }
        var drained = new List<string>(_lines.Count);
        while (_lines.Count > 0) drained.Add(_lines.Dequeue());
        return drained;
    }

    private void AppendBytes(int count)
    {
        // Splitting on the '\n' byte is UTF-8 safe: 0x0A never appears
        // inside a multi-byte UTF-8 sequence, so lines are decoded whole.
        for (int i = 0; i < count; i++)
        {
            byte b = _readBuffer[i];
            if (b == (byte)'\n')
            {
                _lines.Enqueue(DecodePartial());
                _partial.Clear();
            }
            else
            {
                _partial.Add(b);
            }
        }
    }

    private string DecodePartial()
    {
        string line = Encoding.UTF8.GetString(_partial.ToArray());
        return line.EndsWith('\r') ? line[..^1] : line;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
        _client.Dispose();
    }
}
