using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AgentRisk.Client.Protocol;

/// <summary>One point in the map canvas's coordinate space.</summary>
public readonly record struct MapPoint(float X, float Y);

/// <summary>
/// One territory's drawn shape: its outline rings and where its name
/// sits. A territory is one or more rings: mainland
/// territories carry a single ring, island territories several, and a
/// ring is the traced outer boundary of one land piece — lakes inside a
/// piece are part of its ring, the way the printed board paints them.
/// </summary>
public sealed record MapTerritory(string Code, string Name, IReadOnlyList<IReadOnlyList<MapPoint>> Polygons, MapPoint Label);

/// <summary>
/// The drawn map (client/data/map.json): the sole geometry truth for the
/// table. Polygons, label anchors, the lakes the printed board paints
/// inside its land (Great Lakes, Baltic, White Sea), and the sea links
/// between territories whose drawn shapes do not touch. The canvas is the
/// printed board's coordinate space, in the board's own proportions;
/// rendering scales it to whatever rect the table gives.
/// </summary>
public sealed class MapData
{
    private readonly Dictionary<string, MapTerritory> _byCode = new(StringComparer.Ordinal);

    private MapData() { }

    /// <summary>Canvas width and height in map coordinates.</summary>
    public MapPoint Canvas { get; private set; }

    /// <summary>All territories, in the order the dataset lists them.</summary>
    public List<MapTerritory> Territories { get; } = new();

    /// <summary>Pairs of territory codes joined by a drawn sea link.</summary>
    public List<(string From, string To)> SeaLinks { get; } = new();

    /// <summary>
    /// Enclosed water inside the land, drawn over the territories. A lake
    /// belongs to no territory: the shapes beneath it run through it, and
    /// it hides them, the way the printed board's water does.
    /// </summary>
    public List<IReadOnlyList<MapPoint>> Lakes { get; } = new();

    public MapTerritory Territory(string code) => _byCode[code];

    public bool TryTerritory(string code, out MapTerritory territory) =>
        _byCode.TryGetValue(code, out territory!);

    public static MapData FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var map = new MapData();

        var canvas = root.GetProperty("canvas");
        map.Canvas = new MapPoint(canvas[0].GetSingle(), canvas[1].GetSingle());

        foreach (var entry in root.GetProperty("territories").EnumerateObject())
        {
            var polygons = new List<IReadOnlyList<MapPoint>>();
            foreach (var ring in entry.Value.GetProperty("polygons").EnumerateArray())
                polygons.Add(Ring(ring));
            var label = entry.Value.GetProperty("label");
            var territory = new MapTerritory(
                entry.Name,
                entry.Value.GetProperty("name").GetString()!,
                polygons,
                new MapPoint(label[0].GetSingle(), label[1].GetSingle()));
            map.Territories.Add(territory);
            map._byCode[territory.Code] = territory;
        }

        foreach (var link in root.GetProperty("seaLinks").EnumerateArray())
            map.SeaLinks.Add((link[0].GetString()!, link[1].GetString()!));

        if (root.TryGetProperty("lakes", out var lakes))
            foreach (var ring in lakes.EnumerateArray())
                map.Lakes.Add(Ring(ring));

        return map;
    }

    private static List<MapPoint> Ring(JsonElement ring)
    {
        var points = new List<MapPoint>();
        foreach (var point in ring.EnumerateArray())
            points.Add(new MapPoint(point[0].GetSingle(), point[1].GetSingle()));
        return points;
    }
}
