using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// The client's own tunables: sizes, colors, and defaults that
/// tune how the table looks and feels. Rules numbers live in the engine;
/// protocol tokens live in ProtocolConstants; this file is presentation
/// only, grouped by the surface it tunes.
/// </summary>
public static class UiConstants
{
    // --- Connection defaults (the host's own defaults) ---

    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 4000;
    public const string DefaultSeat = "RED";

    // --- Board geometry (design units; the map view scales to fit) ---

    /// <summary>Radius of a territory disc on the board canvas.</summary>
    public const float TerritoryRadius = 30f;

    /// <summary>Extra reach past the disc when hit-testing a click, so small discs stay easy to press.</summary>
    public const float ClickSlack = 12f;

    /// <summary>Font size for territory names on the board.</summary>
    public const int TerritoryNameFontSize = 19;

    /// <summary>Font size for the army count on a disc.</summary>
    public const int ArmyCountFontSize = 26;

    /// <summary>Font size for continent labels on the board.</summary>
    public const int ContinentLabelFontSize = 22;

    // --- The printed board's vintage treatment: board only ---
    // The map renders map.json's polygons over a parchment ground with
    // muted continent tints on unclaimed land and thin ink borders,
    // after the reference art's treatment. Owned land takes its owner's
    // seat colour exactly, never blended with the continent tint: one
    // player's land must look the same on every continent.

    /// <summary>The board's parchment ground.</summary>
    public static readonly Color ParchmentColor = new(0.91f, 0.85f, 0.70f);

    /// <summary>Ink for territory borders and map text.</summary>
    public static readonly Color MapInkColor = new(0.23f, 0.19f, 0.14f);

    /// <summary>Strength of a continent tint on unclaimed land (over the parchment).</summary>
    public const float UnclaimedTintAlpha = 0.40f;

    /// <summary>Width of the ink line drawn along borders between continents, in pixels.</summary>
    public const float ContinentBorderWidth = 3.4f;

    /// <summary>Radius of the owner-coloured chip that carries a territory's army count, in canvas units.</summary>
    public const float ArmyChipRadius = 23f;

    /// <summary>Font size for territory names on the map, in canvas units.</summary>
    public const int MapNameFontSize = 21;

    /// <summary>Font size for army counts on the map, in canvas units.</summary>
    public const int MapArmyFontSize = 26;

    /// <summary>Radius of a continent's ocean badge disc, in canvas units.</summary>
    public const float ContinentBadgeRadius = 37f;

    /// <summary>Font size for the bonus number inside a continent badge, in canvas units.</summary>
    public const int ContinentBadgeNumberFontSize = 44;

    /// <summary>Font size for the continent name beneath a badge, in canvas units.</summary>
    public const int ContinentBadgeNameFontSize = 19;

    /// <summary>Dash and gap lengths for the sea links, in canvas units.</summary>
    public const float SeaLinkDash = 14f;
    public const float SeaLinkGap = 10f;

    // --- Dice ---

    /// <summary>Edge length of one drawn die, in pixels.</summary>
    public const float DieSize = 30f;

    /// <summary>Gap between dice in a row.</summary>
    public const float DieGap = 8f;

    // --- Seat colors: the six seats, the Rebels, and unclaimed land ---
    // The one source for a seat's colour: the map's fills and army
    // chips, the players table, the setup rows and the host controls all
    // read it, so a colour means the same seat everywhere. The seven are
    // spread apart in hue and in lightness (pink is a magenta, never a
    // light red; yellow is a clear yellow, not the old mustard that sat
    // beside Africa's tint; the Rebels are a neutral stone grey), and
    // every one stands apart from the pale parchment of unclaimed land.

    public static Color SeatColor(string seatCode) => seatCode switch
    {
        "RED" => new Color(0.80f, 0.16f, 0.13f),
        "BLU" => new Color(0.15f, 0.40f, 0.85f),
        "GRN" => new Color(0.13f, 0.58f, 0.25f),
        "YEL" => new Color(0.98f, 0.82f, 0.10f),
        "PNK" => new Color(0.86f, 0.26f, 0.71f),
        "BLK" => new Color(0.16f, 0.16f, 0.19f),
        "REB" => new Color(0.60f, 0.58f, 0.54f),
        _ => new Color(0.45f, 0.47f, 0.50f),
    };

    /// <summary>Ink or white, whichever reads on a fill of this colour.</summary>
    public static Color TextOn(Color fill) =>
        fill.Luminance > 0.5f ? new Color(0.10f, 0.09f, 0.08f) : new Color(1f, 1f, 1f);

    /// <summary>The spoken name of a seat, for sentences the player reads ("Red attacks…").</summary>
    public static string SeatDisplayName(string seatCode) => seatCode switch
    {
        "RED" => "Red",
        "BLU" => "Blue",
        "GRN" => "Green",
        "YEL" => "Yellow",
        "PNK" => "Pink",
        "BLK" => "Black",
        "REB" => "the Rebels",
        _ => seatCode,
    };

    /// <summary>A phase code in the words the table uses, lower case for mid-sentence.</summary>
    public static string PhaseName(string phase) => phase switch
    {
        "SETUP" => "setup",
        "REIN" => "reinforcements",
        "ATK" => "attack",
        "FRT" => "fortify",
        "REB" => "the Rebels' turn",
        _ => phase,
    };

