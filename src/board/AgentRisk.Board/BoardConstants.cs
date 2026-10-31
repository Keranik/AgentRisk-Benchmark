namespace AgentRisk.Board;

/// <summary>
/// The fixed shape of the classic world board. These are the numbers the
/// rest of the system is allowed to rely on; keeping them named and in one
/// place means the board data and the code can be checked against each
/// other at a glance.
/// </summary>
public static class BoardConstants
{
    /// <summary>Total territories on the classic board.</summary>
    public const int TerritoryCount = 42;

    /// <summary>Total continents on the classic board.</summary>
    public const int ContinentCount = 6;

    /// <summary>
    /// Total distinct borders between territories (sum of all degrees / 2).
    /// Verified against the canonical board data (src/board/board.json).
    /// </summary>
    public const int BorderCount = 83;

    /// <summary>Smallest number of neighbors any territory has (Japan, Madagascar, Argentina, Eastern Australia).</summary>
    public const int MinimumDegree = 2;

    /// <summary>Cards of each type (INF / CAV / ART) in the territory deck: the deck is balanced 14/14/14.</summary>
    public const int CardsPerType = 14;

    /// <summary>Wild cards in the deck.</summary>
    public const int WildCardCount = 2;

    /// <summary>Total cards a player can ever draw from: one per territory plus the wild cards.</summary>
    public const int DeckSize = TerritoryCount + WildCardCount;
}
