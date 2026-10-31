using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AgentRisk.Client.Protocol;
using AgentRisk.Engine;
using AgentRisk.Host;
using BoardModel = AgentRisk.Board.Board;

namespace AgentRisk.Client.App;

/// <summary>
/// A game hosted by this app, in-process, through the shared host
/// library: the session, connector, and spectator feed, assembled here
/// and driven from the table screen. Socket
/// seats (Client and Agent plans) are accepted on a background thread;
/// the session starts when every seat is filled and runs on its own
/// thread. The seat played on this screen talks through a
/// <see cref="UiSeatChannel"/>; stand-ins are the library's scripted
/// policy.
///
/// The app's own seat panel reads the table through a watch connection
/// to this host's listener, the same feed any watcher gets (see
/// TableScreen) — hosting changes nothing about how the table is read.
/// </summary>
public sealed class HostedTable : IDisposable
{
    private readonly BoardModel _board;
    private readonly Action<string> _notify;
    private readonly Action? _watcherJoined;
    private readonly GameConfig _config;
    private readonly SessionConfig _sessionConfig;
    private readonly Dictionary<string, IAgentChannel> _channels = new(StringComparer.Ordinal);
    private readonly List<string> _socketSeatCodes = new();
    private readonly SpectatorFeed _feed;
    private readonly SocketSeatConnector? _connector;
    private readonly ResumePlan? _resumePlan;
    private readonly IReadOnlyDictionary<string, IAgentChannel> _liveChannels;

    private Thread? _acceptThread;
    private int _started;
    private bool _disposed;

    private HostedTable(
        BoardModel board,
        GameConfig config,
        SessionConfig sessionConfig,
        IReadOnlyList<SeatPlan> seatPlans,
        ResumePlan? resumePlan,
        Action<string> notify,
        Action? watcherJoined)
    {
        _board = board;
        _config = config;
        _sessionConfig = sessionConfig;
        SeatPlans = seatPlans;
        _resumePlan = resumePlan;
        _notify = notify;
        _watcherJoined = watcherJoined;

        var policy = new ScriptedSeatPolicy(BoardProjectBoardData.FromBoard(board));
        var live = new Dictionary<string, IAgentChannel>(StringComparer.Ordinal);
        foreach (var plan in seatPlans)
        {
            switch (plan.Kind)
            {
                case SeatPlanKind.ThisScreen:
                    var channel = new UiSeatChannel(plan.Color);
                    LocalChannel = channel;
                    LocalSeatCode = plan.Color;
                    live[plan.Color] = channel;
                    _channels[plan.Color] = channel;
                    break;
                case SeatPlanKind.Client:
                case SeatPlanKind.Agent:
                    _socketSeatCodes.Add(plan.Color);
                    break;
                case SeatPlanKind.StandIn:
                    _channels[plan.Color] = new DelegateAgentChannel($"stand-in seat {plan.Color}", policy.Respond);
                    break;
            }
        }
        _liveChannels = live;

        _feed = new SpectatorFeed(() => _watcherJoined?.Invoke());
        SeedSpectatorBoard();

        // The listener exists for every hosted table, even one with no
        // socket seats today: watchers attach through it, the app's own
        // seat panel reads through it, and a seat can be released to an
        // agent through it later.
        IPAddress address = sessionConfig.ListenAddress ?? IPAddress.Any;
        _connector = new SocketSeatConnector(address, sessionConfig.ListenPort, _socketSeatCodes, _feed);
        ListenPort = _connector.Port;
    }

    /// <summary>A fresh table from the host door's plan.</summary>
    public HostedTable(BoardModel board, TableSetup setup, Action<string> notify, Action? watcherJoined = null)
        : this(board, PrepareFresh(setup), setup.Seats, resumePlan: null, notify, watcherJoined)
    {
        EffectiveSeed = _config.Seed;
    }

