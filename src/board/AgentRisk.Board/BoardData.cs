using System.Text.Json.Serialization;

namespace AgentRisk.Board;

/// <summary>
/// The raw board data exactly as stored in board.json, before validation and
/// indexing. This is the deserialization shape only; game code should consume
/// the validated, immutable <see cref="Board"/> produced by
/// <see cref="BoardLoader"/> instead.
/// </summary>
public sealed class BoardData
{
    [JsonPropertyName("territories")]
    public List<TerritoryData> Territories { get; set; } = [];

    [JsonPropertyName("continents")]
    public List<ContinentData> Continents { get; set; } = [];

    /// <summary>Territory code -> card type code ("INF" / "CAV" / "ART") pictured on its card.</summary>
    [JsonPropertyName("cards")]
    public Dictionary<string, string> Cards { get; set; } = [];

    /// <summary>Number of wild cards in the deck; must equal <see cref="BoardConstants.WildCardCount"/>.</summary>
    [JsonPropertyName("wildCards")]
    public int WildCards { get; set; }
}

/// <summary>One territory entry in board.json.</summary>
public sealed class TerritoryData
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("continent")]
    public string Continent { get; set; } = "";

    [JsonPropertyName("neighbors")]
    public List<string> Neighbors { get; set; } = [];
}

/// <summary>One continent entry in board.json.</summary>
public sealed class ContinentData
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("bonus")]
    public int Bonus { get; set; }

    [JsonPropertyName("members")]
    public List<string> Members { get; set; } = [];
}
