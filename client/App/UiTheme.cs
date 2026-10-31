using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// The app's look, built in code and set once on the shell so every
/// screen inherits it: a plain, quiet instrument palette (clarity
/// wins: academic and system-like, no fanfare), with
/// spacing, a type scale, and one accent colour reserved for the
/// action a screen is asking for. The board keeps its own vintage
/// treatment; nothing here touches it.
///
/// Screens pick roles through theme type variations rather than
/// per-control overrides, so a role looks the same everywhere:
/// <see cref="Card"/> panels group a form or a readout, <see cref="Hint"/>
/// labels explain, <see cref="PrimaryButton"/> marks the one action that
/// moves a screen forward, and the banner variations say whose move it is.
/// </summary>
public static class UiTheme
{
    // --- Type variation names (Control.ThemeTypeVariation) ---

    /// <summary>A bordered surface that groups one form section or readout.</summary>
    public const string Card = "Card";
    /// <summary>The bar across the top of a screen.</summary>
    public const string HeaderBar = "HeaderBar";
    /// <summary>The guidance banner while the table waits on someone else.</summary>
    public const string BannerWaiting = "BannerWaiting";
    /// <summary>The guidance banner while a decision waits on this screen.</summary>
    public const string BannerAction = "BannerAction";
    /// <summary>The guidance banner when the host must act (a paused game, a stalled seat).</summary>
    public const string BannerAlert = "BannerAlert";
    /// <summary>The one button that moves a screen forward.</summary>
    public const string PrimaryButton = "PrimaryButton";
    /// <summary>A small, quiet button (copy, open folder, per-seat controls).</summary>
    public const string SmallButton = "SmallButton";
    /// <summary>The app's name on a screen.</summary>
    public const string TitleLabel = "TitleLabel";
    /// <summary>A card's or a screen section's heading.</summary>
    public const string HeadingLabel = "HeadingLabel";
    /// <summary>A sub-heading inside a card.</summary>
    public const string SubheadingLabel = "SubheadingLabel";
    /// <summary>Explanatory text: what a field means, what will happen.</summary>
    public const string HintLabel = "HintLabel";
    /// <summary>A small upper-case column or field caption.</summary>
    public const string CaptionLabel = "CaptionLabel";
    /// <summary>A problem the reader must fix before going on.</summary>
    public const string WarningLabel = "WarningLabel";
    /// <summary>A completed action or a healthy state.</summary>
    public const string OkLabel = "OkLabel";
    /// <summary>Monospaced protocol text (reply lines, raw event lines).</summary>
    public const string CodeLabel = "CodeLabel";
    /// <summary>The value half of a labelled fact ("Round: <b>9</b>"): bold, full strength.</summary>
    public const string ValueLabel = "ValueLabel";
    /// <summary>A labelled fact's value in a dense list (the host's seat rows): bold, hint-sized.</summary>
    public const string SmallValueLabel = "SmallValueLabel";
    /// <summary>The outline around a set of segments, one of them lit (the table's role indicator).</summary>
    public const string SegmentGroup = "SegmentGroup";
    /// <summary>The lit segment: the option that holds.</summary>
    public const string SegmentOn = "SegmentOn";
    /// <summary>An unlit segment: an option that could hold, but does not here.</summary>
    public const string SegmentOff = "SegmentOff";
    /// <summary>The lit segment's text.</summary>
    public const string SegmentOnLabel = "SegmentOnLabel";
    /// <summary>An unlit segment's text, dimmed.</summary>
    public const string SegmentOffLabel = "SegmentOffLabel";

    // --- Type scale ---

    public const int BodySize = 15;
    public const int HintSize = 13;
    public const int CaptionSize = 12;
    public const int SubheadingSize = 16;
    public const int HeadingSize = 19;
    public const int TitleSize = 26;