    private HostedTable(
        BoardModel board,
        (GameConfig Config, SessionConfig Session) prepared,
        IReadOnlyList<SeatPlan> seatPlans,
        ResumePlan? resumePlan,
        Action<string> notify,
        Action? watcherJoined)
        : this(board, prepared.Config, prepared.Session, seatPlans, resumePlan, notify, watcherJoined)
    {
    }

    private static (GameConfig, SessionConfig) PrepareFresh(TableSetup setup)
    {
        var config = BuildConfig(setup);
        return (config, new SessionConfig(
            config,
            setup.RecordsFolder,
            IPAddress.Parse(setup.Address),
            setup.Port,
            setup.StallWarningSeconds));
    }

    /// <summary>
    /// A suspended game taken up again: the recorded game's own
    /// configuration, with each seat driven as the host chose on the
    /// resume form. The this-screen seat, when chosen, is handed to the
    /// session as a live channel at the replay boundary.
    /// </summary>
    public static HostedTable ForResume(
        BoardModel board,
        ResumePlan plan,
        IReadOnlyList<SeatPlan> seatChoices,
        string address,
        int port,
        int stallWarningSeconds,
        Action<string> notify,
        Action? watcherJoined = null)
    {
        var original = plan.OriginalConfig;
        var seats = original.Seats
            .Select(seat => new SeatAssignment(
                seat.Code,
                (seatChoices.FirstOrDefault(c => c.Color == seat.Code)?.Kind ?? SeatPlanKind.StandIn) switch
                {
                    SeatPlanKind.StandIn => SeatKind.Scripted,
                    _ => SeatKind.Socket,
                }))
            .ToList();
        var config = new GameConfig(original.Mode, seats, original.Seed, original.TerritorySetup);
        string recordsDir = Path.GetDirectoryName(Path.GetFullPath(plan.RecordPath))!;
        var sessionConfig = new SessionConfig(
            config, recordsDir, IPAddress.Parse(address), port, stallWarningSeconds);
        return new HostedTable(board, config, sessionConfig, seatChoices, plan, notify, watcherJoined)
        {
            EffectiveSeed = original.Seed,
        };
    }

    private static GameConfig BuildConfig(TableSetup setup)
    {
        var seats = setup.Seats
            .Select(plan => new SeatAssignment(
                plan.Color,
                plan.Kind == SeatPlanKind.StandIn ? SeatKind.Scripted : SeatKind.Socket))
            .ToList();
        long seed = setup.Seed ?? Random.Shared.NextInt64(1, long.MaxValue);
        return new GameConfig(
            setup.Mode == TableMode.Duo ? GameMode.Duo : GameMode.Standard,
            seats,
            seed,
            setup.Territories == SetupTerritories.RandomDeal
                ? TerritorySetup.RandomDeal
                : TerritorySetup.ManualClaims);
    }

    /// <summary>The game's mode (Standard or Duo), as the session plays it.</summary>
    public GameMode Mode => _config.Mode;

    /// <summary>The folder the game's record and transcript are written to.</summary>
    public string RecordsFolder => _sessionConfig.RecordDirectory;

    /// <summary>How the game's territories are assigned at setup.</summary>
    public TerritorySetup TerritorySetup => _config.TerritorySetup;

    /// <summary>The seats as planned, with their display names: the app's own labels for the table.</summary>
    public IReadOnlyList<SeatPlan> SeatPlans { get; }

    /// <summary>The seat played on this screen, when the plan has one.</summary>
    public string? LocalSeatCode { get; private set; }

    /// <summary>The channel of the seat played on this screen, when the plan has one.</summary>
    public UiSeatChannel? LocalChannel { get; private set; }

    /// <summary>The seed the table plays with (the host's pick, shown on the setup screen).</summary>
    public long EffectiveSeed { get; private set; }

    /// <summary>The port the listener actually bound.</summary>
    public int ListenPort { get; }

    /// <summary>The session, once every seat is in (immediately, for a resumed game).</summary>
    public GameSession? Session { get; private set; }

    /// <summary>The session's run, completing with its result.</summary>
    public Task<SessionResult>? RunTask { get; private set; }

