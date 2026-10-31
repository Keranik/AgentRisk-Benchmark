using System;
using System.Collections.Generic;
using AgentRisk.Client.Protocol;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// The dice tray: the most recent battle as the summaries reported it.
/// Round-by-round battles show their faces — the latest round
/// large, earlier rounds as a compact line — because the engine rolled
/// them and the table shows what it rolled (presentation follows
/// truth). A blitz shows its totals only; the protocol keeps blitz faces in
/// the full game log, not in summaries, so the tray invents none.
/// </summary>
public partial class DiceView : Control
{
    // The tray's text layout. Every value stands after its caption
    // ("Attacker: Yellow"), captions muted and values bold, and a line
    // breaks between facts rather than inside one, as the neighbouring
    // fact rows wrap. The round's losses stand beside its dice, not
    // under them, so the tray stays short enough to leave the decision
    // panel its room; the tray grows only when a capture or earlier
    // rounds add a line under the dice.
    private const float TextLeft = 4f;
    private const float MinTextWidth = 40f;
    private const float FactGap = 16f;
    private const float TitleBaseline = 16f;
    private const int TitleFontSize = 15;
    private const float BlitzGap = 8f;
    private const int BlitzFontSize = 14;
    private const float DiceGap = 8f;
    private const float LossesBesideDice = 18f;
    private const int LossesFontSize = 14;
    private const float BelowDice = 17f;
    private const int EarlierFontSize = 12;
    private const float TrayFloor = 80f;
    private const float TrayBottomPad = 6f;

    private Battle? _battle;
    private BoardCatalog? _catalog;
    private Font? _font;
    private Font? _bold;
    private StyleBoxFlat? _attackDieStyle;
    private StyleBoxFlat? _defendDieStyle;

    public override void _Ready()
    {
        _font = GetThemeDefaultFont();
        _bold = UiTheme.HeadingFont;
        _attackDieStyle = new StyleBoxFlat
        {
            BgColor = UiConstants.AttackDieColor,
            CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7,
        };
        _defendDieStyle = new StyleBoxFlat
        {
            BgColor = UiConstants.DefendDieColor,
            CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7,
        };
        CustomMinimumSize = new Vector2(0, TrayFloor);
    }

    /// <summary>Shows a battle (or clears the tray with null).</summary>
    public void ShowBattle(Battle? battle, BoardCatalog catalog)
    {
        _battle = battle;
        _catalog = catalog;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_font is null || _bold is null) return;

        if (_battle is null || _catalog is null)
        {
            DrawString(_font, new Vector2(TextLeft, TitleBaseline), "No battle yet. Each attack's dice show here.",
                HorizontalAlignment.Left, -1, LossesFontSize, UiConstants.Chrome.TextMuted);
            FitHeight(TrayFloor);
            return;
        }

        float textWidth = Math.Max(Size.X - TextLeft * 2, MinTextWidth);
        var soft = new Color(1, 1, 1, 0.85f);

        var title = new List<(string, string)>
        {
            ("Attack", $"{TerritoryTerritoryName(_battle.From)} → {TerritoryTerritoryName(_battle.To)}"),
            ("Attacker", UiConstants.SeatDisplayName(_battle.Attacker)),
        };
        if (_battle.Defender is not null)
            title.Add(("Defender", UiConstants.SeatDisplayName(_battle.Defender)));
        float titleLast = DrawFacts(new Vector2(TextLeft, TitleBaseline), textWidth, TitleFontSize, title, Colors.White);

        if (_battle.Blitz is { } blitz)
        {
            float blitzFirst = titleLast + _font.GetHeight(BlitzFontSize) + BlitzGap;
            float blitzLast = DrawFacts(new Vector2(TextLeft, blitzFirst), textWidth, BlitzFontSize,
                blitz.Facts(TerritoryTerritoryName), soft);
            FitHeight(blitzLast + _font.GetDescent(BlitzFontSize) + TrayBottomPad);
            return;
        }

        if (_battle.Rounds.Count == 0)
        {
            FitHeight(titleLast + _font.GetDescent(TitleFontSize) + TrayBottomPad);
            return;
        }
        var last = _battle.Rounds[^1];

        float y = titleLast + _font.GetDescent(TitleFontSize) + DiceGap;
        float defenderLeft = 4 + 3 * (UiConstants.DieSize + UiConstants.DieGap) + 18f;
        DrawDiceRow(last.AttackerFaces, _attackDieStyle!, UiConstants.PipOnDark, new Vector2(4, y));
        DrawDiceRow(last.DefenderFaces, _defendDieStyle!, UiConstants.PipOnLight, new Vector2(defenderLeft, y));

        float lossesLeft = defenderLeft + 2 * (UiConstants.DieSize + UiConstants.DieGap) + LossesBesideDice;
        float lossesWidth = Math.Max(Size.X - lossesLeft - TextLeft, MinTextWidth);
        DrawFacts(new Vector2(lossesLeft, y + LossesFontSize + 2f), lossesWidth, LossesFontSize,
            new[] { ("Attacker lost", last.AttackerLosses.ToString()), ("Defender lost", last.DefenderLosses.ToString()) },
            soft);

