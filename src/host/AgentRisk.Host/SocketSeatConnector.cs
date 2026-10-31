using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentRisk.Host;

/// <summary>
/// Accepts connections for a game: socket seats claiming their seat
/// with <c>JOIN &lt;CODE&gt;</c>, and spectators claiming the reserved
/// pseudo-seat with <c>JOIN WATCH</c>. One accept pump runs
/// for the connector's whole lifetime, from construction to disposal:
/// it performs every handshake, hands watcher connections to the
/// <see cref="SpectatorFeed"/> (they consume no seat and are never
/// offered a decision), and queues claimed seats for
/// <see cref="AcceptSeat"/> to hand out. Because the pump never stops
/// at the moment the seats fill, watchers can join before the game,
/// during play, and alongside other watchers, and a replacement seat
/// can be accepted mid-game through the same path.
///
/// Claiming is first-come within the game's configured socket seats; a
/// code that names no open seat (and no seat the host is currently
/// waiting to replace) is refused and the connection closed. A single
/// pump thread runs the handshakes one at a time, so two connections
/// can never claim the same seat, and a handshake that fails — for any
/// reason — costs that connection only, never the pump.
///
/// Handshakes are lock-step, as the protocol has always been: the host
/// greets, the client answers with its one JOIN line, and nothing else
/// is in flight. A connection that greets and then says nothing holds
/// the pump, exactly as it held the accepting host before the pump
/// existed; clients are expected to speak.
/// </summary>
public sealed class SocketSeatConnector : IDisposable
{
    private readonly TcpListener _listener;
    private readonly HashSet<string> _openSeats;
    private readonly SpectatorFeed? _spectators;

    // Seat claims the pump has completed, waiting for AcceptSeat, and
    // the codes AcceptSeat is currently waiting on as replacements (a
    // claimed seat is filled, not open, yet its replacement must be
    // allowed to claim it). Both are guarded by _gate; AcceptSeat
    // sleeps on the same monitor the pump pulses.
    private readonly object _gate = new();
    private readonly List<(SocketAgentChannel Channel, string Code)> _claimed = new();
    private readonly HashSet<string> _expectedSeats = new(StringComparer.Ordinal);
    private readonly Thread _pump;
    private bool _disposed;

    /// <param name="address">Interface to listen on (Any for the host, Loopback in tests).</param>
    /// <param name="port">Port to bind; 0 lets the OS assign one (used by tests).</param>
    /// <param name="socketSeatCodes">Seat codes this game expects over sockets.</param>
    /// <param name="spectators">
    /// The feed <c>JOIN WATCH</c> connections attach to. Without one,
    /// watching is not offered and a WATCH claim is refused like any
    /// other seat that is not open.
    /// </param>
    public SocketSeatConnector(
        IPAddress address, int port, IEnumerable<string> socketSeatCodes, SpectatorFeed? spectators = null)
    {
        _listener = new TcpListener(address, port);
        ConfigureBindOptions(_listener.Server);
        _openSeats = new HashSet<string>(socketSeatCodes, StringComparer.Ordinal);
        _spectators = spectators;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _pump = new Thread(AcceptPump) { IsBackground = true, Name = "SocketSeatConnector.AcceptPump" };
        _pump.Start();
    }

    /// <summary>
    /// Sets the bind options that give this listener its two required
    /// properties at once: a restarted host can rebind a port whose old
    /// connections sit in TIME_WAIT, and a second live listener on the
    /// same port fails loudly instead of silently splitting connections
    /// with the first. The plain managed ReuseAddress gives neither
    /// guarantee across platforms: on Linux .NET maps it to
    /// SO_REUSEADDR plus SO_REUSEPORT (the splitting hazard), and on
    /// Windows it lets a second socket bind over an active listener.
    /// </summary>
    private static void ConfigureBindOptions(Socket server)
    {
        if (OperatingSystem.IsWindows())
        {
            // The Windows answer: exclusive use. No second socket may
            // bind the port while this one listens, and once the
            // listener closes, TIME_WAIT state does not block a rebind.
            server.ExclusiveAddressUse = true;
        }
        else if (OperatingSystem.IsLinux())
        {
            // Plain SO_REUSEADDR (SOL_SOCKET 1, option 2) without
            // SO_REUSEPORT: TIME_WAIT rebinds work, a live listener
            // still owns the port outright. Set raw because the
            // managed option would add REUSEPORT back.
            server.SetRawSocketOption(1, 2, BitConverter.GetBytes(1));
        }
        else
        {
            // Other Unix: the managed option maps to SO_REUSEADDR
            // alone there, which is exactly the behaviour wanted.
            server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
    }

    /// <summary>The port actually bound (the assigned one, when 0 was requested).</summary>
    public int Port { get; }

    /// <summary>Seat codes not yet claimed by a connection.</summary>
    public IReadOnlyCollection<string> OpenSeats
    {
        get { lock (_gate) return _openSeats.ToList(); }
    }

    /// <summary>
    /// Waits for a claimed seat — <paramref name="expectedCode"/>, or
    /// any claimed seat when null — and returns its channel and code.
    /// Claims are completed by the accept pump as connections arrive
    /// (possibly before this is called); this hands them out in claim
    /// order. While waiting on a specific code, that code is claimable
    /// even though its seat is filled: that is how a replacement joins
    /// mid-game.
    /// </summary>
    public (SocketAgentChannel Channel, string SeatCode) AcceptSeat(string? expectedCode = null)
    {
        lock (_gate)
        {
            if (expectedCode is not null) _expectedSeats.Add(expectedCode);
            try
            {
                while (true)
                {
                    int at = _claimed.FindIndex(c => expectedCode is null || c.Code == expectedCode);
                    if (at >= 0)
                    {
                        var found = _claimed[at];
                        _claimed.RemoveAt(at);
                        return found;
                    }
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(SocketSeatConnector),
                            "The connector was disposed while the host waited for a seat.");
                    Monitor.Wait(_gate);
                }
            }
            finally
            {
                if (expectedCode is not null) _expectedSeats.Remove(expectedCode);
            }
        }
    }

