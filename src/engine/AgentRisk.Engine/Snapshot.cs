namespace AgentRisk.Engine;

/// <summary>Plain-data view of one territory: owner seat code (null while unclaimed) and army count.</summary>
public sealed record TerritorySnapshot(string Code, string? Owner, int Armies);

/// <summary>
/// Plain-data view of one seat. The hand is included because the engine runs
/// host-side and is trusted; hiding other players' faces is the host's job
/// when it formats prompts.
/// </summary>
public sealed record PlayerSnapshot(
    string Code,
    bool Alive,
    bool IsRebel,
    int TerritoryCount,
    int TotalArmies,
    IReadOnlyList<string> Hand);

/// <summary>
/// The reinforcement count broken out by source: the table ritual
/// the agent is entitled to hear in full. Trade entries accumulate over the
/// turn's trade phase; Total is computed after the last trade.
/// </summary>
public sealed record EarnBreakdown(
    int TerritoryCount,
    int TerritoryYield,
    IReadOnlyList<(string Continent, int Bonus)> Continents,
    IReadOnlyList<(int SetNumber, int Value)> Trades,
    int Total);

/// <summary>
/// Audit record for one combat round: every die face, so any game
/// can be replayed and checked. Blitz rounds each get their own record even
/// though the summary log compresses them into one ALL line.
/// </summary>
public sealed record DiceRollRecord(
    int Index,
    string Attacker,
    string Defender,
    string From,
    string To,
    AttackMode Mode,
    int[] AttackerFaces,
    int[] DefenderFaces,
    int AttackerLosses,
    int DefenderLosses);

/// <summary>A whole-game plain-data snapshot; records and lists only, no engine internals.</summary>
public sealed record GameSnapshot(
    IReadOnlyList<TerritorySnapshot> Territories,
    IReadOnlyList<PlayerSnapshot> Players,
    string? CurrentTurnSeat,
    int Round,
    int NextTradeValue,
    int SetsTraded,
    string? Winner,
    bool IsGameOver);
