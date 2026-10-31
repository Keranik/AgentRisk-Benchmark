using AgentRisk.Engine;

namespace AgentRisk.Host;

/// <summary>How a seat's channel stands, as seat telemetry reports it.</summary>
public enum SeatConnection
{
    /// <summary>An in-process channel (a scripted seat, or any host-built local channel).</summary>
    InProcess,

    /// <summary>A socket seat, joined and its stream live.</summary>
    Connected,

    /// <summary>
    /// A socket seat whose stream has closed or failed. The channel knows
    /// the moment its read hits the end of the stream, so a seat that
    /// drops while its decision is pending shows as disconnected at once,
    /// not when someone next writes to it.
    /// </summary>
    Disconnected,

    /// <summary>The seat was reopened over a socket and no client has claimed it yet.</summary>
    AwaitingJoin,
}

/// <summary>One seat's standing in a <see cref="SessionSnapshot"/>.</summary>
/// <param name="Code">The seat's code.</param>
/// <param name="Kind">How the seat is currently driven (replacements change it).</param>
/// <param name="Connection">The state of the seat's channel.</param>
/// <param name="SinceLastReply">
/// Time since the seat last sent any reply line, legal or not; null when
/// the seat has never replied.
/// </param>
/// <param name="StrikesOnCurrentDecision">
/// Replies rejected on the decision the table currently waits on (0 when
/// the table is not waiting on this seat).
/// </param>
/// <param name="PendingDecisionNumber">
/// The D-n of the decision the table waits on this seat for, or null.
/// While the session sits in a strike-pause this is the paused decision.
/// </param>
/// <param name="PendingDecisionKind">The kind of that decision, or null.</param>
/// <param name="Stalled">
/// The table has waited on this seat longer than the session's
/// stall-warning interval. A flag for the host only: the session never
/// acts on a stall by itself.
/// </param>
public sealed record SeatSnapshot(
    string Code,
    SeatKind Kind,
    SeatConnection Connection,
    TimeSpan? SinceLastReply,
    int StrikesOnCurrentDecision,
    int? PendingDecisionNumber,
    DecisionKind? PendingDecisionKind,
    bool Stalled);

/// <summary>
/// The table's standing at one instant, for any host surface (console
/// status, the app's host area): the session's lifecycle state plus one
/// <see cref="SeatSnapshot"/> per seat, in seat order. Reading a
/// snapshot never touches the game; it is safe from any thread at any
/// time, including while a decision is in flight.
/// </summary>
/// <param name="Status">The session's lifecycle state (running, strike-paused, host-paused, ended).</param>
/// <param name="Seats">One snapshot per player seat, in seat order. The Rebels are automated and never appear.</param>
/// <param name="HostEdited">
/// The game has been edited by the host at least once: it
/// carries the flag for the rest of its life, across suspend and
/// resume, exactly as its record header does.
/// </param>
/// <param name="EditUsedThisPause">
/// The current host pause has already spent its one edit (a
/// pause allows one edit session). False whenever the table is
/// not host-paused.
/// </param>
public sealed record SessionSnapshot(
    SessionStatus Status,
    IReadOnlyList<SeatSnapshot> Seats,
    bool HostEdited = false,
    bool EditUsedThisPause = false);