    /// <summary>The fallback font made heavier, for headings.</summary>
    public static Font HeadingFont { get; } = new FontVariation
    {
        BaseFont = ThemeDB.FallbackFont,
        VariationEmbolden = 0.55f,
    };

    /// <summary>A monospaced system font for protocol text, falling back to the default.</summary>
    public static Font CodeFont { get; } = new SystemFont
    {
        FontNames = new[] { "DejaVu Sans Mono", "Consolas", "Menlo", "Liberation Mono", "monospace" },
    };

    /// <summary>Builds the theme the shell sets on its root.</summary>
    public static Theme Build()
    {
        var c = UiConstants.Chrome;
        var theme = new Theme { DefaultFontSize = BodySize };

        // Containers.
        theme.SetConstant("separation", "VBoxContainer", 8);
        theme.SetConstant("separation", "HBoxContainer", 8);
        theme.SetConstant("h_separation", "GridContainer", 12);
        theme.SetConstant("v_separation", "GridContainer", 6);
        theme.SetStylebox("panel", "PanelContainer", Flat(c.Background));
        theme.SetStylebox("panel", "Panel", Flat(c.Background));

        theme.SetTypeVariation(Card, "PanelContainer");
        theme.SetStylebox("panel", Card, Flat(c.Surface, border: c.Border, radius: 6, marginH: 16, marginV: 14));
        theme.SetTypeVariation(HeaderBar, "PanelContainer");
        theme.SetStylebox("panel", HeaderBar, Flat(c.HeaderBackground, marginH: 16, marginV: 8, borderBottom: c.Border));
        theme.SetTypeVariation(BannerWaiting, "PanelContainer");
        theme.SetStylebox("panel", BannerWaiting, Flat(c.Surface, radius: 6, marginH: 16, marginV: 10, borderLeft: c.TextMuted));
        theme.SetTypeVariation(BannerAction, "PanelContainer");
        theme.SetStylebox("panel", BannerAction, Flat(c.AccentSurface, radius: 6, marginH: 16, marginV: 10, borderLeft: c.Accent));
        theme.SetTypeVariation(BannerAlert, "PanelContainer");
        theme.SetStylebox("panel", BannerAlert, Flat(c.WarningSurface, radius: 6, marginH: 16, marginV: 10, borderLeft: c.Warning));

        // Labels.
        theme.SetColor("font_color", "Label", c.Text);
        theme.SetTypeVariation(TitleLabel, "Label");
        theme.SetFontSize("font_size", TitleLabel, TitleSize);
        theme.SetFont("font", TitleLabel, HeadingFont);
        theme.SetTypeVariation(HeadingLabel, "Label");
        theme.SetFontSize("font_size", HeadingLabel, HeadingSize);
        theme.SetFont("font", HeadingLabel, HeadingFont);
        theme.SetTypeVariation(SubheadingLabel, "Label");
        theme.SetFontSize("font_size", SubheadingLabel, SubheadingSize);
        theme.SetFont("font", SubheadingLabel, HeadingFont);
        theme.SetTypeVariation(HintLabel, "Label");
        theme.SetFontSize("font_size", HintLabel, HintSize);
        theme.SetColor("font_color", HintLabel, c.TextMuted);
        theme.SetTypeVariation(CaptionLabel, "Label");
        theme.SetFontSize("font_size", CaptionLabel, CaptionSize);
        theme.SetColor("font_color", CaptionLabel, c.TextMuted);
        theme.SetTypeVariation(WarningLabel, "Label");
        theme.SetColor("font_color", WarningLabel, c.Warning);
        theme.SetTypeVariation(OkLabel, "Label");
        theme.SetColor("font_color", OkLabel, c.Ok);
        theme.SetTypeVariation(CodeLabel, "Label");
        theme.SetFont("font", CodeLabel, CodeFont);
        theme.SetFontSize("font_size", CodeLabel, HintSize);
        theme.SetTypeVariation(ValueLabel, "Label");
        theme.SetFont("font", ValueLabel, HeadingFont);
        theme.SetTypeVariation(SmallValueLabel, "Label");
        theme.SetFont("font", SmallValueLabel, HeadingFont);
        theme.SetFontSize("font_size", SmallValueLabel, HintSize);

        // Segments: a set of options with the one that holds lit.
        theme.SetTypeVariation(SegmentGroup, "PanelContainer");
        theme.SetStylebox("panel", SegmentGroup, Flat(c.Field, border: c.Border, radius: 5, marginH: 2, marginV: 2));
        theme.SetTypeVariation(SegmentOn, "PanelContainer");
        theme.SetStylebox("panel", SegmentOn, Flat(c.AccentSurface, border: c.Accent, radius: 4, marginH: 10, marginV: 2));
        theme.SetTypeVariation(SegmentOff, "PanelContainer");
        theme.SetStylebox("panel", SegmentOff, Flat(new Color(0, 0, 0, 0), marginH: 10, marginV: 2));
        theme.SetTypeVariation(SegmentOnLabel, "Label");
        theme.SetFont("font", SegmentOnLabel, HeadingFont);
        theme.SetFontSize("font_size", SegmentOnLabel, HintSize);
        theme.SetColor("font_color", SegmentOnLabel, c.Text);
        theme.SetTypeVariation(SegmentOffLabel, "Label");
        theme.SetFontSize("font_size", SegmentOffLabel, HintSize);
        theme.SetColor("font_color", SegmentOffLabel, c.TextDisabled);

        // Buttons. OptionButton and MenuButton inherit Button's boxes;
        // CheckBox gets its own empty ones so it never looks like a button.
        theme.SetStylebox("normal", "Button", Flat(c.Control, border: c.Border, radius: 4, marginH: 12, marginV: 6));
        theme.SetStylebox("hover", "Button", Flat(c.ControlHover, border: c.BorderStrong, radius: 4, marginH: 12, marginV: 6));
        theme.SetStylebox("pressed", "Button", Flat(c.ControlPressed, border: c.BorderStrong, radius: 4, marginH: 12, marginV: 6));
        theme.SetStylebox("hover_pressed", "Button", Flat(c.ControlPressed, border: c.Accent, radius: 4, marginH: 12, marginV: 6));
        theme.SetStylebox("disabled", "Button", Flat(c.ControlDisabled, border: c.Border, radius: 4, marginH: 12, marginV: 6));
        theme.SetStylebox("focus", "Button", Outline(c.Accent, 4));
        theme.SetColor("font_color", "Button", c.Text);
        theme.SetColor("font_hover_color", "Button", c.Text);
        theme.SetColor("font_pressed_color", "Button", c.Text);
        theme.SetColor("font_hover_pressed_color", "Button", c.Text);
        theme.SetColor("font_focus_color", "Button", c.Text);
        theme.SetColor("font_disabled_color", "Button", c.TextDisabled);

        theme.SetTypeVariation(PrimaryButton, "Button");
        theme.SetStylebox("normal", PrimaryButton, Flat(c.Accent, radius: 4, marginH: 16, marginV: 8));
        theme.SetStylebox("hover", PrimaryButton, Flat(c.AccentHover, radius: 4, marginH: 16, marginV: 8));
        theme.SetStylebox("pressed", PrimaryButton, Flat(c.AccentPressed, radius: 4, marginH: 16, marginV: 8));
        theme.SetStylebox("hover_pressed", PrimaryButton, Flat(c.AccentPressed, radius: 4, marginH: 16, marginV: 8));
        theme.SetStylebox("disabled", PrimaryButton, Flat(c.ControlDisabled, border: c.Border, radius: 4, marginH: 16, marginV: 8));
        theme.SetColor("font_color", PrimaryButton, c.OnAccent);
        theme.SetColor("font_hover_color", PrimaryButton, c.OnAccent);
        theme.SetColor("font_pressed_color", PrimaryButton, c.OnAccent);
        theme.SetColor("font_focus_color", PrimaryButton, c.OnAccent);
        theme.SetFont("font", PrimaryButton, HeadingFont);

        theme.SetTypeVariation(SmallButton, "Button");
        theme.SetFontSize("font_size", SmallButton, HintSize);
        theme.SetStylebox("normal", SmallButton, Flat(c.Control, border: c.Border, radius: 4, marginH: 8, marginV: 3));
        theme.SetStylebox("hover", SmallButton, Flat(c.ControlHover, border: c.BorderStrong, radius: 4, marginH: 8, marginV: 3));
        theme.SetStylebox("pressed", SmallButton, Flat(c.ControlPressed, border: c.BorderStrong, radius: 4, marginH: 8, marginV: 3));
        theme.SetStylebox("disabled", SmallButton, Flat(c.ControlDisabled, border: c.Border, radius: 4, marginH: 8, marginV: 3));

        var empty = new StyleBoxEmpty();
        foreach (string box in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            theme.SetStylebox(box, "CheckBox", empty);
        theme.SetColor("font_color", "CheckBox", c.Text);
        theme.SetColor("font_hover_color", "CheckBox", c.Text);
        theme.SetColor("font_pressed_color", "CheckBox", c.Text);
        theme.SetColor("font_hover_pressed_color", "CheckBox", c.Text);

        // Dropdown lists.
        theme.SetStylebox("panel", "PopupMenu", Flat(c.SurfaceRaised, border: c.BorderStrong, radius: 4, marginH: 4, marginV: 4));
        theme.SetStylebox("hover", "PopupMenu", Flat(c.AccentSurface, radius: 3));
        theme.SetColor("font_color", "PopupMenu", c.Text);
        theme.SetColor("font_hover_color", "PopupMenu", c.Text);

        // Text entry.
        var field = Flat(c.Field, border: c.Border, radius: 4, marginH: 8, marginV: 5);
        theme.SetStylebox("normal", "LineEdit", field);
        theme.SetStylebox("focus", "LineEdit", Outline(c.Accent, 4));
        theme.SetStylebox("read_only", "LineEdit", Flat(c.Surface, border: c.Border, radius: 4, marginH: 8, marginV: 5));
        theme.SetColor("font_color", "LineEdit", c.Text);
        theme.SetColor("font_placeholder_color", "LineEdit", c.TextDisabled);
        theme.SetColor("font_uneditable_color", "LineEdit", c.TextMuted);
        theme.SetColor("caret_color", "LineEdit", c.Text);
        theme.SetStylebox("normal", "TextEdit", field);
        theme.SetStylebox("focus", "TextEdit", Outline(c.Accent, 4));
        theme.SetStylebox("read_only", "TextEdit", Flat(c.Field, border: c.Border, radius: 4, marginH: 8, marginV: 6));
        theme.SetColor("font_color", "TextEdit", c.Text);
        theme.SetColor("font_readonly_color", "TextEdit", c.Text);
        theme.SetFont("font", "TextEdit", CodeFont);
        theme.SetFontSize("font_size", "TextEdit", HintSize);

        // Readouts.
        theme.SetStylebox("normal", "RichTextLabel", Flat(c.Field, border: c.Border, radius: 4, marginH: 8, marginV: 6));
        theme.SetColor("default_color", "RichTextLabel", c.Text);
        theme.SetFont("bold_font", "RichTextLabel", HeadingFont);
        theme.SetFont("mono_font", "RichTextLabel", CodeFont);
        theme.SetStylebox("panel", "ItemList", Flat(c.Field, border: c.Border, radius: 4, marginH: 4, marginV: 4));
        theme.SetStylebox("focus", "ItemList", new StyleBoxEmpty());
        theme.SetStylebox("selected", "ItemList", Flat(c.AccentSurface, radius: 3));
        theme.SetStylebox("selected_focus", "ItemList", Flat(c.AccentSurface, border: c.Accent, radius: 3));
        theme.SetStylebox("hovered", "ItemList", Flat(c.ControlHover, radius: 3));
        theme.SetColor("font_color", "ItemList", c.Text);
        theme.SetColor("font_selected_color", "ItemList", c.Text);
        theme.SetColor("font_hovered_color", "ItemList", c.Text);
        theme.SetConstant("v_separation", "ItemList", 8);

        // Tabs.
        theme.SetStylebox("panel", "TabContainer", Flat(c.Background, marginH: 0, marginV: 12, borderTop: c.Border));
        theme.SetStylebox("tab_selected", "TabContainer", Tab(c.Background, c.Accent));
        theme.SetStylebox("tab_unselected", "TabContainer", Tab(c.Background, c.Background));
        theme.SetStylebox("tab_hovered", "TabContainer", Tab(c.Surface, c.BorderStrong));
        theme.SetStylebox("tab_disabled", "TabContainer", Tab(c.Background, c.Background));
        theme.SetStylebox("tab_focus", "TabContainer", new StyleBoxEmpty());
        theme.SetStylebox("tabbar_background", "TabContainer", new StyleBoxEmpty());
        theme.SetColor("font_selected_color", "TabContainer", c.Text);
        theme.SetColor("font_unselected_color", "TabContainer", c.TextMuted);
        theme.SetColor("font_hovered_color", "TabContainer", c.Text);
        theme.SetColor("font_disabled_color", "TabContainer", c.TextDisabled);
        theme.SetFontSize("font_size", "TabContainer", SubheadingSize);

        // Dialogs (the host's confirmations).
        theme.SetStylebox("panel", "AcceptDialog", Flat(c.Surface, marginH: 16, marginV: 14));
        theme.SetStylebox("embedded_border", "Window", Flat(c.Surface, border: c.BorderStrong, radius: 6, marginH: 0, marginV: 0));
        theme.SetStylebox("embedded_unfocused_border", "Window", Flat(c.Surface, border: c.Border, radius: 6));
        theme.SetColor("title_color", "Window", c.Text);
        theme.SetConstant("title_height", "Window", 34);

        // Tooltips.
        theme.SetStylebox("panel", "TooltipPanel", Flat(c.SurfaceRaised, border: c.BorderStrong, radius: 4, marginH: 10, marginV: 7));
        theme.SetColor("font_color", "TooltipLabel", c.Text);
        theme.SetFontSize("font_size", "TooltipLabel", HintSize);

        return theme;
    }

    private static StyleBoxFlat Flat(Color background, Color? border = null, int radius = 0,
        int marginH = 0, int marginV = 0, Color? borderLeft = null, Color? borderTop = null,
        Color? borderBottom = null)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            ContentMarginLeft = marginH,
            ContentMarginRight = marginH,
            ContentMarginTop = marginV,
            ContentMarginBottom = marginV,
        };
        box.SetCornerRadiusAll(radius);
        if (border is { } all)
        {
            box.BorderColor = all;
            box.SetBorderWidthAll(1);
        }
        else if (borderLeft is { } left)
        {
            box.BorderColor = left;
            box.BorderWidthLeft = 4;
        }
        else if (borderTop is { } top)
        {
            box.BorderColor = top;
            box.BorderWidthTop = 1;
        }
        else if (borderBottom is { } bottom)
        {
            box.BorderColor = bottom;
            box.BorderWidthBottom = 1;
        }
        return box;
    }

    private static StyleBoxFlat Outline(Color color, int radius)
    {
        var box = new StyleBoxFlat { DrawCenter = false, BorderColor = color };
        box.SetBorderWidthAll(2);
        box.SetCornerRadiusAll(radius);
        box.SetExpandMarginAll(1);
        return box;
    }

    private static StyleBoxFlat Tab(Color background, Color underline)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = underline,
            BorderWidthBottom = 3,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        return box;
    }
}
