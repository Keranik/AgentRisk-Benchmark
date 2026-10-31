using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentRisk.Client.Protocol;
using AgentRisk.Host;
using Godot;
using BoardModel = AgentRisk.Board.Board;

namespace AgentRisk.Client.App;

/// <summary>
/// The <c>--wedgetest</c> driver: the failure drill,
/// run through the app with no hand on any mouse. This app hosts a
/// fixed table (Standard, the reference seed, claims) whose agent
/// seat is played by a joining client that answers its first few
/// prompts with the plain policy and then goes silent — the socket
/// stays open, nothing crashes, the seat simply never replies again:
/// the textbook wedge. The driver plays the host's part exactly as a
/// person at the Host door would, with the session's own surfaces:
/// it waits until the seat's telemetry shows it stalled, pokes it
/// (the poke is re-sent and recorded; the wedge holds), then
/// replaces the seat with a stand-in through the host control, and
/// the game plays to its recorded finish. The closing checks read
/// the finished record and transcript back and require both
/// intervention lines to stand in each, so the drill passes only
/// when the audit trail proves what the host did.
/// </summary>
public sealed class WedgeTestDriver
{
    /// <summary>The fixed seed of the drill game: the reference seed, so the table is a known quantity.</summary>
    public const long Seed = 43;

    /// <summary>The seat the joining client plays, and then wedges.</summary>
    public const string WedgeSeat = "GRN";

    /// <summary>Prompts the joining client answers before it goes silent.</summary>
    public const int RepliesBeforeWedge = 6;

    /// <summary>Seconds the table waits on a reply before its telemetry calls the seat stalled.</summary>
    public const int StallWarningSeconds = 5;

    /// <summary>Seconds the driver watches the table after the poke, proving the wedge holds.</summary>
    private const int PokeObserveSeconds = 4;

    /// <summary>The wedge guard: a drill that has not finished in this long has failed, loudly.</summary>
    private const int DeadlineMinutes = 30;

    private const int MonitorPollMilliseconds = 50;
    private const int AnswererPollMilliseconds = 2;

    private readonly HostedTable _table;
    private readonly BoardCatalog _catalog;
    private readonly WedgedJoiner _joiner;
    private volatile bool _done;

    public WedgeTestDriver(BoardModel board, BoardCatalog catalog, string recordsFolder)
    {
        _catalog = catalog;
        var setup = new TableSetup
        {
            Address = HostTestDriver.ListenAddress,
            Port = 0,
            Seed = Seed,
            RecordsFolder = recordsFolder,
            StallWarningSeconds = StallWarningSeconds,
        };
        setup.Seats.Clear();
        setup.Seats.Add(new SeatPlan("RED", SeatPlanKind.ThisScreen));
        setup.Seats.Add(new SeatPlan("BLU", SeatPlanKind.StandIn));
        setup.Seats.Add(new SeatPlan(WedgeSeat, SeatPlanKind.Agent));
        setup.Seats.Add(new SeatPlan("YEL", SeatPlanKind.StandIn));
        _table = new HostedTable(board, setup, notice => GD.Print($"WEDGETEST NOTICE {notice}"));
        _joiner = new WedgedJoiner(catalog);
    }

    /// <summary>The session's result, once the game has ended.</summary>
    public SessionResult? Result { get; private set; }

    /// <summary>Why the drill failed, when it did.</summary>
    public string? Failure { get; private set; }

    /// <summary>True once the closing checks found both intervention lines in the record and the transcript.</summary>
    public bool EvidenceOk { get; private set; }

    /// <summary>True once the drill has an outcome, result or failure.</summary>
    public bool Done => _done;

