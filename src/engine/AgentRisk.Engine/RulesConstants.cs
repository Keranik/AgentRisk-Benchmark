namespace AgentRisk.Engine;

/// <summary>
/// Every rules number in the game, named and in one place (no magic numbers
/// in engine code). Values are the printed rules; anything that is
/// a design tunable rather than a printed rule is marked as such.
/// </summary>
public static class RulesConstants
{
    /// <summary>Player seat codes in table order.</summary>
    public static readonly string[] PlayerCodes = { "RED", "BLU", "GRN", "YEL", "PNK", "BLK" };

    /// <summary>The Rebels faction code in Duo mode.</summary>
    public const string RebelCode = "REB";

    public const int MinPlayers = 2;
    public const int MaxPlayers = 6;
    public const int TotalTerritories = 42;

    /// <summary>Starting armies by player count: 2P 40, 3P 35, 4P 30, 5P 25, 6P 20.</summary>
    public static readonly IReadOnlyDictionary<int, int> StartingArmiesByPlayerCount =
        new Dictionary<int, int> { [2] = 40, [3] = 35, [4] = 30, [5] = 25, [6] = 20 };

    // --- Reinforcement ---

    /// <summary>Territory income is floor(territories / TerritoriesPerArmyDivisor), floored at MinTerritoryIncome.</summary>
    public const int TerritoriesPerArmyDivisor = 3;
    public const int MinTerritoryIncome = 3;

    // --- Combat ---

    public const int MaxAttackerDice = 3;
    public const int MaxDefenderDice = 2;
    public const int MinDice = 1;

    /// <summary>Armies that must always stay behind: attacking, capture move-ins and fortifies never empty a territory.</summary>
    public const int MinArmiesLeftBehind = 1;

    /// <summary>The floor under every territory, in play and under a host edit alike: no territory ever stands at zero.</summary>
    public const int MinArmiesOnTerritory = 1;

    // --- Cards ---

    public const int WildCardCount = 2;
    public const int CardsPerSet = 3;

    /// <summary>Trade-in values for sets 1..6; every later set adds TradeValueStepAfterBase.</summary>
    public static readonly int[] TradeValueSchedule = { 4, 6, 8, 10, 12, 15 };
    public const int TradeValueStepAfterBase = 5;

    /// <summary>Bonus armies for a traded card picturing a territory the trader owns; forced onto that territory.</summary>
    public const int TerritoryMatchBonus = 2;

    /// <summary>Holding this many cards at turn start forces a trade before placement.</summary>
    public const int ForcedTradeThreshold = 5;

    /// <summary>After inheriting an eliminated player's cards, holding this many forces immediate mid-turn trades.</summary>
    public const int InheritanceForcedTradeThreshold = 6;

    /// <summary>Inheritance trades continue until the hand is at or below this size.</summary>
    public const int InheritanceTradeDownTarget = 4;

    /// <summary>Cards drawn at end of turn by a player who conquered at least one territory.</summary>
    public const int ConquestCardDraw = 1;

    // --- Duo mode / Rebels ---

    /// <summary>Tunable: the Rebels start with this multiple of a solo player's starting armies (1.5x: 40 becomes 60).</summary>
    public const double RebelStartingArmyMultiplier = 1.5;
}
