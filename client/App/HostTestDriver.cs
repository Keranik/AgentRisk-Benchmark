using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentRisk.Client.Protocol;
using AgentRisk.Host;
using Godot;
using BoardModel = AgentRisk.Board.Board;

namespace AgentRisk.Client.App;

/// <summary>
/// The <c>--hosttest</c> driver: this app hosts a real
/// table and two more copies of the app join it, so a mixed table plays
/// end to end through the actual sockets with no hand on any mouse.
/// The setup is fixed: a Standard game at the reference seed, seats RED
/// (this screen), BLU (Client), GRN (Agent), YEL (stand-in), and
/// territories claimed, never dealt. The port is ephemeral; the driver
/// prints it as its first HOSTTEST line so the joining instances can
/// be aimed at it.
///
/// This screen's seat is answered by the plain policy on a background
/// thread — the demo auto-answer the screenshot mode uses — never on
/// the UI thread, and no table screen is built: the driver's output is
/// its HOSTTEST lines on stdout (the listening port, the host's own
/// notices, a milestone every <see cref="MilestoneInterval"/>
/// decisions, and the closing result). A watch connection counts the
/// feed's decisions for the milestones; it is reporting only, and its
/// loss never fails the run. The run ends when the session does; a
/// hard deadline turns a wedged table into a loud failure instead of
/// a hang.
/// </summary>
public sealed class HostTestDriver
{
    /// <summary>
    /// The address the table listens on and the joiners dial: this
    /// machine's IPv6 loopback, or the IPv4 loopback on a machine with
    /// no IPv6 at all (where binding ::1 fails with "address family not
    /// supported"). The IPv6 form stays first because the sandbox these
    /// drivers were built in blackholes .NET connects to 127.0.0.1.
    /// </summary>
    public static readonly string ListenAddress =
        System.Net.Sockets.Socket.OSSupportsIPv6 ? "::1" : "127.0.0.1";

    /// <summary>The fixed seed of the hosttest game: the reference seed, so the table is a known quantity.</summary>
    public const long Seed = 43;

    /// <summary>Decisions between HOSTTEST DECISIONS milestone lines.</summary>
    public const int MilestoneInterval = 100;

    /// <summary>The wedge guard: a table that has not finished in this long has failed, loudly.</summary>
    private const int DeadlineMinutes = 30;

    private const int AnswererPollMilliseconds = 2;
    private const int MonitorPollMilliseconds = 50;

    private readonly HostedTable _table;
    private readonly BoardCatalog _catalog;
    private readonly Stopwatch _wall = new();
    private readonly HashSet<int> _decisionsSeen = new();
    private readonly object _decisionsLock = new();
    private SeatClient? _tap;
    private volatile bool _answering = true;
    private volatile bool _done;

    public HostTestDriver(BoardModel board, BoardCatalog catalog, string recordsFolder)
    {
        _catalog = catalog;
        var setup = new TableSetup
        {
            Address = ListenAddress,
            Port = 0,
            Seed = Seed,
            RecordsFolder = recordsFolder,
        };
        setup.Seats.Clear();
        setup.Seats.Add(new SeatPlan("RED", SeatPlanKind.ThisScreen));
        setup.Seats.Add(new SeatPlan("BLU", SeatPlanKind.Client));
        setup.Seats.Add(new SeatPlan("GRN", SeatPlanKind.Agent));
        setup.Seats.Add(new SeatPlan("YEL", SeatPlanKind.StandIn));
        _table = new HostedTable(board, setup, notice => GD.Print($"HOSTTEST NOTICE {notice}"));
    }

    /// <summary>The port the listener actually bound; the joiners dial this.</summary>
    public int ListenPort => _table.ListenPort;

    /// <summary>Decisions the watch tap has seen asked so far (distinct decision numbers).</summary>
    public int DecisionsSeen
    {
        get { lock (_decisionsLock) return _decisionsSeen.Count; }
    }

    /// <summary>The session's result, once the game has ended.</summary>
    public SessionResult? Result { get; private set; }

    /// <summary>Why the run failed, when it did: a start that never happened, a fault, or the deadline.</summary>
    public string? Failure { get; private set; }

    /// <summary>True once the run has an outcome, result or failure.</summary>
    public bool Done => _done;

    /// <summary>Wall time from the table opening to its outcome.</summary>
    public TimeSpan Elapsed => _wall.Elapsed;

    /// <summary>Opens the table and starts the answerer, the monitor, and the counting tap.</summary>
    public void Start()
    {
        GD.Print($"HOSTTEST LISTENING address={ListenAddress} port={ListenPort}");
        GD.Print($"HOSTTEST TABLE mode=standard seed={Seed} setup=claims seats=RED:this-screen,BLU:client,GRN:agent,YEL:stand-in");
        _wall.Start();
        _table.Start();

        var answerer = new Thread(AnswerLoop) { IsBackground = true, Name = "Hosttest seat answerer" };
        answerer.Start();
        var monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "Hosttest monitor" };
        monitor.Start();

        try
        {
            _tap = SeatClient.Connect(ListenAddress, ListenPort, "WATCH");
            _tap.BlockReceived += block =>
            {
                var entry = FeedEntry.Parse(block.Text);
                if (entry.Kind == FeedEntryKind.Prompt && entry.DecisionNumber is { } number)
                    lock (_decisionsLock) _decisionsSeen.Add(number);
            };
            _tap.StartReading();
        }
        catch (Exception)
        {
            // The tap only counts milestones; the session's own result
            // is the verdict, so a lost tap never fails the run.
        }
    }

    // This screen's seat, answered by the plain policy on this thread:
    // the session asks through the channel, the question is parsed like
    // any prompt, and the policy's reply line completes it. A prompt the
    // policy cannot answer closes the seat without a reply, exactly as
    // a closed window would; the session's failure policy then owns the
    // table, and the deadline reports the stall.
    private void AnswerLoop()
    {
        var channel = _table.LocalChannel!;
        while (_answering)
        {
            if (channel.TryTakeRequest(out var request) && !request.IsResend)
            {
                var message = PromptParser.ParseBlock(request.Text, expectsReply: true);
                string? reply = message switch
                {
                    PromptMessage.Decision decision => AutoPolicy.Answer(decision.Prompt, _catalog),
                    PromptMessage.StrikeThree => AutoPolicy.AnswerStrikeThree(),
                    _ => null,
                };
                if (reply is null)
                    GD.Print("HOSTTEST NOTICE the RED answerer had no reply for a prompt; the seat closes without one");
                request.Answer(reply);
            }
            Thread.Sleep(AnswererPollMilliseconds);
        }
    }

    // Waits for the session to exist, then for it to end. The deadline
    // is measured from the table opening and covers both waits: seats
    // that never join are a wedged table too.
    private void MonitorLoop()
    {
        var deadline = DateTime.UtcNow.AddMinutes(DeadlineMinutes);
        Task<SessionResult>? run = null;
        while (DateTime.UtcNow < deadline)
        {
            run ??= _table.RunTask;
            if (run is not null && run.IsCompleted) break;
            Thread.Sleep(MonitorPollMilliseconds);
        }

        if (run is null)
        {
            Failure = "the session never started";
        }
        else if (!run.IsCompleted)
        {
            Failure = $"the game did not finish within {DeadlineMinutes} minutes";
        }
        else
        {
            try
            {
                Result = run.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Failure = ex.GetBaseException().Message;
            }
        }

        _answering = false;
        _wall.Stop();
        _done = true;
    }
}
