using System.Text.Json;

namespace AgentRisk.Board;

/// <summary>
/// Loads board.json into a validated, immutable <see cref="Board"/>.
/// The canonical board.json ships as content with this library, so
/// <see cref="Load()"/> finds the same file for every consumer; the path and
/// JSON overloads exist for tests and tooling. Any validation problem aborts
/// the load with a <see cref="BoardValidationException"/> listing all of them.
/// </summary>
public static class BoardLoader
{
    /// <summary>File name of the canonical board data.</summary>
    public const string BoardFileName = "board.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>Loads the board.json copied alongside the calling application.</summary>
    public static Board Load() => Load(Path.Combine(AppContext.BaseDirectory, BoardFileName));

    /// <summary>Loads and validates the board data at <paramref name="path"/>.</summary>
    /// <exception cref="BoardValidationException">The data failed validation.</exception>
    public static Board Load(string path)
    {
        var json = File.ReadAllText(path);
        return FromJson(json, path);
    }

    /// <summary>Parses, validates, and indexes board data from a JSON string.</summary>
    /// <param name="json">The board data JSON.</param>
    /// <param name="source">Description of where the JSON came from, used in error messages.</param>
    /// <exception cref="BoardValidationException">The data failed validation.</exception>
    public static Board FromJson(string json, string source = "<json string>")
    {
        var data = JsonSerializer.Deserialize<BoardData>(json, JsonOptions)
            ?? throw new BoardValidationException(source, ["board data is empty"]);

        var problems = BoardValidator.Validate(data);
        if (problems.Count > 0)
        {
            throw new BoardValidationException(source, problems);
        }

        return Build(data);
    }

    /// <summary>Builds the immutable indexed model from already-validated data.</summary>
    private static Board Build(BoardData data)
    {
        var territories = data.Territories
            .Select(t => new Territory(
                t.Code,
                t.Name,
                t.Continent,
                Array.AsReadOnly(t.Neighbors.ToArray())))
            .ToList();

        var continents = data.Continents
            .Select(c => new Continent(
                c.Code,
                c.Name,
                c.Bonus,
                Array.AsReadOnly(c.Members.ToArray())))
            .ToList();

        var cardByTerritory = data.Cards.ToDictionary(
            pair => pair.Key, pair => CardTypeCode.Parse(pair.Value));

        return new Board(territories, continents, cardByTerritory, data.WildCards);
    }
}
