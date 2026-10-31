using System;
using System.Collections.Generic;
using AgentRisk.Client.Protocol;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// The map: client/data/map.json rendered as drawn — every territory's
/// rings filled with its owner's seat colour, exactly that colour on
/// every continent so a player's land reads as one at a glance, and
/// unclaimed land in the printed board's pale continent tints, on
/// parchment, with the board's inland lakes painted back as
/// water on top and the sea links dashed between the labels of the
/// territories they join. The dataset is the only geometry this view
/// knows: names and army counts sit at each territory's label
/// anchor, continent names and bonuses stand as board text, and a click
/// resolves to the territory whose polygon holds it (falling back to the
/// nearest label, so small island shapes stay easy to press).
///
/// Ownership is said three ways that always agree: the fill, a chip in
/// the owner's colour carrying the army count, and the same colour in
/// every panel beside the map (<see cref="UiConstants.SeatColor"/> is
/// the one source). Continents stay legible without tinting owned land:
/// borders between continents are drawn heavier than the rest.
///
/// Vintage treatment lives here and nowhere else: the board is a printed
/// artifact, the chrome around it is not.
/// </summary>
public partial class PolygonBoardView : Control
{
    private BoardCatalog? _catalog;
    private MapData? _map;
    private TerritoryLayout? _layout;
    private BoardState? _state;
    private Font? _font;

    private readonly HashSet<string> _selectable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _targets = new(StringComparer.Ordinal);
    private string? _selected;

    // Border segments between territories of different continents, in
    // canvas units, found once per board from the shared ring vertices.
    private readonly List<(MapPoint A, MapPoint B)> _continentBorders = new();

    /// <summary>Raised when the player clicks a territory.</summary>
    public event Action<string>? TerritoryClicked;

