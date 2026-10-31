namespace AgentRisk.Engine;

/// <summary>Standard game, or Duo mode with the Rebels faction.</summary>
public enum GameMode
{
    Standard,
    Duo,
}

/// <summary>
/// How the board is assigned at setup: claimed in turn order
/// (the default, official setup), or dealt at random from the shuffled
/// board as a session option. Setup army placement is played by decision
/// either way; only the claiming step changes.
/// </summary>
public enum TerritorySetup
{
    ManualClaims,
    RandomDeal,
}

/// <summary>Mutable per-territory state: who holds it and with how many armies.</summary>
internal sealed class TerritoryState
{
    public string? Owner;
    public int Armies;
}

/// <summary>Mutable per-seat state. The Rebels are a seat with IsRebel set and no hand, ever.</summary>
internal sealed class PlayerState
{
    public required string Code;
    public bool Alive = true;
    public bool IsRebel;
    public List<Card> Hand = new();
}

/// <summary>
/// What the current attacker is owed in placements right now: free armies plus
/// a queue of forced territory-match placements. Shared by the
/// turn-start and inheritance placement flows, which differ only in context.
/// </summary>
internal sealed class PlacementState
{
    public int FreeArmies;
    public Queue<(IReadOnlyList<string> Candidates, int Armies)> MatchQueue = new();
    public bool AnyPending => FreeArmies > 0 || MatchQueue.Count > 0;
}

/// <summary>Bookkeeping for the active player's turn.</summary>
internal sealed class TurnState
{
    public required string Player;
    public int TerritoryCountAtStart;
    public int TerritoryYield;
    public List<(string Continent, int Bonus)> ContinentBonuses = new();
    public List<(int SetNumber, int Value)> Trades = new();
    public PlacementState Placement = new();
    public bool Conquered;

    /// <summary>
    /// The traded-card territory bonus is at most +2 per
    /// turn in total, claimed by the first set traded this turn that
    /// pictures a territory the trader occupies. Set when claimed;
    /// later trades in the same turn (including mid-turn inheritance
    /// trades) earn no second bonus.
    /// </summary>
    public bool CardBonusClaimed;
}

/// <summary>An attack in progress, across as many combat rounds as it takes.</summary>
internal sealed class BattleState
{
    public required string From;
    public required string To;
    public required string Attacker;
    public required string Defender;
    public AttackMode Mode;
    public int Rounds;
    public int TotalAttackerLosses;
    public int TotalDefenderLosses;

    /// <summary>Dice the attacker committed to for the round currently awaiting the defender.</summary>
    public int AttackerDiceForRound;

    /// <summary>Attacker dice in the most recent completed round; the capture move-in floor.</summary>
    public int LastAttackerDice;

    /// <summary>A finished round's log line held back until the capture move is known (the CAP suffix).</summary>
    public string? HeldLogLine;
}

/// <summary>The engine's pending-decision state; exactly one exists at any time in a live game.</summary>
internal abstract record PendingState
{
    public sealed record Claim(string Player) : PendingState;
    public sealed record ClaimForRebels(string Chooser) : PendingState;
    public sealed record SetupPlace(string Player) : PendingState;
    public sealed record Trade(string Player, PlacementContext Context) : PendingState;
    public sealed record Place(string Player, PlacementContext Context) : PendingState;
    public sealed record AttackDeclare(string Player) : PendingState;
    public sealed record AttackDice(BattleState Battle) : PendingState;
    public sealed record DefendDice(BattleState Battle) : PendingState;
    public sealed record CaptureMove(BattleState Battle) : PendingState;
    public sealed record Fortify(string Player) : PendingState;
    public sealed record GameOver(string Winner) : PendingState;
}