    // ------------------------------------------------------------------
    // The accept pump: every connection the listener ever takes is
    // greeted and sorted here — seat claim to the queue, watcher to the
    // feed, anything else refused and closed.
    // ------------------------------------------------------------------

    private void AcceptPump()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                return; // the listener was stopped by Dispose; the pump's work is done
            }

            try
            {
                HandleConnection(client);
            }
            catch (Exception)
            {
                // A connection that breaks mid-handshake is closed and
                // forgotten; the pump itself must survive it.
                client.Dispose();
            }
        }
    }

    private void HandleConnection(TcpClient client)
    {
        var (outcome, code) = TryHandshake(client);
        switch (outcome)
        {
            case HandshakeOutcome.Watcher:
                _spectators!.Attach(client);
                break;

            case HandshakeOutcome.Seat:
            {
                var channel = new SocketAgentChannel(client, code!);
                try
                {
                    channel.Send($"JOINED {code}");
                }
                catch (Exception)
                {
                    // The claimant vanished before its confirmation:
                    // the seat was never taken from the open set, so
                    // close the channel and let the pump wait for the
                    // seat's real claimant.
                    channel.Dispose();
                    return;
                }
                lock (_gate)
                {
                    _openSeats.Remove(code!);
                    _expectedSeats.Remove(code!);
                    _claimed.Add((channel, code!));
                    Monitor.PulseAll(_gate);
                }
                break;
            }

            default: // Refused: the handshake already said why.
                client.Dispose();
                break;
        }
    }

    private enum HandshakeOutcome { Refused, Seat, Watcher }

    /// <summary>
    /// Runs the one-line handshake over a fresh connection. The
    /// handshake reader is deliberately not disposed (it shares the
    /// stream with whatever the connection becomes) and cannot
    /// over-read: the protocol is lock-step here, the client sends its
    /// JOIN line and then waits, so nothing exists in the stream to
    /// buffer past it.
    /// </summary>
    private (HandshakeOutcome Outcome, string? SeatCode) TryHandshake(TcpClient client)
    {
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        string watchOffer = _spectators is null
            ? ""
            : $", or {HostConstants.JoinVerb} {HostConstants.WatchSeatCode} to watch";
        string openSeats;
        lock (_gate) openSeats = string.Join(" ", _openSeats.OrderBy(c => c, StringComparer.Ordinal));
        writer.Write($"AGENT RISK | Seats open: {openSeats}. " +
                     $"Send one line: {HostConstants.JoinVerb} <SEAT CODE>{watchOffer}\n{HostConstants.EndMarker}\n");

        string? line = reader.ReadLine();
        var tokens = (line ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2 || tokens[0] != HostConstants.JoinVerb)
        {
            writer.Write($"ERROR | Expected: {HostConstants.JoinVerb} <SEAT CODE>\n{HostConstants.EndMarker}\n");
            return (HandshakeOutcome.Refused, null);
        }
        string code = tokens[1];
        if (code == HostConstants.WatchSeatCode && _spectators is not null)
            return (HandshakeOutcome.Watcher, null);

        bool claimable;
        lock (_gate) claimable = _openSeats.Contains(code) || _expectedSeats.Contains(code);
        if (!claimable)
        {
            writer.Write($"ERROR | Seat {code} is not open.\n{HostConstants.EndMarker}\n");
            return (HandshakeOutcome.Refused, null);
        }
        return (HandshakeOutcome.Seat, code);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.PulseAll(_gate); // wake any AcceptSeat waiter so it can fail honestly
        }
        _listener.Stop();
        lock (_gate)
        {
            foreach (var (channel, _) in _claimed) channel.Dispose();
            _claimed.Clear();
        }
    }
}