    public override void _Ready()
    {
        _font = GetThemeDefaultFont();
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>Shows the map: catalog, dataset, and continent label anchors once, then the live state.</summary>
    public void SetBoard(BoardCatalog catalog, MapData map, TerritoryLayout layout, BoardState? state)
    {
        _catalog = catalog;
        _map = map;
        _layout = layout;
        _state = state;
        FindContinentBorders();
        QueueRedraw();
    }

    /// <summary>
    /// Neighbouring territories share their border vertex for vertex
    /// (the dataset's own guarantee), so a ring segment that also stands,
    /// reversed or not, in a territory of another continent is a stretch
    /// of continent border.
    /// </summary>
    private void FindContinentBorders()
    {
        _continentBorders.Clear();
        if (_map is null || _catalog is null) return;
        static (long, long) Key(MapPoint p) => ((long)MathF.Round(p.X * 2f), (long)MathF.Round(p.Y * 2f));
        var owners = new Dictionary<((long, long), (long, long)), string>();
        foreach (var territory in _map.Territories)
        {
            if (!_catalog.TryTerritory(territory.Code, out var info)) continue;
            foreach (var ring in territory.Polygons)
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    var ka = Key(a);
                    var kb = Key(b);
                    var key = ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
                    if (owners.TryGetValue(key, out string? continent))
                    {
                        if (continent != info.ContinentCode) _continentBorders.Add((a, b));
                    }
                    else
                    {
                        owners[key] = info.ContinentCode;
                    }
                }
            }
        }
    }

    /// <summary>Updates just the live state (owners and armies).</summary>
    public void SetState(BoardState? state)
    {
        _state = state;
        QueueRedraw();
    }

    /// <summary>Rings the territories a decision can pick, as the disc board did.</summary>
    public void SetHighlights(IEnumerable<string> selectable, string? selected, IEnumerable<string> targets)
    {
        _selectable.Clear();
        foreach (string code in selectable) _selectable.Add(code);
        _targets.Clear();
        foreach (string code in targets) _targets.Add(code);
        _selected = selected;
        QueueRedraw();
    }

    public void ClearHighlights() => SetHighlights(Array.Empty<string>(), null, Array.Empty<string>());

    // ------------------------------------------------------------------
    // Drawing
    // ------------------------------------------------------------------

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), UiConstants.ParchmentColor);
        if (_catalog is null || _map is null || _layout is null || _font is null) return;

        var (offset, scale) = FitTransform();
        Vector2 At(MapPoint point) => offset + new Vector2(point.X, point.Y) * scale;
        Vector2 LabelOf(string code) => At(_map.Territory(code).Label);

        DrawSeaLinks(At, LabelOf, scale);
        DrawTerritories(At, scale);
        foreach (var (a, b) in _continentBorders)
            DrawLine(At(a), At(b), UiConstants.MapInkColor, UiConstants.ContinentBorderWidth, antialiased: true);
        DrawLakes(At);
        DrawContinentLabels(offset, scale);
        DrawLabels(LabelOf, scale);
        DrawRings(LabelOf, scale);
    }

    private void DrawSeaLinks(Func<MapPoint, Vector2> at, Func<string, Vector2> labelOf, float scale)
    {
        var ink = new Color(UiConstants.MapInkColor, 0.55f);
        foreach (var (from, to) in _map!.SeaLinks)
        {
            if (!_map.TryTerritory(from, out var a) || !_map.TryTerritory(to, out var b)) continue;
            // Alaska–Kamchatka joins through the map edges (the dataset's
            // one wrap pair): out the left edge from Alaska, in the right
            // edge to Kamchatka, at the labels' heights.
            if ((from == "ALA" && to == "KAM") || (from == "KAM" && to == "ALA"))
            {
                var ala = _map.Territory("ALA").Label;
                var kam = _map.Territory("KAM").Label;
                DrawDashed(at(ala), at(new MapPoint(0, ala.Y)), ink, scale);
                DrawDashed(at(new MapPoint(_map.Canvas.X, kam.Y)), at(kam), ink, scale);
                continue;
            }
            DrawDashed(at(a.Label), at(b.Label), ink, scale);
        }
    }

    private void DrawDashed(Vector2 from, Vector2 to, Color color, float scale)
    {
        float length = from.DistanceTo(to);
        if (length <= 0) return;
        var direction = (to - from) / length;
        float dash = UiConstants.SeaLinkDash * scale;
        float gap = UiConstants.SeaLinkGap * scale;
        for (float at = 0; at < length; at += dash + gap)
        {
            float end = Math.Min(at + dash, length);
            DrawLine(from + direction * at, from + direction * end, color, 2f);
        }
    }

    private void DrawTerritories(Func<MapPoint, Vector2> at, float scale)
    {
        foreach (var territory in _map!.Territories)
        {
            Color fill = FillFor(territory.Code);
            bool selected = _selected == territory.Code;
            // Every ring is drawn: island territories are several
            // separate land pieces with one owner and one outline ink.
            foreach (var ring in territory.Polygons)
            {
                var points = new Vector2[ring.Count];
                for (int i = 0; i < points.Length; i++) points[i] = at(ring[i]);
                if (points.Length < 3) continue;

                DrawColoredPolygon(points, fill);

                var outline = new Vector2[points.Length + 1];
                Array.Copy(points, outline, points.Length);
                outline[points.Length] = points[0];
                DrawPolyline(outline,
                    selected ? UiConstants.SelectedRingColor : UiConstants.MapInkColor,
                    selected ? 3.5f : 1.6f);
            }
        }
    }

    /// <summary>
    /// The water the printed board paints inside its land. The territory
    /// shapes run beneath a lake; drawing it after them, in the sea's own
    /// parchment with the coast ink, hides them the way the board does.
    /// </summary>
    private void DrawLakes(Func<MapPoint, Vector2> at)
    {
        foreach (var lake in _map!.Lakes)
        {
            if (lake.Count < 3) continue;
            var points = new Vector2[lake.Count];
            for (int i = 0; i < points.Length; i++) points[i] = at(lake[i]);
            DrawColoredPolygon(points, UiConstants.ParchmentColor);

            var outline = new Vector2[points.Length + 1];
            Array.Copy(points, outline, points.Length);
            outline[points.Length] = points[0];
            DrawPolyline(outline, UiConstants.MapInkColor, 1.6f);
        }
    }

    private Color FillFor(string code)
    {
        var tint = _catalog is not null && _catalog.TryTerritory(code, out var info)
            ? UiConstants.ContinentColor(info.ContinentCode)
            : new Color(0.75f, 0.75f, 0.75f);
        string? owner = null;
        if (_state is not null && _state.Territories.TryGetValue(code, out var live))
            owner = live.Owner;
        // Unclaimed land keeps the board's pale continent tint; owned
        // land takes its owner's colour exactly, the same on every
        // continent and in every panel.
        return owner is null
            ? new Color(tint, UiConstants.UnclaimedTintAlpha)
            : UiConstants.SeatColor(owner);
    }

    /// <summary>
    /// Continent badges: a parchment disc in open water near each
    /// continent (the layout anchor is its center), the bonus number
    /// large inside it and the continent's name small beneath — the
    /// physical board's ocean badges, in this board's own plain
    /// plaque treatment rather than any printed ornament.
    /// </summary>
    private void DrawContinentLabels(Vector2 offset, float scale)
    {
        foreach (var continent in _catalog!.Continents)
        {
            Vector2 center = offset + _layout!.ContinentLabelAnchor(continent.Code) * scale;
            if (center == offset) continue; // no anchor recorded for this continent
            var ink = UiConstants.MapInkColor;
            float radius = UiConstants.ContinentBadgeRadius * scale;

            DrawCircle(center + new Vector2(2f, 3f) * scale, radius, new Color(ink, 0.22f));
            DrawCircle(center, radius, UiConstants.ParchmentColor);
            DrawArc(center, radius, 0, Mathf.Tau, 64, ink, 2.5f);

            string number = continent.Bonus.ToString();
            int numberSize = Mathf.RoundToInt(UiConstants.ContinentBadgeNumberFontSize * scale);
            float numberWidth = 120f * scale;
            DrawString(_font!, center + new Vector2(-numberWidth / 2f, numberSize * 0.36f),
                number, HorizontalAlignment.Center, numberWidth, numberSize, ink);

            string name = continent.Name.ToUpperInvariant();
            int nameSize = Mathf.RoundToInt(UiConstants.ContinentBadgeNameFontSize * scale);
            float nameWidth = 360f * scale;
            Vector2 nameAt = center + new Vector2(-nameWidth / 2f, radius + nameSize + 6f * scale);
            DrawString(_font!, nameAt + new Vector2(1, 1), name, HorizontalAlignment.Center,
                nameWidth, nameSize, new Color(UiConstants.ParchmentColor, 0.85f));
            DrawString(_font!, nameAt, name, HorizontalAlignment.Center,
                nameWidth, nameSize, new Color(ink, 0.9f));
        }
    }

    private void DrawLabels(Func<string, Vector2> labelOf, float scale)
    {
        var ink = UiConstants.MapInkColor;
        var halo = UiConstants.ParchmentColor;
        int nameSize = Mathf.RoundToInt(UiConstants.MapNameFontSize * scale);
        int armySize = Mathf.RoundToInt(UiConstants.MapArmyFontSize * scale);
        int outline = Math.Max(2, Mathf.RoundToInt(5f * scale));
        float width = 260f * scale;
        float chipRadius = UiConstants.ArmyChipRadius * scale;

        foreach (var territory in _map!.Territories)
        {
            Vector2 label = labelOf(territory.Code);
            string name = _catalog is not null && _catalog.TryTerritory(territory.Code, out var info)
                ? info.Name
                : territory.Name;

            // The name in ink inside a parchment outline, readable on any
            // owner's fill, light or dark.
            Vector2 nameAt = label + new Vector2(-width / 2f, -4f * scale);
            DrawStringOutline(_font!, nameAt, name, HorizontalAlignment.Center, width, nameSize, outline, halo);
            DrawString(_font!, nameAt, name, HorizontalAlignment.Center, width, nameSize, ink);

            // The army count on a chip in the owner's colour: ownership
            // reads even where the land itself is a sliver or an island.
            if (_state is null || !_state.Territories.TryGetValue(territory.Code, out var live) || live.Owner is null)
                continue;
            var color = UiConstants.SeatColor(live.Owner);
            Vector2 center = ChipCenter(label, scale);
            string count = live.Armies.ToString();
            // A disc for short counts, stretched into a pill when the
            // digits need more room than the disc holds.
            float textWidth = _font!.GetStringSize(count, HorizontalAlignment.Left, -1, armySize).X;
            float halfStretch = Math.Max(0f, (textWidth + 10f * scale - chipRadius * 2f) / 2f);
            DrawChip(center, chipRadius + 2.5f * scale, halfStretch, ink);
            DrawChip(center, chipRadius + 1f * scale, halfStretch, halo);
            DrawChip(center, chipRadius - 0.5f * scale, halfStretch, color);
            var text = UiConstants.TextOn(color);
            float box = chipRadius * 2f + halfStretch * 2f;
            Vector2 countAt = center + new Vector2(-box / 2f, armySize * 0.36f);
            DrawString(_font!, countAt, count, HorizontalAlignment.Center, box, armySize, text);
        }
    }

    /// <summary>A disc, or a pill stretched sideways by the given half-length.</summary>
    private void DrawChip(Vector2 center, float radius, float halfStretch, Color color)
    {
        if (halfStretch <= 0f)
        {
            DrawCircle(center, radius, color);
            return;
        }
        var offset = new Vector2(halfStretch, 0);
        DrawCircle(center - offset, radius, color);
        DrawCircle(center + offset, radius, color);
        DrawRect(new Rect2(center - offset - new Vector2(0, radius), new Vector2(halfStretch * 2f, radius * 2f)), color);
    }

    /// <summary>Where a territory's army chip sits: just below its name.</summary>
    private static Vector2 ChipCenter(Vector2 label, float scale) =>
        label + new Vector2(0, (UiConstants.MapNameFontSize * 0.5f + UiConstants.ArmyChipRadius + 2f) * scale);

    private void DrawRings(Func<string, Vector2> labelOf, float scale)
    {
        // Each ring is drawn over an ink ring a little wider, so it reads
        // on every owner's colour, the light ones included.
        float radius = (UiConstants.TerritoryRadius + 14f) * scale;
        var ink = UiConstants.MapInkColor;
        Vector2 CenterOf(string code) => labelOf(code) + new Vector2(0, 12f * scale);
        void Ring(string code, float r, Color color, float thickness)
        {
            DrawArc(CenterOf(code), r, 0, Mathf.Tau, 56, ink, thickness + 2.5f, antialiased: true);
            DrawArc(CenterOf(code), r, 0, Mathf.Tau, 56, color, thickness, antialiased: true);
        }
        foreach (string code in _targets)
            if (_map!.TryTerritory(code, out _))
                Ring(code, radius, UiConstants.TargetRingColor, 3f);
        foreach (string code in _selectable)
            if (_map!.TryTerritory(code, out _))
                Ring(code, radius, UiConstants.SelectableRingColor, 2.5f);
        if (_selected is not null && _map!.TryTerritory(_selected, out _))
            Ring(_selected, radius + 5f * scale, UiConstants.SelectedRingColor, 3.5f);
    }

    // ------------------------------------------------------------------
    // Picking
    // ------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        if (_map is null) return;
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;

        var (offset, scale) = FitTransform();
        var point = new MapPoint((click.Position.X - offset.X) / scale, (click.Position.Y - offset.Y) / scale);

        string? hit = null;
        foreach (var territory in _map.Territories)
        {
            bool inside = false;
            foreach (var ring in territory.Polygons)
                if (PointInPolygon(point, ring)) { inside = true; break; }
            if (inside) { hit = territory.Code; break; }
        }

        if (hit is null)
        {
            // Small shapes (islands especially) can be thinner than a
            // click: the nearest label within reach answers instead.
            float reach = UiConstants.TerritoryRadius + UiConstants.ClickSlack;
            float best = float.MaxValue;
            foreach (var territory in _map.Territories)
            {
                float dx = point.X - territory.Label.X, dy = point.Y - territory.Label.Y;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance <= reach && distance < best) { best = distance; hit = territory.Code; }
            }
        }

        if (hit is not null)
        {
            TerritoryClicked?.Invoke(hit);
            AcceptEvent();
        }
    }

    private static bool PointInPolygon(MapPoint point, IReadOnlyList<MapPoint> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Scale and offset that fit the map canvas into this control, centered.</summary>
    private (Vector2 Offset, float Scale) FitTransform()
    {
        Vector2 canvas = _map is null
            ? new Vector2(2048, 1384)
            : new Vector2(_map.Canvas.X, _map.Canvas.Y);
        float scale = Math.Min(Size.X / canvas.X, Size.Y / canvas.Y);
        Vector2 offset = (Size - canvas * scale) / 2f;
        return (offset, scale);
    }
}
