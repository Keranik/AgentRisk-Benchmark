namespace AgentRisk.Engine;

/// <summary>
/// Thrown when a choice does not match the pending decision or breaks a rule.
/// The host's failure policy depends on strict rejection: the
/// engine never coerces, clamps, or guesses at an illegal choice.
/// </summary>
public sealed class IllegalChoiceException : Exception
{
    public IllegalChoiceException(string reason) : base(reason) => Reason = reason;

    /// <summary>Human-readable reason, safe to show the deciding agent on a re-prompt.</summary>
    public string Reason { get; }
}

/// <summary>The kinds of decision the engine can be waiting on.</summary>
public enum DecisionKind
{
    Claim,
    ClaimForRebels,
    SetupPlace,
    Trade,
    Place,
    AttackDeclare,
    AttackDice,
    DefendDice,
    CaptureMove,
    Fortify,
}

/// <summary>Whether a placement happens at turn start or during an elimination inheritance.</summary>
public enum PlacementContext
{
    TurnStart,
    Inheritance,
}

/// <summary>Base type for everything the engine can be waiting on. <see cref="Seat"/> is who decides.</summary>
public abstract record PendingDecision(DecisionKind Kind, string Seat);

/// <summary>Setup: claim one unclaimed territory for yourself.</summary>
public sealed record ClaimDecision(string Seat, IReadOnlyList<string> Unclaimed)
    : PendingDecision(DecisionKind.Claim, Seat);

/// <summary>Duo setup: choose which territory the Rebels receive; the decider is a player, the recipient is REB.</summary>
public sealed record ClaimForRebelsDecision(string Seat, string Recipient, IReadOnlyList<string> Unclaimed)
    : PendingDecision(DecisionKind.ClaimForRebels, Seat);

/// <summary>Setup: place one army on one of your own territories.</summary>
public sealed record SetupPlaceDecision(string Seat, IReadOnlyList<string> OwnTerritories, int ArmiesLeft)
    : PendingDecision(DecisionKind.SetupPlace, Seat);

/// <summary>
/// Trade in one set, or decline when the trade is optional. Forced at 5+ cards
/// at turn start. After an inheritance the trade opens at 6+ and stays forced
/// until the hand is down to 4 or fewer; at that point the window closes even
/// if a set remains.
/// </summary>
public sealed record TradeDecision(
    string Seat,
    bool Forced,
    bool CanDecline,
    PlacementContext Context,
    IReadOnlyList<IReadOnlyList<string>> Sets,
    int NextValue)
    : PendingDecision(DecisionKind.Trade, Seat);

/// <summary>
/// Place armies. Normally any count up to ArmiesLeft on any owned territory;
/// a territory-match placement is restricted to the pictured territories
/// with a fixed count (MatchCandidates set, FixedCount set) because the
/// +2 must go onto a territory pictured by the traded set.
/// One candidate means no choice; several mean the seat chooses which
/// pictured territory receives both armies.
/// </summary>
public sealed record PlaceDecision(
    string Seat,
    IReadOnlyList<string> OwnTerritories,
    int ArmiesLeft,
    PlacementContext Context,
    IReadOnlyList<string>? MatchCandidates,
    int? FixedCount)
    : PendingDecision(DecisionKind.Place, Seat)
{
    public bool IsMatchPlacement => MatchCandidates is not null;
}

/// <summary>One legal attack: source, target, and the most dice the attacker may roll from it.</summary>
public sealed record AttackOption(string From, string To, int MaxDice);

/// <summary>Declare an attack from the enumerated options, or end the attack phase.</summary>
public sealed record AttackDeclareDecision(string Seat, IReadOnlyList<AttackOption> Attacks)
    : PendingDecision(DecisionKind.AttackDeclare, Seat);

/// <summary>
/// ROUND mode: roll the next combat round with 1..MaxDice dice, or stop the
/// attack (stopping is offered only after at least one round has been fought).
/// </summary>
public sealed record AttackDiceDecision(string Seat, string From, string To, int MaxDice, bool CanStop)
    : PendingDecision(DecisionKind.AttackDice, Seat);

/// <summary>The defender picks their own dice count for this round.</summary>
public sealed record DefendDiceDecision(string Seat, string Attacker, string From, string To, int MaxDice)
    : PendingDecision(DecisionKind.DefendDice, Seat);

/// <summary>After a capture: how many armies move in, between Min and Max inclusive.</summary>
public sealed record CaptureMoveDecision(string Seat, string From, string To, int Min, int Max)
    : PendingDecision(DecisionKind.CaptureMove, Seat);

/// <summary>One legal fortify: source, destination, and the most armies that may move (source keeps at least 1).</summary>
public sealed record FortifyOption(string From, string To, int MaxCount);

/// <summary>Fortify once along a connected owned path, or skip.</summary>
public sealed record FortifyDecision(string Seat, IReadOnlyList<FortifyOption> Moves)
    : PendingDecision(DecisionKind.Fortify, Seat);

// ---------------------------------------------------------------------------
// Choices: what a decider hands back. ApplyDecision validates a choice
// strictly against the pending decision; a mismatched type is illegal.
// ---------------------------------------------------------------------------

/// <summary>Base type for all choices.</summary>
public abstract record Choice;

public sealed record ClaimChoice(string Territory) : Choice;
public sealed record ClaimForRebelsChoice(string Territory) : Choice;
public sealed record SetupPlaceChoice(string Territory) : Choice;

/// <summary>Trade the named set, identified by its card ids.</summary>
public sealed record TradeChoice(IReadOnlyList<string> CardIds) : Choice;

/// <summary>Decline an optional trade.</summary>
public sealed record TradeNoneChoice : Choice;

public sealed record PlaceChoice(string Territory, int Count) : Choice;

/// <summary>Combat pacing for one attack: decide each round, or resolve the whole attack.</summary>
public enum AttackMode
{
    Round,
    All,
}

public sealed record AttackChoice(string From, string To, AttackMode Mode) : Choice;
public sealed record EndAttackChoice : Choice;

/// <summary>Roll the next round with this many dice (ROUND mode).</summary>
public sealed record AttackDiceChoice(int Dice) : Choice;

/// <summary>Stop the current attack (ROUND mode, after at least one round).</summary>
public sealed record AttackStopChoice : Choice;

public sealed record DefendDiceChoice(int Dice) : Choice;
public sealed record CaptureMoveChoice(int Count) : Choice;
public sealed record FortifyChoice(string From, string To, int Count) : Choice;
public sealed record FortifySkipChoice : Choice;
