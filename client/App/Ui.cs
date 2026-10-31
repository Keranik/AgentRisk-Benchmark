using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// Small builders the screens share, so a card, a hint, or a form row is
/// put together the same way everywhere. Presentation only: no state, no
/// protocol, and every role's look comes from <see cref="UiTheme"/>.
/// </summary>
public static class Ui
{
    /// <summary>A label in a theme role; wrapped text by default.</summary>
    public static Label Text(string text, string variation = "", bool wrap = true)
    {
        var label = new Label { Text = text, ThemeTypeVariation = variation };
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            // A wrapped label inside a box sizes from its container; it
            // needs a floor or it can collapse to one word per line.
            label.CustomMinimumSize = new Vector2(80, 0);
        }
        return label;
    }

    /// <summary>Explanatory text in the muted hint role.</summary>
    public static Label Hint(string text) => Text(text, UiTheme.HintLabel);

    /// <summary>A heading in the heading role.</summary>
    public static Label Heading(string text) => Text(text, UiTheme.HeadingLabel, wrap: false);

    /// <summary>A sub-heading in the sub-heading role.</summary>
    public static Label Subheading(string text) => Text(text, UiTheme.SubheadingLabel, wrap: false);

    /// <summary>
    /// A card: a bordered surface with an optional heading and hint, and a
    /// body to fill. The card is what goes into the layout; the body is
    /// where its content goes.
    /// </summary>
    public static (PanelContainer Card, VBoxContainer Body) Card(string? heading = null, string? hint = null)
    {
        var card = new PanelContainer { ThemeTypeVariation = UiTheme.Card };
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        card.AddChild(body);
        if (heading is not null) body.AddChild(Heading(heading));
        if (hint is not null) body.AddChild(Hint(hint));
        return (card, body);
    }

    /// <summary>
    /// One form field: the label in a fixed column, the control beside it,
    /// and an optional hint underneath the control explaining what the
    /// field does. Returns the row's container and the hint label (null
    /// when no hint was given), so a screen can rewrite the hint live.
    /// </summary>
    public static (VBoxContainer Row, Label? Hint) Field(string label, Control control, string? hint = null)
    {
        var row = new VBoxContainer();
        row.AddThemeConstantOverride("separation", 3);
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 12);
        line.AddChild(new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(UiConstants.FormLabelWidth, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        line.AddChild(control);
        row.AddChild(line);
        Label? hintLabel = null;
        if (hint is not null)
        {
            var indent = new HBoxContainer();
            indent.AddThemeConstantOverride("separation", 12);
            indent.AddChild(new Control { CustomMinimumSize = new Vector2(UiConstants.FormLabelWidth, 0) });
            hintLabel = Hint(hint);
            indent.AddChild(hintLabel);
            row.AddChild(indent);
        }
        return (row, hintLabel);
    }

    /// <summary>A button in a theme role.</summary>
    public static Button Button(string text, string variation = "", string tooltip = "")
    {
        var button = new Button { Text = text, ThemeTypeVariation = variation, TooltipText = tooltip };
        return button;
    }

    /// <summary>A square swatch of a seat's colour.</summary>
    public static ColorRect Swatch(Color color, float size = 14f) => new()
    {
        Color = color,
        CustomMinimumSize = new Vector2(size, size),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
    };

    /// <summary>A small square of a seat's colour, for dropdown entries.</summary>
    public static ImageTexture SwatchIcon(string seatCode)
    {
        var image = Image.CreateEmpty(14, 14, false, Image.Format.Rgba8);
        image.Fill(UiConstants.SeatColor(seatCode));
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>A margin container with the given margins.</summary>
    public static MarginContainer Margins(int left, int top, int right, int bottom)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", left);
        margin.AddThemeConstantOverride("margin_top", top);
        margin.AddThemeConstantOverride("margin_right", right);
        margin.AddThemeConstantOverride("margin_bottom", bottom);
        return margin;
    }

    /// <summary>A flexible gap that pushes what follows it to the far end of a box.</summary>
    public static Control Spacer() => new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

    /// <summary>Sets a status line's text, showing the line only while it has something to say.</summary>
    public static void SetStatus(Label label, string text)
    {
        label.Text = text;
        label.Visible = text.Length > 0;
    }

    /// <summary>A bulleted list as one wrapped label per item.</summary>
    public static VBoxContainer Bullets(params string[] items)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        foreach (string item in items)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(new Label { Text = "•", SizeFlagsVertical = Control.SizeFlags.ShrinkBegin });
            row.AddChild(Text(item));
            list.AddChild(row);
        }
        return list;
    }

    /// <summary>A numbered list as one wrapped label per step.</summary>
    public static VBoxContainer Steps(params string[] steps)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < steps.Length; i++)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(new Label
            {
                Text = $"{i + 1}.",
                CustomMinimumSize = new Vector2(18, 0),
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            });
            row.AddChild(Text(steps[i]));
            list.AddChild(row);
        }
        return list;
    }
}
