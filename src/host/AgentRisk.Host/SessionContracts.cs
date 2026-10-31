using AgentRisk.Engine;

namespace AgentRisk.Host;

/// <summary>How a seat is driven in a game.</summary>
public enum SeatKind
{
    /// <summary>A remote agent on a socket, playing in text.</summary>
    Socket,

    /// <summary>A local scripted seat: the host's built-in policy, no socket.</summary>
    Scripted,
}

/// <summary>One seat's assignment: a player code and how it is driven. The Rebels never appear here.</summary>
public sealed record SeatAssignment(string Code, SeatKind Kind);

/// <summary>
/// Everything the host fixes before a game starts: mode, seats in order,
/// the seed, and how territories are assigned at setup
/// (manual claims by default, or the optional random deal).
/// </summary>
public sealed record GameConfig(
    GameMode Mode,
    IReadOnlyList<SeatAssignment> Seats,
    long Seed,
    TerritorySetup TerritorySetup = TerritorySetup.ManualClaims)
{
    /// <summary>Player codes in seat order (the rotation the engine is built with).</summary>
    public IReadOnlyList<string> PlayerCodes => Seats.Select(s => s.Code).ToList();

    /// <summary>How the seat with this code is driven.</summary>
    public SeatKind KindOf(string seatCode) => Seats.Single(s => s.Code == seatCode).Kind;
}

/// <summary>Lifecycle of a <see cref="GameSession"/>.</summary>
public enum SessionStatus
{
    NotStarted,
    Running,

    /// <summary>Halted for the host: a seat struck out, or its channel failed.</summary>
    Paused,

    /// <summary>
    /// Halted by the host's own word: the table issues no new
    /// decisions until the host resumes it. Distinct from
    /// <see cref="Paused"/>, which is the failure policy's halt; a
    /// decision in flight when the host pauses still completes.
    /// </summary>
    HostPaused,

    /// <summary>The game played to a winner.</summary>
    Completed,

    /// <summary>The host ended the game (from a pause, or by the decision cap).</summary>
    EndedByHost,

    /// <summary>
    /// The host suspended the game: the record and transcript
    /// close in a suspended state, with no winner, naming the decision
    /// play stopped at. A suspended game — and only a suspended game —
    /// can be resumed, by deterministic re-execution of its recorded
    /// choice stream (see <see cref="ResumePlan"/>). Distinct from
    /// <see cref="EndedByHost"/>: a suspended game is not over, it is
    /// waiting to be continued.
    /// </summary>
    Suspended,
}

/// <summary>Why the session is paused, and what the host is looking at.</summary>
/// <param name="Seat">The seat whose decision is waiting.</param>
/// <param name="Decision">The decision the engine is waiting on.</param>
/// <param name="DecisionNumber">The session's running decision counter (the D-n in prompts).</param>
/// <param name="Reason">What went wrong, in the words last shown to the seat or the channel error.</param>
/// <param name="LegalMoves">The enumerated legal moves, for the host's substitute control.</param>
public sealed record PauseInfo(
    string Seat,
    PendingDecision Decision,
    int DecisionNumber,
    string Reason,
    IReadOnlyList<string> LegalMoves);

/// <summary>The outcome of a finished session.</summary>
/// <param name="Status">How the session ended.</param>
/// <param name="Winner">The winning seat code, or null when nobody won (host-ended).</param>
/// <param name="Decisions">Decisions resolved over the game's life.</param>
/// <param name="RecordPath">Path of the full game record file.</param>
/// <param name="EndReason">
/// Why the game ended, when the host or the cap ended it; for a
/// suspended game, the statement of the suspension (the decision play
/// stopped at).
/// </param>
public sealed record SessionResult(
    SessionStatus Status,
    string? Winner,
    int Decisions,
    string RecordPath,
    string? EndReason);

/// <summary>
/// The outcome of a host edit request. An edit
/// is either applied — carrying the engine's account of what changed —
/// or refused, carrying the plain-language reason the host is shown.
/// Refusals are values here, never exceptions: every refusal in the
/// edit path is a normal answer (the table was not host-paused, this
/// pause's edit was already used, the game is over, the edit stepped
/// outside the closed set), and the game is untouched by each of them.
/// The task a host receives from
/// <see cref="GameSession.HostApplyEditAsync"/> always completes with
/// one of these; it never faults.
/// </summary>
public abstract record HostEditOutcome
{
    private HostEditOutcome() { }

    /// <summary>The edit was validated, applied atomically, and recorded.</summary>
    public sealed record Applied(HostEditResult Result) : HostEditOutcome;

    /// <summary>The edit was refused; nothing changed. The reason is fit to show the host verbatim.</summary>
    public sealed record Refused(string Reason) : HostEditOutcome;
}
