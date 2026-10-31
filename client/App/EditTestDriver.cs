using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentRisk.Client.Protocol;
using AgentRisk.Engine;
using AgentRisk.Host;
using Godot;
using BoardModel = AgentRisk.Board.Board;

namespace AgentRisk.Client.App;

/// <summary>
/// The <c>--edittest</c> driver: the live exercise of
/// the guarded host edit through the app, with no hand on any mouse.
/// This app hosts a fixed table (Standard, the reference seed,
/// claims, three stand-in seats) and, once the game is under way,
/// pauses it, applies one edit through the exact path the edit
/// panel's Confirm uses (<see cref="HostedTable.ApplyEditAsync"/> —
/// the host library's <see cref="GameSession.HostApplyEditAsync"/>
/// under it), resumes, and plays the game out. The driver's output is
/// its EDITTEST lines on stdout: the edit it chose and its outcome,
/// the closing result, and the record's path, which the caller keeps
/// as the exercise's evidence. A hard deadline turns a wedged table
/// into a loud failure instead of a hang.
/// </summary>
public sealed class EditTestDriver
{
    /// <summary>The fixed seed of the edittest game: the reference seed, so the table is a known quantity.</summary>
    public const long Seed = 43;

    /// <summary>Decisions to let pass before pausing for the edit.</summary>
    public const int PauseAfterDecisions = 60;

    /// <summary>Armies the exercise's edit adds to the territory it moves.</summary>
    public const int ArmiesAdded = 4;

    /// <summary>The seat the exercise's edit hands its territory to.</summary>
    public const string EditRecipient = "RED";

    /// <summary>The seat the exercise's edit takes its territory from.</summary>
    public const string EditSource = "GRN";

    /// <summary>The wedge guard: a table that has not finished in this long has failed, loudly.</summary>
    private const int DeadlineMinutes = 30;

    private const int MonitorPollMilliseconds = 25;

    private readonly HostedTable _table;
    private readonly HashSet<int> _decisionsSeen = new();
    private readonly object _decisionsLock = new();
    private SeatClient? _tap;
    private volatile bool _done;

    public EditTestDriver(BoardModel board, string recordsFolder)
    {
        var setup = new TableSetup
        {
            Address = HostTestDriver.ListenAddress,
            Port = 0,
            Seed = Seed,
            RecordsFolder = recordsFolder,
        };
        setup.Seats.Clear();
        setup.Seats.Add(new SeatPlan("RED", SeatPlanKind.StandIn));
        setup.Seats.Add(new SeatPlan("BLU", SeatPlanKind.StandIn));
        setup.Seats.Add(new SeatPlan("GRN", SeatPlanKind.StandIn));
        _table = new HostedTable(board, setup, notice => GD.Print($"EDITTEST NOTICE {notice}"));
    }

    /// <summary>The session's result, once the game has ended.</summary>
    public SessionResult? Result { get; private set; }

    /// <summary>The edit the exercise applied, in the record's own words; empty until it lands.</summary>
    public string EditSummary { get; private set; } = "";

    /// <summary>Why the run failed, when it did.</summary>
    public string? Failure { get; private set; }

    /// <summary>True once the run has an outcome, result or failure.</summary>
    public bool Done => _done;

    /// <summary>Opens the table and starts the exercise on its own thread.</summary>
    public void Start()
    {
        GD.Print($"EDITTEST TABLE mode=standard seed={Seed} setup=claims seats=RED:stand-in,BLU:stand-in,GRN:stand-in");
        _table.Start();
        try
        {
            _tap = SeatClient.Connect(HostTestDriver.ListenAddress, _table.ListenPort, "WATCH");
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
            // The tap only paces the pause; the session's own state
            // (its snapshot) paces it just as well without one.
        }
        var driver = new Thread(DriveLoop) { IsBackground = true, Name = "Edittest driver" };
        driver.Start();
    }

    private int DecisionsSeen
    {
        get { lock (_decisionsLock) return _decisionsSeen.Count; }
    }

    // The exercise itself, on this thread: wait for the game to be
    // under way, pause it, apply the edit through the panel's path,
    // resume, and see the game out — each wait bounded by the one
    // deadline, so any wedge is a named failure.
    private void DriveLoop()
    {
        var deadline = DateTime.UtcNow.AddMinutes(DeadlineMinutes);
        try
        {
            var session = WaitFor(deadline, () => _table.Session, "the session to start");

            WaitFor(deadline, () => DecisionsSeen >= PauseAfterDecisions
                ? (object)true : null, $"{PauseAfterDecisions} decisions to pass");
            session.HostPause();
            WaitFor(deadline, () => session.Status == SessionStatus.HostPaused
                ? (object)true : null, "the host pause to take effect");

            var target = session.Game.Snapshot().Territories.First(t => t.Owner == EditSource);
            var edits = new[]
            {
                new TerritoryEdit(target.Code, EditRecipient, target.Armies + ArmiesAdded),
            };
            var apply = _table.ApplyEditAsync(edits)
                        ?? throw new InvalidOperationException("the table has no session to edit through");
            var outcome = apply.WaitAsync(TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
            switch (outcome)
            {
                case HostEditOutcome.Applied applied:
                    EditSummary = string.Join("; ", applied.Result.Changes.Select(change =>
                        $"{change.Territory} {GameTranscriptWriter.HostEditParts(change)}"));
                    GD.Print($"EDITTEST EDIT applied {EditSummary}");
                    break;
                case HostEditOutcome.Refused refused:
                    throw new InvalidOperationException($"the edit was refused: {refused.Reason}");
            }

            session.HostResume();
            var run = _table.RunTask ?? throw new InvalidOperationException("the session never ran");
            if (!run.Wait(TimeSpan.FromMinutes(DeadlineMinutes)))
                throw new InvalidOperationException($"the game did not finish within {DeadlineMinutes} minutes");
            Result = run.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Failure = ex.GetBaseException().Message;
        }
        _done = true;
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
}