    /// <summary>Opens the table, seats the joining client, and starts the drill on its own thread.</summary>
    public void Start()
    {
        GD.Print($"WEDGETEST TABLE mode=standard seed={Seed} setup=claims seats=RED:this-screen,BLU:stand-in,{WedgeSeat}:agent(wedges after {RepliesBeforeWedge} replies),YEL:stand-in stall_warning={StallWarningSeconds}s");
        _table.Start();

        var answerer = new Thread(AnswerLoop) { IsBackground = true, Name = "Wedgetest seat answerer" };
        answerer.Start();
        _joiner.Connect(HostTestDriver.ListenAddress, _table.ListenPort);
        GD.Print($"WEDGETEST JOINED seat={WedgeSeat} port={_table.ListenPort}");

        var driver = new Thread(DriveLoop) { IsBackground = true, Name = "Wedgetest driver" };
        driver.Start();
    }

    // This screen's seat, answered by the plain policy on this thread:
    // the hosttest's pattern, unchanged. The drill is about the agent
    // seat; RED just has to keep the table honest.
    private void AnswerLoop()
    {
        var channel = _table.LocalChannel!;
        while (!_done)
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
                    GD.Print("WEDGETEST NOTICE the RED answerer had no reply for a prompt; the seat closes without one");
                request.Answer(reply);
            }
            Thread.Sleep(AnswererPollMilliseconds);
        }
    }

    // The drill itself, on this thread: each step is something the
    // Host door offers a person, taken through the same HostedTable
    // calls the screen's buttons make, and each wait is bounded by
    // the one deadline so any surprise is a named failure.
    private void DriveLoop()
    {
        var deadline = DateTime.UtcNow.AddMinutes(DeadlineMinutes);
        try
        {
            var session = WaitFor(deadline, () => _table.Session, "the session to start");

            int wedgedAt = (int)WaitFor(deadline,
                () => _joiner.WedgedAtDecision is { } number ? (object)number : null,
                "the agent seat to go silent");
            GD.Print($"WEDGETEST WEDGED seat={WedgeSeat} after_replies={RepliesBeforeWedge} decision=D-{wedgedAt}");

            var stalled = WaitFor(deadline, () =>
            {
                var seat = session.GetSnapshot().Seats.Single(s => s.Code == WedgeSeat);
                return seat.Stalled ? seat : null;
            }, "the seat's telemetry to show it stalled");
            GD.Print($"WEDGETEST STALLED seat={WedgeSeat} decision=D-{stalled.PendingDecisionNumber} " +
                     $"since_last_reply={(stalled.SinceLastReply?.TotalSeconds ?? -1):0.0}s connection={stalled.Connection}");
            int pendingAtPoke = stalled.PendingDecisionNumber ?? wedgedAt;

            _table.Poke(WedgeSeat);
            GD.Print($"WEDGETEST POKE seat={WedgeSeat} decision=D-{pendingAtPoke}");

            Thread.Sleep(TimeSpan.FromSeconds(PokeObserveSeconds));
            var afterPoke = session.GetSnapshot().Seats.Single(s => s.Code == WedgeSeat);
            if (session.Status != SessionStatus.Running
                || afterPoke.PendingDecisionNumber != pendingAtPoke)
                throw new InvalidOperationException(
                    $"the table moved on after the poke (status {session.Status}, " +
                    $"pending D-{afterPoke.PendingDecisionNumber}); the wedge did not persist");
            GD.Print($"WEDGETEST WEDGE PERSISTED seat={WedgeSeat} decision=D-{pendingAtPoke} " +
                     $"observed={PokeObserveSeconds}s stalled={afterPoke.Stalled}");

            _table.ReplaceSeatWithStandIn(WedgeSeat);
            GD.Print($"WEDGETEST REPLACE seat={WedgeSeat} with=stand-in decision=D-{pendingAtPoke}");
            _joiner.Dispose();

            var run = _table.RunTask ?? throw new InvalidOperationException("the session never ran");
            if (!run.Wait(TimeSpan.FromMinutes(DeadlineMinutes)))
                throw new InvalidOperationException($"the game did not finish within {DeadlineMinutes} minutes");
            Result = run.GetAwaiter().GetResult();
            if (Result.Status != SessionStatus.Completed)
                throw new InvalidOperationException($"the game ended as {Result.Status}, not Completed");

            EvidenceOk = CheckEvidence(Result.RecordPath);
        }
        catch (Exception ex)
        {
            Failure = ex.GetBaseException().Message;
        }
        _done = true;
    }

    // The drill's verdict is the audit trail, not the driver's say-so:
    // the poke and the replacement must stand in the record and in
    // the transcript, in the forms the host writes them.
    private bool CheckEvidence(string recordPath)
    {
        string[] recordLines = File.ReadAllLines(recordPath);
        string? poke = recordLines.FirstOrDefault(line => line.StartsWith($"HOST POKE {WedgeSeat} ", StringComparison.Ordinal));
        string? replace = recordLines.FirstOrDefault(line => line.StartsWith($"HOST REPLACE {WedgeSeat} script", StringComparison.Ordinal));
        string transcriptPath = GameTranscriptWriter.PathForRecord(recordPath);
        string transcript = File.ReadAllText(transcriptPath);
        bool transcriptOk = transcript.Contains($"### HOST POKE seat={WedgeSeat}", StringComparison.Ordinal)
                            && transcript.Contains($"### HOST REPLACE seat={WedgeSeat}", StringComparison.Ordinal);
        GD.Print($"WEDGETEST RECORD poke=\"{poke ?? "MISSING"}\"");
        GD.Print($"WEDGETEST RECORD replace=\"{replace ?? "MISSING"}\"");
        GD.Print($"WEDGETEST TRANSCRIPT interventions={(transcriptOk ? "present" : "MISSING")}");
        if (poke is null || replace is null || !transcriptOk)
        {
            Failure = "the record or transcript lacks an intervention line";
            return false;
        }
        return true;
    }

    private T WaitFor<T>(DateTime deadline, Func<T?> probe, string what) where T : class
    {
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value) return value;
            Thread.Sleep(MonitorPollMilliseconds);
        }
        throw new InvalidOperationException($"timed out waiting for {what}");
    }

    /// <summary>
    /// The joining client that wedges: it plays its seat with the
    /// plain policy for the first <see cref="RepliesBeforeWedge"/>
    /// prompts, then never replies again. Its socket stays open and
    /// its reader keeps running — re-sent prompts (a host poke) are
    /// received and ignored, exactly the silence a wedged agent makes.
    /// </summary>
    private sealed class WedgedJoiner : IDisposable
    {
        private const int NotWedged = -1;

        private readonly BoardCatalog _catalog;
        private SeatClient? _client;
        private int _repliesSent;
        private int _wedgedAtDecision = NotWedged;

        public WedgedJoiner(BoardCatalog catalog) => _catalog = catalog;

        /// <summary>The decision number the seat went silent on, once it has.</summary>
        public int? WedgedAtDecision
        {
            get
            {
                int value = Volatile.Read(ref _wedgedAtDecision);
                return value == NotWedged ? null : value;
            }
        }

        public void Connect(string host, int port)
        {
            _client = SeatClient.Connect(host, port, WedgeSeat);
            _client.BlockReceived += OnBlock;
            _client.StartReading();
        }

        private void OnBlock(BlockMessage block)
        {
            if (!block.ExpectsReply) return;
            var message = PromptParser.ParseBlock(block.Text, expectsReply: true);
            if (Volatile.Read(ref _repliesSent) >= RepliesBeforeWedge)
            {
                // Wedged. The decision number is noted once, for the
                // drill's log; the block itself is left unanswered.
                if (message is PromptMessage.Decision decision)
                    Interlocked.CompareExchange(ref _wedgedAtDecision, decision.Prompt.Number, NotWedged);
                return;
            }
            string? reply = message switch
            {
                PromptMessage.Decision decision => AutoPolicy.Answer(decision.Prompt, _catalog),
                PromptMessage.StrikeThree => AutoPolicy.AnswerStrikeThree(),
                _ => null,
            };
            if (reply is null) return; // no answer to give; the silence starts here instead
            Interlocked.Increment(ref _repliesSent);
            _client!.SendReply(reply);
        }

        public void Dispose() => _client?.Dispose();
    }
}
