using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AgentRisk.Client.Protocol;

/// <summary>One territory of the printed board: its code, full name, continent, and borders.</summary>
public sealed record TerritoryInfo(string Code, string Name, string ContinentCode, IReadOnlyList<string> Neighbors);

/// <summary>One continent of the printed board: code, full name, printed bonus, member codes.</summary>
public sealed record ContinentInfo(string Code, string Name, int Bonus, IReadOnlyList<string> Members);

/// <summary>
/// The static board reference: territory names, continent membership and
/// bonuses, adjacency, and the card pictured on each territory. This is
/// printed-on-the-box knowledge — the client holds it so it can
/// draw names on the map and enumerate the legal choices a prompt offers,
/// exactly as a human reads them off the physical board.
///
/// Two sources feed the same shape: the canonical board data file
/// (board.json, the build's single source of truth, shipped with the
/// client), and the briefing the host sends at the start of a game. The
/// client prefers the briefing — the table's own copy — when one has
/// arrived, and falls back to the shipped file for the sample board.
/// </summary>
public sealed class BoardCatalog
{
    private readonly Dictionary<string, TerritoryInfo> _territories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContinentInfo> _continents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cardTypes = new(StringComparer.Ordinal);

    private BoardCatalog() { }

    /// <summary>All territories, in the order the source listed them (board order).</summary>
    public List<TerritoryInfo> Territories { get; } = new();

    /// <summary>All continents, in the order the source listed them.</summary>
    public List<ContinentInfo> Continents { get; } = new();

    /// <summary>How many wild cards the deck holds (the shipped data file states it; a briefing does not).</summary>
    public int WildCardCount { get; private set; }

    public TerritoryInfo Territory(string code) => _territories[code];

    public ContinentInfo Continent(string code) => _continents[code];

    public bool TryTerritory(string code, out TerritoryInfo info) => _territories.TryGetValue(code, out info!);

    /// <summary>The card type pictured on a territory (INF/CAV/ART), or null when the source did not say.</summary>
    public string? CardTypeOf(string territoryCode) =>
        _cardTypes.TryGetValue(territoryCode, out string? type) ? type : null;

    /// <summary>Display name for a card id from a hand line: a wild card is just "Wild".</summary>
    public string CardDisplayName(string cardId)
    {
        if (cardId == "WLD") return "Wild card";
        int dash = cardId.LastIndexOf('-');
        string code = dash > 0 ? cardId[..dash] : cardId;
        string type = dash > 0 ? cardId[(dash + 1)..] : "";
        string name = TryTerritory(code, out var info) ? info.Name : code;
        return type.Length > 0 ? $"{name} ({CardTypeName(type)})" : name;
    }

    /// <summary>The spelled-out name of a card type code, for faces the player reads.</summary>
    public static string CardTypeName(string typeCode) => typeCode switch
    {
        "INF" => "Infantry",
        "CAV" => "Cavalry",
        "ART" => "Artillery",
        "WLD" => "Wild",
        _ => typeCode,
    };

    /// <summary>Builds the catalog from the canonical board data file's text.</summary>
    public static BoardCatalog FromBoardJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var catalog = new BoardCatalog();

        foreach (var element in root.GetProperty("continents").EnumerateArray())
        {
            var members = element.GetProperty("members").EnumerateArray().Select(m => m.GetString()!).ToList();
            var continent = new ContinentInfo(
                element.GetProperty("code").GetString()!,
                element.GetProperty("name").GetString()!,
                element.GetProperty("bonus").GetInt32(),
                members);
            catalog.Continents.Add(continent);
            catalog._continents[continent.Code] = continent;
        }

        foreach (var element in root.GetProperty("territories").EnumerateArray())
        {
            var neighbors = element.GetProperty("neighbors").EnumerateArray().Select(n => n.GetString()!).ToList();
            var territory = new TerritoryInfo(
                element.GetProperty("code").GetString()!,
                element.GetProperty("name").GetString()!,
                element.GetProperty("continent").GetString()!,
                neighbors);
            catalog.Territories.Add(territory);
            catalog._territories[territory.Code] = territory;
        }

        if (root.TryGetProperty("cards", out var cards))
            foreach (var card in cards.EnumerateObject())
                catalog._cardTypes[card.Name] = card.Value.GetString()!;

        if (root.TryGetProperty("wildCards", out var wild))
            catalog.WildCardCount = wild.GetInt32();

        return catalog;
    }

    /// <summary>
    /// Builds the catalog from the host's briefing block instead: continent
    /// lines carry membership and bonuses, territory lines carry names and
    /// borders. Card types are not part of the briefing; they stay unknown
    /// (null) rather than guessed.
    /// </summary>
    public static BoardCatalog FromBriefing(IEnumerable<string> briefingLines)
    {
        var catalog = new BoardCatalog();
        var continentPattern = new System.Text.RegularExpressions.Regex(
            @"^\[([A-Z]{2})\] (.+), bonus (\d+): ([A-Z ]+)$");
        var territoryPattern = new System.Text.RegularExpressions.Regex(
            @"^([A-Z]{3}) (.+) \(([A-Z]{2})\) borders: ([A-Z ]+)$");

        foreach (string line in briefingLines)
        {
            var continentMatch = continentPattern.Match(line);
            if (continentMatch.Success)
            {
                var members = continentMatch.Groups[4].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var continent = new ContinentInfo(
                    continentMatch.Groups[1].Value,
                    continentMatch.Groups[2].Value,
                    int.Parse(continentMatch.Groups[3].Value),
                    members);
                catalog.Continents.Add(continent);
                catalog._continents[continent.Code] = continent;
                continue;
            }

            var territoryMatch = territoryPattern.Match(line);
            if (territoryMatch.Success)
            {
                var neighbors = territoryMatch.Groups[4].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var territory = new TerritoryInfo(
                    territoryMatch.Groups[1].Value,
                    territoryMatch.Groups[2].Value,
                    territoryMatch.Groups[3].Value,
                    neighbors);
                catalog.Territories.Add(territory);
                catalog._territories[territory.Code] = territory;
            }
        }

        return catalog;
    }
}
