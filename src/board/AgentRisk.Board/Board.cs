namespace AgentRisk.Board;

/// <summary>
/// The validated, immutable board: the single source of truth the engine,
/// the prompt builder, and the agent briefings all read from. Instances are
/// built by <see cref="BoardLoader"/> from data that has passed
/// <see cref="BoardValidator"/>, so lookups here can assume the data is
/// internally consistent. Unknown codes are rejected with
/// <see cref="KeyNotFoundException"/>.
/// </summary>
public sealed class Board
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _neighborsByCode;
    private readonly IReadOnlyDictionary<string, CardType> _cardByTerritory;

    internal Board(
        IReadOnlyList<Territory> territories,
        IReadOnlyList<Continent> continents,
        IReadOnlyDictionary<string, CardType> cardByTerritory,
        int wildCards)
    {
        Territories = territories;
        Continents = continents;
        WildCards = wildCards;
        _cardByTerritory = cardByTerritory;

        TerritoryByCode = territories.ToDictionary(t => t.Code);
        ContinentByCode = continents.ToDictionary(c => c.Code);
        _neighborsByCode = territories.ToDictionary(
            t => t.Code, t => (IReadOnlySet<string>)new HashSet<string>(t.Neighbors));
    }

    /// <summary>All territories, in board.json order (continent by continent, then by code).</summary>
    public IReadOnlyList<Territory> Territories { get; }

    /// <summary>All continents, in board.json order (NA, SA, EU, AF, AS, AU).</summary>
    public IReadOnlyList<Continent> Continents { get; }

    /// <summary>Territory code -> territory.</summary>
    public IReadOnlyDictionary<string, Territory> TerritoryByCode { get; }

    /// <summary>Continent code -> continent.</summary>
    public IReadOnlyDictionary<string, Continent> ContinentByCode { get; }

    /// <summary>Number of wild cards in the deck.</summary>
    public int WildCards { get; }

    /// <summary>The territory with this code.</summary>
    public Territory GetTerritory(string code) =>
        TerritoryByCode.TryGetValue(code, out var territory)
            ? territory
            : throw new KeyNotFoundException($"Unknown territory code \"{code}\".");

    /// <summary>The continent with this code.</summary>
    public Continent GetContinent(string code) =>
        ContinentByCode.TryGetValue(code, out var continent)
            ? continent
            : throw new KeyNotFoundException($"Unknown continent code \"{code}\".");

    /// <summary>True when a territory with this code exists on the board.</summary>
    public bool HasTerritory(string code) => TerritoryByCode.ContainsKey(code);

    /// <summary>The continent a territory belongs to.</summary>
    public Continent ContinentOf(string territoryCode) => GetContinent(GetTerritory(territoryCode).ContinentCode);

    /// <summary>The member territory codes of a continent.</summary>
    public IReadOnlyList<string> MembersOf(string continentCode) => GetContinent(continentCode).Members;

    /// <summary>The set of territory codes bordering this territory.</summary>
    public IReadOnlySet<string> NeighborsOf(string territoryCode)
    {
        GetTerritory(territoryCode); // reject unknown codes with the standard error
        return _neighborsByCode[territoryCode];
    }

    /// <summary>True when the two territories share a border (sea routes included).</summary>
    public bool AreNeighbors(string firstCode, string secondCode) => NeighborsOf(firstCode).Contains(secondCode);

    /// <summary>Number of bordering territories (graph degree).</summary>
    public int Degree(string territoryCode) => NeighborsOf(territoryCode).Count;

    /// <summary>Total distinct borders on the board: half the sum of all degrees.</summary>
    public int BorderCount => Territories.Sum(t => t.Degree) / 2;

    /// <summary>The card type pictured on a territory's card.</summary>
    public CardType CardOf(string territoryCode)
    {
        GetTerritory(territoryCode);
        return _cardByTerritory[territoryCode];
    }
}