    /// <summary>A phase's name as a value standing on its own ("Current phase: Fortify").</summary>
    public static string PhaseTitle(string phase)
    {
        string name = PhaseName(phase);
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    // --- Continent tints: the printed board's own grouping, used for names and labels ---

    public static Color ContinentColor(string continentCode) => continentCode switch
    {
        "NA" => new Color(0.85f, 0.60f, 0.35f),
        "SA" => new Color(0.85f, 0.45f, 0.50f),
        "EU" => new Color(0.45f, 0.62f, 0.90f),
        "AF" => new Color(0.90f, 0.78f, 0.35f),
        "AS" => new Color(0.45f, 0.78f, 0.55f),
        "AU" => new Color(0.72f, 0.52f, 0.85f),
        _ => new Color(0.75f, 0.75f, 0.75f),
    };

    // --- The host edit panel ---

    /// <summary>Minimum size of the edit panel's card: the whole board's rows scroll inside it.</summary>
    public static readonly Vector2 EditPanelMinSize = new(780, 660);

    /// <summary>Width of the territory-name column in the edit panel's rows.</summary>
    public const float EditNameColumnWidth = 210f;

    /// <summary>
    /// The largest army count the edit panel's stepper accepts. The
    /// engine accepts any positive count; this is a typing guard for
    /// the form, not a rules limit.
    /// </summary>
    public const int EditArmiesMaximum = 100_000;

    /// <summary>Minimum size of the table's help card.</summary>
    public static readonly Vector2 HelpPanelMinSize = new(820, 620);

    /// <summary>The dim behind the edit panel's card, so the modal reads as a layer over the table.</summary>
    public static readonly Color EditDimColor = new(0f, 0f, 0f, 0.55f);

    // --- The chrome palette: everything around the board ---
    // A quiet slate ground, one accent for the action a screen asks for,
    // amber for what needs attention, green for what went through. The
    // board's parchment treatment above is separate and unaffected.

    public static readonly ChromePalette Chrome = new();

    public sealed class ChromePalette
    {
        public Color Background { get; } = new(0.086f, 0.098f, 0.118f);
        public Color HeaderBackground { get; } = new(0.067f, 0.075f, 0.090f);
        public Color Surface { get; } = new(0.118f, 0.133f, 0.157f);
        public Color SurfaceRaised { get; } = new(0.149f, 0.165f, 0.192f);
        public Color Field { get; } = new(0.071f, 0.082f, 0.098f);
        public Color Control { get; } = new(0.165f, 0.184f, 0.212f);
        public Color ControlHover { get; } = new(0.204f, 0.227f, 0.259f);
        public Color ControlPressed { get; } = new(0.137f, 0.153f, 0.176f);
        public Color ControlDisabled { get; } = new(0.110f, 0.122f, 0.141f);
        public Color Border { get; } = new(0.208f, 0.231f, 0.267f);
        public Color BorderStrong { get; } = new(0.310f, 0.341f, 0.388f);
        public Color Text { get; } = new(0.905f, 0.918f, 0.933f);
        public Color TextMuted { get; } = new(0.600f, 0.639f, 0.690f);
        public Color TextDisabled { get; } = new(0.420f, 0.447f, 0.486f);
        public Color Accent { get; } = new(0.278f, 0.533f, 0.839f);
        public Color AccentHover { get; } = new(0.341f, 0.596f, 0.890f);
        public Color AccentPressed { get; } = new(0.231f, 0.459f, 0.737f);
        public Color AccentSurface { get; } = new(0.118f, 0.192f, 0.290f);
        public Color OnAccent { get; } = new(1f, 1f, 1f);
        public Color Warning { get; } = new(0.910f, 0.663f, 0.282f);
        public Color WarningSurface { get; } = new(0.235f, 0.180f, 0.098f);
        public Color Ok { get; } = new(0.416f, 0.765f, 0.537f);
    }

    /// <summary>Width of the label column in the setup forms.</summary>
    public const float FormLabelWidth = 190f;

    /// <summary>Width of the table screen's information column at its default split.</summary>
    public const float TableSideWidth = 500f;

    // --- Table palette ---
    public static readonly Color TableBackground = new(0.09f, 0.11f, 0.14f);
    public static readonly Color MapBackground = new(0.12f, 0.16f, 0.21f);
    public static readonly Color BorderLineColor = new(1f, 1f, 1f, 0.10f);
    public static readonly Color SelectableRingColor = new(1f, 1f, 1f, 0.85f);
    public static readonly Color SelectedRingColor = new(1f, 0.85f, 0.30f);
    public static readonly Color TargetRingColor = new(1f, 0.45f, 0.35f);
    public static readonly Color DiscOutlineColor = new(0f, 0f, 0f, 0.55f);
    public static readonly Color AttackDieColor = new(0.80f, 0.22f, 0.18f);
    public static readonly Color DefendDieColor = new(0.92f, 0.93f, 0.95f);
    public static readonly Color PipOnLight = new(0.10f, 0.10f, 0.12f);
    public static readonly Color PipOnDark = new(0.95f, 0.95f, 0.95f);
}
