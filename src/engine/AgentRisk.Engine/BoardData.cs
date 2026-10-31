using AgentRisk.Board;
using BoardCardType = AgentRisk.Board.CardType;
using BoardModel = AgentRisk.Board.Board;

namespace AgentRisk.Engine;

/// <summary>Static facts about one territory from the canonical board data.</summary>
public sealed record TerritoryInfo(string Code, string Name, string Continent, IReadOnlyList<string> Neighbors);

/// <summary>Static facts about one continent: its bonus and member territory codes.</summary>
public sealed record ContinentInfo(string Code, string Name, int Bonus, IReadOnlyList<string> Members);

/// <summary>
/// Read-only access to the canonical board: territories, continents, adjacency
/// and the deck's card assignment. The engine depends only on this interface;
/// the data behind it is the validated AgentRisk.Board model (see
/// <see cref="BoardProjectBoardData"/>), the single source of truth.
/// </summary>
public interface IBoardData
{
    IReadOnlyList<TerritoryInfo> Territories { get; }
    IReadOnlyList<ContinentInfo> Continents { get; }
    TerritoryInfo Territory(string code);
    ContinentInfo Continent(string code);

    /// <summary>The card type the canonical deck assigns to a territory.</summary>
    CardType CardTypeOf(string territoryCode);

    bool AreAdjacent(string a, string b);
}

/// <summary>
/// Adapts the validated <see cref="Board"/> from the AgentRisk.Board project
/// to the engine's <see cref="IBoardData"/>. This is the engine's only board
/// source: board.json parsing and validation live in AgentRisk.Board
/// (<see cref="BoardLoader"/>), never duplicated here.
/// </summary>
public sealed class BoardProjectBoardData : IBoardData
{
    private readonly BoardModel _board;
    private readonly Dictionary<string, TerritoryInfo> _territories;
    private readonly Dictionary<string, ContinentInfo> _continents;

    private BoardProjectBoardData(BoardModel board)
    {
        _board = board;
        _territories = board.Territories.ToDictionary(
            t => t.Code,
            t => new TerritoryInfo(t.Code, t.Name, t.ContinentCode, t.Neighbors),
            StringComparer.Ordinal);
        _continents = board.Continents.ToDictionary(
            c => c.Code,
            c => new ContinentInfo(c.Code, c.Name, c.Bonus, c.Members),
            StringComparer.Ordinal);
        Territories = _territories.Values.OrderBy(t => t.Code, StringComparer.Ordinal).ToList();
        Continents = _continents.Values.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
    }

    public static BoardProjectBoardData FromBoard(BoardModel board) => new(board);

    /// <summary>Loads and validates board.json at <paramref name="path"/> via AgentRisk.Board, then adapts it.</summary>
    public static BoardProjectBoardData LoadFromFile(string path) => new(BoardLoader.Load(path));

    public IReadOnlyList<TerritoryInfo> Territories { get; }
    public IReadOnlyList<ContinentInfo> Continents { get; }

    public TerritoryInfo Territory(string code) =>
        _territories.TryGetValue(code, out var info)
            ? info
            : throw new ArgumentException($"Unknown territory code '{code}'.", nameof(code));

    public ContinentInfo Continent(string code) =>
        _continents.TryGetValue(code, out var info)
            ? info
            : throw new ArgumentException($"Unknown continent code '{code}'.", nameof(code));

    public CardType CardTypeOf(string territoryCode) => _board.CardOf(territoryCode) switch
    {
        BoardCardType.Infantry => CardType.Infantry,
        BoardCardType.Cavalry => CardType.Cavalry,
        BoardCardType.Artillery => CardType.Artillery,
        _ => throw new ArgumentException($"No card assigned to territory '{territoryCode}'.", nameof(territoryCode)),
    };

    public bool AreAdjacent(string a, string b) => _board.AreNeighbors(a, b);
}