    /// <summary>Socket seats still waiting for their player to join.</summary>
    public IReadOnlyCollection<string> OpenSeats => _connector?.OpenSeats ?? Array.Empty<string>();

    /// <summary>Where this app's own watch connection should knock: the listen address, or loopback when listening on all addresses.</summary>
    public IReadOnlyList<IPAddress> WatchTapAddresses
    {
        get
        {
            var address = _sessionConfig.ListenAddress ?? IPAddress.Any;
            return address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
                : new[] { address };
        }
    }

    /// <summary>Opens the table: socket seats are accepted in the background, then the session starts.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Directory.CreateDirectory(_sessionConfig.RecordDirectory);

        if (_resumePlan is not null)
        {
            Session = GameSession.CreateResumed(
                _board, _sessionConfig, _resumePlan, _liveChannels, _notify, _feed, _connector);
            RunTask = Task.Run(Session.Run);
            return;
        }

        if (_socketSeatCodes.Count == 0)
        {
            CreateSessionAndRun();
            return;
        }

        _acceptThread = new Thread(AcceptSeatsThenRun) { IsBackground = true, Name = "AgentRisk seat acceptor" };
        _acceptThread.Start();
    }

    private void AcceptSeatsThenRun()
    {
        try
        {
            for (int i = 0; i < _socketSeatCodes.Count; i++)
            {
                var (channel, code) = _connector!.AcceptSeat();
                _channels[code] = channel;
                _notify($"Seat {code} joined.");
            }
            CreateSessionAndRun();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The table was closed while seats were still out: nothing to start.
            if (!_disposed) _notify($"The table could not start: {ex.Message}");
        }
    }

    private void CreateSessionAndRun()
    {
        Session = new GameSession(_board, _sessionConfig, _channels, _notify, _feed, _connector);
        RunTask = Task.Run(Session.Run);
    }

    private void SeedSpectatorBoard()
    {
        // A scratch game renders the
        // setup-state board block so a watcher who connects before play
        // begins already has a table to look at.
        var setup = new RiskGame(
            BoardProjectBoardData.FromBoard(_board),
            _config.PlayerCodes,
            _config.Mode,
            new SeededRandom(_config.Seed),
            territorySetup: _config.TerritorySetup);
        var firstDecision = setup.PendingDecision();
        if (firstDecision is not null)
            _feed.SeedBoardBlock(new PromptBuilder(_board).BoardBlock(setup.Snapshot(), firstDecision, firstDecision.Seat));
    }

    // -- Host controls (pause/poke/replace + suspend), passed to the session.

    public SessionSnapshot? Snapshot() => Session?.GetSnapshot();

    public void Poke(string seatCode) => Session?.HostPoke(seatCode);

    public void ReplaceSeatWithStandIn(string seatCode) => Session?.HostReplaceSeatKind(seatCode, SeatKind.Scripted);

    public void ReleaseSeatToAgent(string seatCode) => Session?.HostReplaceSeatKind(seatCode, SeatKind.Socket);

    public void PauseGame() => Session?.HostPause();

    public void ResumeGame() => Session?.HostResume();

    public void Substitute(int moveNumber) => Session?.HostSubstitute(moveNumber);

    public void SuspendGame() => Session?.HostSuspend();

    public void EndGame() => Session?.HostEndGame();

    /// <summary>
    /// Applies a guarded host edit through the session, the
    /// same call the edit panel's Confirm makes: the outcome task, or
    /// null when no session is running (the panel treats that as the
    /// table having moved on).
    /// </summary>
    public Task<HostEditOutcome>? ApplyEditAsync(IReadOnlyList<TerritoryEdit> edits) =>
        Session?.HostApplyEditAsync(edits);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalChannel?.Close();
        try
        {
            if (Session is not null && RunTask is not null && !RunTask.IsCompleted)
            {
                Session.HostEndGame();
                RunTask.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Teardown only: a faulted run has already reported itself.
        }
        _connector?.Dispose();
        _feed.Dispose();
    }
}
