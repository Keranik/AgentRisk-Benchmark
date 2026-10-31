using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// One labelled fact: "Round: <b>9</b>". The caption says what the value
/// is, in the muted hint role; the value stands bold beside it, with an
/// optional seat-colour swatch before it. A reader who does not know the
/// game's vocabulary still learns what each number or word is from its
/// caption (never a bare run of values joined by
/// dots). The caption's tooltip, when given, explains the fact.
/// A fact with nothing to say hides itself.
/// </summary>
public sealed partial class FactField : HBoxContainer
{
    private readonly Label _caption;
    private readonly ColorRect _swatch;
    private readonly Label _value;

    public FactField(string caption, string tooltip = "", bool small = false)
    {
        AddThemeConstantOverride("separation", 5);
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText = tooltip;
        _caption = new Label
        {
            Text = $"{caption}:",
            ThemeTypeVariation = UiTheme.HintLabel,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Pass,
        };
        AddChild(_caption);
        _swatch = Ui.Swatch(Colors.Transparent, small ? 11 : 13);
        _swatch.Visible = false;
        _swatch.MouseFilter = MouseFilterEnum.Pass;
        AddChild(_swatch);
        _value = new Label
        {
            ThemeTypeVariation = small ? UiTheme.SmallValueLabel : UiTheme.ValueLabel,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Pass,
        };
        AddChild(_value);
        Visible = false;
    }

    /// <summary>Shows the value (hiding the fact when it is empty), with a swatch of the given colour before it.</summary>
    public void Set(string value, Color? swatch = null)
    {
        _value.Text = value;
        _swatch.Visible = swatch is not null;
        if (swatch is { } color) _swatch.Color = color;
        Visible = value.Length > 0;
    }

    /// <summary>Changes what the fact is called ("Current player" becomes "Winner" at the end).</summary>
    public void SetCaption(string caption) => _caption.Text = $"{caption}:";

    /// <summary>Colours the value (a warning, a healthy state); null restores the role's own colour.</summary>
    public void SetValueColor(Color? color)
    {
        if (color is { } c) _value.AddThemeColorOverride("font_color", c);
        else _value.RemoveThemeColorOverride("font_color");
    }

    /// <summary>
    /// A row of facts that wraps onto a second line, fact by fact, when
    /// its space runs out; a fact is never split across lines.
    /// </summary>
    public static HFlowContainer Row(int gap = 22)
    {
        var row = new HFlowContainer();
        row.AddThemeConstantOverride("h_separation", gap);
        row.AddThemeConstantOverride("v_separation", 2);
        return row;
    }
}
