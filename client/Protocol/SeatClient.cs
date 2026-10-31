using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AgentRisk.Client.Protocol;

/// <summary>One complete host block: its text (framing marker stripped) and whether it expects one reply line.</summary>
public sealed record BlockMessage(string Text, bool ExpectsReply);

/// <summary>Raised when the host refuses the seat claim; the message is the host's own refusal line.</summary>
public sealed class SeatRefusedException : Exception
{
    public SeatRefusedException(string message) : base(message) { }
}

/// <summary>
/// The block framing of the wire protocol, kept separate from any socket so
/// it can be exercised over plain streams: a block is every line up to and
/// excluding its framing marker line (<see cref="ProtocolConstants.EndMarker"/>
/// or <see cref="ProtocolConstants.ReplyMarker"/>).
/// </summary>
public static class BlockCodec
{
    /// <summary>
    /// Reads one block. Returns null at end of stream before any line; a
    /// block cut short by the stream's end is returned as it stands.
    /// </summary>
    public static BlockMessage? ReadBlock(TextReader reader)
    {
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line == ProtocolConstants.EndMarker)
                return new BlockMessage(string.Join("\n", lines), ExpectsReply: false);
            if (line == ProtocolConstants.ReplyMarker)
                return new BlockMessage(string.Join("\n", lines), ExpectsReply: true);
            lines.Add(line);
        }
        return lines.Count > 0 ? new BlockMessage(string.Join("\n", lines), ExpectsReply: false) : null;
    }
}

/// <summary>
/// The human seat's connection to the host. Speaks the same line protocol
/// every agent seat speaks: greet, claim a seat with one JOIN
/// line, then read blocks and answer prompts with one line each. All socket
/// work happens on one reader thread owned by this class; blocks surface
/// through <see cref="BlockReceived"/> in arrival order, so callers on a UI
/// thread only ever marshal completed blocks.
/// </summary>
public sealed class SeatClient : IDisposable
{
    private readonly TcpClient? _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _writeGate = new();
    private Thread? _readerThread;
    private volatile bool _disposed;

    private SeatClient(Stream stream, TcpClient? tcp)
    {
        _tcp = tcp;
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    /// <summary>Raised for each block after the handshake, in arrival order, on the reader thread.</summary>
    public event Action<BlockMessage>? BlockReceived;

    /// <summary>Raised once when the stream ends or fails, after which no more blocks arrive.</summary>
    public event Action? Closed;

    /// <summary>The seat this connection holds. Assigned by the handshake in <see cref="Connect"/>.</summary>
    public string SeatCode { get; internal set; } = "";

    /// <summary>The greeting block the host opened with (names the seats that were open).</summary>
    public string GreetingText { get; internal set; } = "";

    /// <summary>
    /// Connects and claims the seat. The handshake is lock-step (greeting,
    /// one JOIN line, one answer), so it runs on the caller's thread; the
    /// reader thread starts only after the seat is held.
    /// </summary>
    /// <exception cref="SeatRefusedException">The host refused the claim.</exception>
    /// <exception cref="IOException">The transport failed during the handshake.</exception>
    public static SeatClient Connect(string host, int port, string seatCode)
    {
        var tcp = new TcpClient();
        try
        {
            tcp.Connect(host, port);
            tcp.NoDelay = true; // replies are interactive; never batch them behind Nagle
            return Connect(tcp.GetStream(), seatCode, tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The handshake over an already-connected stream — the same exchange
    /// <see cref="Connect(string, int, string)"/> performs, factored out so
    /// the protocol can be exercised over in-memory streams in tests.
    /// </summary>
    public static SeatClient Connect(Stream stream, string seatCode) => Connect(stream, seatCode, tcp: null);

    private static SeatClient Connect(Stream stream, string seatCode, TcpClient? tcp)
    {
        var client = new SeatClient(stream, tcp);

        var greeting = BlockCodec.ReadBlock(client._reader)
            ?? throw new IOException("The host closed the connection before greeting.");
        client.GreetingText = greeting.Text;

        client.WriteLine($"{ProtocolConstants.JoinVerb} {seatCode}");
        var answer = BlockCodec.ReadBlock(client._reader)
            ?? throw new IOException("The host closed the connection during the handshake.");
        if (answer.Text.StartsWith(ProtocolConstants.ErrorPrefix, StringComparison.Ordinal))
            throw new SeatRefusedException(answer.Text);
        if (!answer.Text.StartsWith($"{ProtocolConstants.JoinedPrefix} ", StringComparison.Ordinal))
            throw new IOException($"Unexpected handshake answer: {answer.Text.Split('\n')[0]}");

        client.SeatCode = seatCode;
        return client;
    }

    /// <summary>Starts the reader thread; blocks arrive through <see cref="BlockReceived"/>.</summary>
    public void StartReading()
    {
        if (_readerThread is not null) throw new InvalidOperationException("Reading already started.");
        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "SeatClient reader" };
        _readerThread.Start();
    }

    /// <summary>Sends one reply line. Safe to call from any thread.</summary>
    public void SendReply(string replyLine) => WriteLine(replyLine);

    private void WriteLine(string line)
    {
        lock (_writeGate)
        {
            _writer.Write(line);
            _writer.Write('\n');
        }
    }

    private void ReadLoop()
    {
        // This method is a thread entry point: no exception may ever
        // escape it, or the runtime takes the whole process down. Every
        // failure of the stream (a reset connection, a read cut short by
        // Dispose closing the socket under the blocked read) means the
        // same thing as a clean end of stream: the table is closed, and
        // the Closed event carries it. There is deliberately no disposal
        // filter on the catch: the teardown race is exactly a failure
        // that lands after Dispose has begun.
        try
        {
            BlockMessage? block;
            while (!_disposed && (block = BlockCodec.ReadBlock(_reader)) is not null)
                BlockReceived?.Invoke(block);
        }
        catch (Exception)
        {
            // A failed stream is a closed table; the Closed event carries it.
        }
        finally
        {
            if (!_disposed)
            {
                try { Closed?.Invoke(); }
                catch (Exception) { /* a listener's failure is not the reader thread's */ }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _writer.Dispose(); } catch { /* already gone */ }
        try { _reader.Dispose(); } catch { /* already gone */ }
        _tcp?.Dispose();
    }
}
