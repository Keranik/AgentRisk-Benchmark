using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// Where each territory sits on the board canvas, and where each continent
/// badge is anchored. The positions are data, not code (data/layout.json),
/// so the map arrangement can be reviewed and adjusted without touching
/// the scene: each territory anchor is where the printed board sets the
/// territory's name (the same point as its map.json label), and each
/// continent anchor is the center of the printed board's ocean badge
/// for that continent, measured on the rectified board photo.
/// </summary>
public sealed class TerritoryLayout
{
    private readonly Dictionary<string, Vector2> _positions = new();
    private readonly Dictionary<string, Vector2> _continentLabels = new();

    private TerritoryLayout() { }

    /// <summary>The board canvas size the positions were authored for.</summary>
    public Vector2 CanvasSize { get; private set; } = new(2048, 1384);

    public Vector2 PositionOf(string territoryCode) => _positions[territoryCode];

    public bool TryPositionOf(string territoryCode, out Vector2 position) =>
        _positions.TryGetValue(territoryCode, out position);

    public Vector2 ContinentLabelAnchor(string continentCode) =>
        _continentLabels.TryGetValue(continentCode, out var anchor) ? anchor : Vector2.Zero;

    /// <summary>Parses the layout file's text (kept Godot-free so the shape stays testable).</summary>
    public static TerritoryLayout FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var layout = new TerritoryLayout();

        var canvas = root.GetProperty("canvas");
        layout.CanvasSize = new Vector2(
            canvas.GetProperty("width").GetSingle(),
            canvas.GetProperty("height").GetSingle());

        foreach (var territory in root.GetProperty("territories").EnumerateArray())
        {
            layout._positions[territory.GetProperty("code").GetString()!] = new Vector2(
                territory.GetProperty("x").GetSingle(),
                territory.GetProperty("y").GetSingle());
        }

        foreach (var label in root.GetProperty("continentLabels").EnumerateArray())
        {
            layout._continentLabels[label.GetProperty("code").GetString()!] = new Vector2(
                label.GetProperty("labelX").GetSingle(),
                label.GetProperty("labelY").GetSingle());
        }

        return layout;
    }
}
