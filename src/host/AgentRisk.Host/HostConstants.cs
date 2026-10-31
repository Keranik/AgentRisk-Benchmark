namespace AgentRisk.Host;

/// <summary>
/// Every behavior-tuning number and protocol token of the host, named in
/// one place (no magic numbers). Rules numbers themselves live in
/// the engine's RulesConstants; these are the host's own numbers.
/// </summary>
public static class HostConstants
{
    // --- Network (the host serves the decision protocol) ---

    /// <summary>Default TCP port the host listens on; overridable on the command line.</summary>
    public const int DefaultPort = 4000;

    // --- Failure policy ---

    /// <summary>
    /// Prompts a seat gets for one decision before the game pauses for the
    /// host: the full prompt, a full re-prompt carrying the error, then a
    /// prompt containing only the enumerated legal moves.
    /// </summary>
    public const int MaxAttemptsPerDecision = 3;

    // --- Host controls ---

    /// <summary>
    /// Default stall-warning interval for seat telemetry: a seat the
    /// table has been waiting on longer than this is flagged stalled in
    /// the session snapshot. The flag is information for the host only;
    /// the session never acts on a stall by itself. A session may
    /// override it (SessionConfig.StallWarningSeconds).
    /// </summary>
    public const int DefaultStallWarningSeconds = 120;

    // --- Runaway guard ---

    /// <summary>
    /// A game that asks for more decisions than this is treated as stuck and
    /// ended by the host, so a pathological seat pairing can never spin the
    /// host forever. Far above any real game's length (the engine's own
    /// competent-policy games finish in hundreds of decisions).
    /// </summary>
    public const int MaxDecisionsPerGame = 200_000;

    // --- Wire protocol framing (socket transport) ---
    // Every block the host sends ends with exactly one marker line: EndMarker
    // when no reply is wanted, ReplyMarker when one reply line is wanted.
    // The markers are transport, not game text: in-process channels never
    // see them, and they never appear inside a prompt's game text.

    /// <summary>Last line of a block that expects no reply.</summary>
    public const string EndMarker = "<<END>>";

    /// <summary>Last line of a block that expects exactly one reply line.</summary>
    public const string ReplyMarker = "<<REPLY>>";

    /// <summary>First word of the one-line handshake a socket seat sends to claim its seat.</summary>
    public const string JoinVerb = "JOIN";

    /// <summary>
    /// The reserved pseudo-seat a spectator claims with
    /// <c>JOIN WATCH</c>: it holds no seat, is never sent a
    /// decision, and receives the game's live transcript feed instead.
    /// </summary>
    public const string WatchSeatCode = "WATCH";

    // --- Board block rendering ---

    /// <summary>
    /// Owner token written for a territory nobody has claimed yet. The
    /// board block shows owned territories; setup prompts are sent
    /// while claims are still open, so unclaimed land needs a token. This is
    /// a host rendering choice.
    /// </summary>
    public const string UnclaimedOwnerToken = "NONE";

    /// <summary>Written on the YOUR HAND line when the prompted seat holds no cards.</summary>
    public const string EmptyHandToken = "none";
}