        // Under the dice: the capture, if this round made one, then the
        // battle's earlier rounds, small, so the record stays visible.
        float baseline = y + UiConstants.DieSize;
        float bottom = baseline;
        if (last.CapturedCode is not null)
        {
            baseline = DrawFacts(new Vector2(TextLeft, baseline + BelowDice), textWidth, LossesFontSize,
                new[] { ("Captured", TerritoryTerritoryName(last.CapturedCode)) }, soft);
            bottom = baseline + _font.GetDescent(LossesFontSize);
        }
        if (_battle.Rounds.Count > 1)
        {
            var earlier = new List<(string, string)>();
            for (int i = 0; i < _battle.Rounds.Count - 1; i++)
            {
                var round = _battle.Rounds[i];
                earlier.Add(($"Round {i + 1}",
                    $"{string.Join(" ", round.AttackerFaces)} against {string.Join(" ", round.DefenderFaces)}"));
            }
            baseline = DrawFacts(new Vector2(TextLeft, baseline + BelowDice), textWidth, EarlierFontSize, earlier,
                new Color(1, 1, 1, 0.6f));
            bottom = baseline + _font.GetDescent(EarlierFontSize);
        }
        FitHeight(bottom + TrayBottomPad);
    }

    /// <summary>
    /// Draws captioned facts left to right from a first baseline, the
    /// caption muted and the value bold, breaking to a new line between
    /// facts when the next would cross the width. Returns the last
    /// line's baseline.
    /// </summary>
    private float DrawFacts(Vector2 firstBaseline, float width, int fontSize,
        IReadOnlyList<(string Caption, string Value)> facts, Color valueColor)
    {
        float lineHeight = _font!.GetHeight(fontSize);
        float x = 0;
        float baseline = firstBaseline.Y;
        foreach (var (caption, value) in facts)
        {
            string captionText = $"{caption}: ";
            float captionWidth = _font.GetStringSize(captionText, HorizontalAlignment.Left, -1, fontSize).X;
            float valueWidth = _bold!.GetStringSize(value, HorizontalAlignment.Left, -1, fontSize).X;
            if (x > 0 && x + captionWidth + valueWidth > width)
            {
                x = 0;
                baseline += lineHeight;
            }
            DrawString(_font, new Vector2(firstBaseline.X + x, baseline), captionText,
                HorizontalAlignment.Left, -1, fontSize, UiConstants.Chrome.TextMuted);
            DrawString(_bold, new Vector2(firstBaseline.X + x + captionWidth, baseline), value,
                HorizontalAlignment.Left, -1, fontSize, valueColor);
            x += captionWidth + valueWidth + FactGap;
        }
        return baseline;
    }

    /// <summary>Grows or shrinks the tray to what it drew (never below the floor), once this frame's draw is done.</summary>
    private void FitHeight(float wanted)
    {
        float height = MathF.Ceiling(Math.Max(TrayFloor, wanted));
        if (Math.Abs(CustomMinimumSize.Y - height) < 0.5f) return;
        Callable.From(() => CustomMinimumSize = new Vector2(0, height)).CallDeferred();
    }

    private string TerritoryTerritoryName(string code) =>
        _catalog is not null && _catalog.TryTerritory(code, out var info) ? info.Name : code;

    private void DrawDiceRow(IReadOnlyList<int> faces, StyleBoxFlat style, Color pipColor, Vector2 origin)
    {
        for (int i = 0; i < faces.Count; i++)
        {
            var rect = new Rect2(origin + new Vector2(i * (UiConstants.DieSize + UiConstants.DieGap), 0),
                new Vector2(UiConstants.DieSize, UiConstants.DieSize));
            DrawStyleBox(style, rect);
            DrawPips(rect, faces[i], pipColor);
        }
    }

    /// <summary>Pip positions on the standard 3-by-3 die grid.</summary>
    private static readonly Dictionary<int, (float X, float Y)[]> PipLayouts = new()
    {
        [1] = new[] { (0.50f, 0.50f) },
        [2] = new[] { (0.30f, 0.30f), (0.70f, 0.70f) },
        [3] = new[] { (0.28f, 0.28f), (0.50f, 0.50f), (0.72f, 0.72f) },
        [4] = new[] { (0.30f, 0.30f), (0.70f, 0.30f), (0.30f, 0.70f), (0.70f, 0.70f) },
        [5] = new[] { (0.28f, 0.28f), (0.72f, 0.28f), (0.50f, 0.50f), (0.28f, 0.72f), (0.72f, 0.72f) },
        [6] = new[] { (0.30f, 0.27f), (0.70f, 0.27f), (0.30f, 0.50f), (0.70f, 0.50f), (0.30f, 0.73f), (0.70f, 0.73f) },
    };

    private void DrawPips(Rect2 die, int face, Color color)
    {
        if (!PipLayouts.TryGetValue(face, out var pips)) return;
        float radius = UiConstants.DieSize * 0.085f;
        foreach (var (fx, fy) in pips)
            DrawCircle(die.Position + new Vector2(fx * die.Size.X, fy * die.Size.Y), radius, color);
    }
}
