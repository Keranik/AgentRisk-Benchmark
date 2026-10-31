using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentRisk.Client.Protocol;
using AgentRisk.Engine;
using AgentRisk.Host;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>Which door the table was entered through; it decides what the human may do here.</summary>
public enum TableDoor
{
    /// <summary>This app hosts the game (the host area shows; a seat may also be played here).</summary>
    Host,
    /// <summary>The human joined a game hosted elsewhere, and plays one seat.</summary>
    Join,
    /// <summary>The human only watches: the full table, no decision controls.</summary>
    Watch,
}

/// <summary>
/// The table itself: a header with the game's facts, the guidance banner,
/// the map, and a column of labelled panels beside it — the players, the
/// last battle, the decision surface, and tabs for the record of play,
/// each seat's latest exchange, and (when this app hosts) the host
/// controls. One screen for all three doors; the door decides which
/// inputs exist. Everything fits the window it is given: the column
/// scrolls rather than pushing a panel off the screen, and the map and
/// the column can be resized against each other.
///
/// State discipline: the table renders what the host's prompts and feed
/// report, and nothing else. The spectator feed is the one reading path
/// in every mode — hosted games are read through a watch connection to
/// the app's own listener, exactly as a remote watcher reads them — so
/// the seat panel and history behave identically at every door. The one
/// sanctioned sum is each seat's total armies, counted off the board
/// block's printed counts the way a human counts pieces.
///
/// Threading: connections and the hosted session live on their own
/// threads; everything they produce lands in one inbox, drained in
/// _Process on the Godot main thread, which is the only place UI state
/// changes.
/// </summary>
public partial class TableScreen : Control
{
    private abstract record InboxItem
    {
        public sealed record SeatBlock(BlockMessage Message) : InboxItem;
        public sealed record SeatClosed : InboxItem;
        public sealed record TapBlock(string Text) : InboxItem;
        public sealed record TapReady(SeatClient Client) : InboxItem;
        public sealed record TapFailed : InboxItem;
        public sealed record UiRequest(UiSeatChannel.PromptRequest Request) : InboxItem;
        public sealed record UiNotice(string Text) : InboxItem;
        public sealed record RunFinished(SessionResult? Result, string? Error) : InboxItem;
    }

    private readonly ConcurrentQueue<InboxItem> _inbox = new();
    private readonly FeedHistory _history = new();
    private readonly SeatExchangeTracker _exchanges = new();

    private BoardCatalog _catalog = null!;
    private MapData _map = null!;
    private TerritoryLayout _layout = null!;

    private TableDoor _door;
    private string? _mySeat;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private IReadOnlyList<SeatPlan> _seatPlans = Array.Empty<SeatPlan>();
    private string _modeText = "";
    private string _seedText = "";
    private string _connectAddress = "";

    private BoardState? _board;
    private BoardState? _seedBoard;
    private string? _handSeat;
    private IReadOnlyList<string> _handCards = Array.Empty<string>();
    private bool _gameOver;
    private string? _winner;
    private SessionResult? _result;
    private int? _resultDecisions;

    // What the table is waiting on, as last reported.
    private string? _pendingSeat;
    private DecisionAsk? _pendingAsk;
    private bool _pendingStrikeThree;
    private bool _myDecisionPending;
    private DecisionAsk? _myAsk;
    private bool _myStrikeThree;
    private Action<string>? _answerSink;

    // Connections and the hosted table.
    private SeatClient? _seatClient;
    private SeatClient? _watchTap;
    private bool _tapWanted;
    private HostedTable? _hosted;
    private ConcurrentQueue<string>? _hostNotices;
    private bool _runFinishNoted;
    private bool _resultLinesAppended;

    // Host-area polling.
    private double _snapshotClock;
    private SessionSnapshot? _lastSnapshot;

    // Panels redraw at most this often while the feed streams.
    private double _panelClock;
    private bool _seatPanelDirty;
    private bool _playersDirty;

    // Demo/screenshot auto-answer (never set in normal use).
    private double _demoClock = -1;

    // Header and banner. The header's facts are labelled, one field each.
    private FactField _modeFact = null!;
    private FactField _seedFact = null!;
    private FactField _roundFact = null!;
    private FactField _phaseFact = null!;
    private FactField _turnFact = null!;
    private FactField _colourFact = null!;
    private RoleIndicator _role = null!;
    private Label _editedBadge = null!;
    private PanelContainer _bannerPanel = null!;
    private Label _bannerTag = null!;
    private Label _banner = null!;
    private Label _status = null!;

    // The map and the column beside it.
    private PolygonBoardView _mapView = null!;
    private GridContainer _playersGrid = null!;
    private string _playersKey = "";
    private readonly Dictionary<string, PlayerRow> _playerRows = new(StringComparer.Ordinal);
    private Label _hand = null!;
    private DiceView _diceView = null!;
    private PanelContainer _decisionCard = null!;
    private DecisionPanel _panel = null!;
    private TabContainer _infoTabs = null!;
    private RichTextLabel _historyText = null!;
    private CheckBox _protocolToggle = null!;
    private readonly List<string> _recordLines = new();
    private RichTextLabel _seatPanelText = null!;

    // The host tab.
    private Control? _hostTab;
    private Label _hostGameStatus = null!;
    private Button _pauseButton = null!;
    private Button _resumeButton = null!;
    private Button _suspendButton = null!;
    private Button _endButton = null!;
    private Button _editButton = null!;
    private VBoxContainer _substituteBox = null!;
    private string _substituteKey = "";
    private string _alertedPauseKey = "";
    private VBoxContainer _hostSeatsBody = null!;
    private string _hostSeatsKey = "";
    private readonly Dictionary<string, HostSeatRow> _hostSeatRows = new(StringComparer.Ordinal);
    private VBoxContainer _joiningBody = null!;
    private Control _joiningSection = null!;
    private string _joiningKey = "";

    private HostEditPanel _editPanel = null!;
    private HelpPanel _helpPanel = null!;
    private bool _hostEdited;

    private sealed record PlayerRow(ColorRect Swatch, Label Name, Label Territories, Label Armies, Label Cards, Label Note);

    private sealed record HostSeatRow(
        Label Name, FactField Kind, FactField State, FactField Deciding, FactField Strikes, FactField LastReply,
        Button Poke, Button StandIn, Button Release);

    /// <summary>Raised when the human leaves the table; the shell tears the connections down.</summary>
    public event Action? LeaveRequested;

    /// <summary>Raised when the game ends, with the winner's seat code ("" when the feed did not name one).</summary>
    public event Action<string>? GameOverWinner;

    /// <summary>Feed entries consumed so far (the headless drivers report from these).</summary>
    public int FeedEntriesSeen { get; private set; }

    /// <summary>Board entries consumed from the feed so far.</summary>
    public int BoardEntriesSeen { get; private set; }

    /// <summary>Prompt entries consumed from the feed so far.</summary>
    public int PromptEntriesSeen { get; private set; }

    /// <summary>True once the game's result has been seen.</summary>
    public bool GameFinished { get; private set; }

    /// <summary>When set, the local seat's decisions are answered by the plain policy after a short pause.</summary>
    public bool DemoAutoAnswer { get; set; }

    /// <summary>When set, local decisions are answered by the plain policy at once (the autopilot driver).</summary>
    public bool Autopilot { get; set; }

    /// <summary>The latest battle folded from the history (the self-check reads it).</summary>
    public Battle? LastBattle => _history.LatestBattle;

    /// <summary>Supplies the shipped data the table draws from. Call before adding the screen to the tree.</summary>
    public void Configure(BoardCatalog catalog, MapData map, TerritoryLayout layout)
    {
        _catalog = catalog;
        _map = map;
        _layout = layout;
    }

    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    public override void _Ready()
    {
        var background = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var layout = new VBoxContainer();
        layout.SetAnchorsPreset(LayoutPreset.FullRect);
        layout.AddThemeConstantOverride("separation", 0);
        AddChild(layout);
        layout.AddChild(BuildHeader());

        var margin = Ui.Margins(12, 10, 12, 10);
        margin.SizeFlagsVertical = SizeFlags.ExpandFill;
        layout.AddChild(margin);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        margin.AddChild(body);
        body.AddChild(BuildBanner());

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(split);
        _mapView = new PolygonBoardView
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(420, 280),
        };
        _mapView.TerritoryClicked += code => _panel.HandleTerritoryClick(code);
        split.AddChild(_mapView);

        // The column scrolls if the window is too short for it, so no
        // panel is ever pushed off the bottom of the screen.
        var sideScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(UiConstants.TableSideWidth, 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        split.AddChild(sideScroll);
        var side = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        side.AddThemeConstantOverride("separation", 10);
        sideScroll.AddChild(side);

        side.AddChild(BuildPlayersCard());
        side.AddChild(BuildBattleCard());
        side.AddChild(BuildDecisionCard());
        side.AddChild(BuildInfoTabs());

        _mapView.SetBoard(_catalog, _map, _layout, null);
        SetBanner("SETTING UP", "Setting up the table…", UiTheme.BannerWaiting);

        // Overlays: the host edit panel and the help, built over
        // the table and hidden until opened.
        _editPanel = new HostEditPanel();
        _editPanel.Confirmed += OnEditConfirmed;
        AddChild(_editPanel);
        _helpPanel = new HelpPanel();
        AddChild(_helpPanel);
    }

    private Control BuildHeader()
    {
        var header = new PanelContainer { ThemeTypeVariation = UiTheme.HeaderBar };
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 22);
        header.AddChild(row);
        var title = Ui.Text("Agent Risk", UiTheme.HeadingLabel, wrap: false);
        title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(title);

        // The game's facts, each with its caption: a reader new to the
        // game learns what each value is from the caption beside it.
        var facts = FactField.Row();
        facts.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        facts.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(facts);
        _modeFact = new FactField("Game type",
            "Standard: three to six players; the winner holds every territory. Duo: two players and the " +
            "Rebels, a neutral army the engine plays; the game ends when one of the two players is eliminated.");
        _seedFact = new FactField("Seed",
            "The number that fixes this game's dice, deck and deal. The same seed and the same decisions " +
            "replay the same game; the record names it.");
        _roundFact = new FactField("Round", "A round is every seat taking one turn, in turn order.");
        _phaseFact = new FactField("Current phase",
            "Where the game is in a turn. Setup comes first: claiming territories and placing starting armies. " +
            "Then each turn runs reinforcements (trade cards and place new armies), attack, and fortify (one move " +
            "of armies between connected territories).");
        _turnFact = new FactField("Current player", "The seat whose turn it is.");
        foreach (var fact in new[] { _modeFact, _seedFact, _roundFact, _phaseFact, _turnFact })
            facts.AddChild(fact);
        // The host-edited flag: stands among the facts for the
        // rest of the game's life once any host has edited it, learned
        // from the session at the host door and from the feed's HOST
        // EDIT events at every other door.
        _editedBadge = Ui.Text("Host-edited game", UiTheme.WarningLabel, wrap: false);
        _editedBadge.VerticalAlignment = VerticalAlignment.Center;
        _editedBadge.TooltipText = "The host edited the position during this game. The record says what changed.";
        _editedBadge.MouseFilter = MouseFilterEnum.Pass;
        _editedBadge.Visible = false;
        facts.AddChild(_editedBadge);

        // Who this screen is at the table: its colour, and its role among
        // the three the app can take, the held one lit.
        _colourFact = new FactField("Your colour", "The seat this screen plays. The table knows a seat by its colour.");
        _colourFact.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(_colourFact);
        var role = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        role.AddThemeConstantOverride("separation", 6);
        role.AddChild(new Label { Text = "Role:", ThemeTypeVariation = UiTheme.HintLabel, VerticalAlignment = VerticalAlignment.Center });
        _role = new RoleIndicator();
        role.AddChild(_role);
        row.AddChild(role);

        var buttons = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var help = Ui.Button("Help", UiTheme.SmallButton, "What each part of this screen shows, and what the controls do.");
        help.Pressed += () => _helpPanel.Open(_door == TableDoor.Host);
        buttons.AddChild(help);
        var leave = Ui.Button("Leave the table", UiTheme.SmallButton,
            "Return to the front door. A hosted game closes; a joined seat's connection closes and the host decides what happens to it.");
        leave.Pressed += () => LeaveRequested?.Invoke();
        buttons.AddChild(leave);
        row.AddChild(buttons);
        return header;
    }

    private Control BuildBanner()
    {
        _bannerPanel = new PanelContainer { ThemeTypeVariation = UiTheme.BannerWaiting };
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        _bannerPanel.AddChild(row);
        _bannerTag = new Label
        {
            ThemeTypeVariation = UiTheme.CaptionLabel,
            CustomMinimumSize = new Vector2(150, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.AddChild(_bannerTag);
        var lines = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        lines.AddThemeConstantOverride("separation", 2);
        row.AddChild(lines);
        _banner = Ui.Text("");
        _banner.AddThemeFontSizeOverride("font_size", UiTheme.HeadingSize - 1);
        _banner.VerticalAlignment = VerticalAlignment.Center;
        lines.AddChild(_banner);
        // The table's latest notice (a connection, a re-sent prompt, an
        // edit, the result), under what the game waits for; hidden while
        // there is nothing to add.
        _status = Ui.Hint("");
        _status.Visible = false;
        lines.AddChild(_status);
        return _bannerPanel;
    }

    private Control BuildPlayersCard()
    {
        var (card, body) = CompactCard();
        body.AddThemeConstantOverride("separation", 6);
        _playersGrid = new GridContainer { Columns = 6 };
        _playersGrid.AddThemeConstantOverride("h_separation", 10);
        _playersGrid.AddThemeConstantOverride("v_separation", 4);
        body.AddChild(_playersGrid);
        _hand = Ui.Text("");
        body.AddChild(_hand);
        RebuildPlayersGrid(Array.Empty<string>());
        return card;
    }

    private Control BuildBattleCard()
    {
        var (card, body) = CompactCard();
        body.AddChild(Ui.Text("LAST BATTLE", UiTheme.CaptionLabel, wrap: false));
        _diceView = new DiceView();
        body.AddChild(_diceView);
        return card;
    }

    private Control BuildDecisionCard()
    {
        var (card, body) = CompactCard();
        body.AddChild(Ui.Text("YOUR DECISION", UiTheme.CaptionLabel, wrap: false));
        // The decision panel is where the game is played; it holds a
        // protected share of the column, so a normal window can never
        // squeeze its controls out of sight (at a 969px window the
        // panel's body once collapsed to nothing and the confirm button
        // existed but could not be seen). Its height is fixed and its
        // controls scroll inside it; the tabs below take what is left.
        _panel = new DecisionPanel
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        _panel.ReplyReady += OnPanelReply;
        _panel.HighlightsChanged += h => _mapView.SetHighlights(h.Selectable, h.Selected, h.Targets);
        body.AddChild(_panel);
        _decisionCard = card;
        return card;
    }

    private Control BuildInfoTabs()
    {
        _infoTabs = new TabContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 110),
        };
        _infoTabs.AddThemeFontSizeOverride("font_size", UiTheme.BodySize);

        var record = new VBoxContainer();
        record.AddThemeConstantOverride("separation", 6);
        var recordBar = new HBoxContainer();
        _protocolToggle = new CheckBox
        {
            Text = "Protocol lines",
            TooltipText = "Show the record exactly as the seats receive it in their prompts.",
        };
        _protocolToggle.Toggled += _ => RenderRecord();
        recordBar.AddChild(_protocolToggle);
        var recordHint = Ui.Hint("Every event of the game, in order.");
        recordHint.VerticalAlignment = VerticalAlignment.Center;
        recordBar.AddChild(recordHint);
        record.AddChild(recordBar);
        _historyText = new RichTextLabel
        {
            BbcodeEnabled = false,
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _historyText.AddThemeFontSizeOverride("normal_font_size", UiTheme.HintSize);
        record.AddChild(_historyText);
        _infoTabs.AddChild(record);
        _infoTabs.SetTabTitle(0, "Record of play");

        _seatPanelText = new RichTextLabel
        {
            BbcodeEnabled = false,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _seatPanelText.AddThemeFontSizeOverride("normal_font_size", UiTheme.HintSize);
        _infoTabs.AddChild(_seatPanelText);
        _infoTabs.SetTabTitle(1, "Seats");
        _infoTabs.SetTabTooltip(1, "Each seat's latest prompt and its reply.");
        return _infoTabs;
    }

    private static (PanelContainer Card, VBoxContainer Body) CompactCard()
    {
        var (card, body) = Ui.Card();
        card.AddThemeStyleboxOverride("panel", CompactCardStyle);
        body.AddThemeConstantOverride("separation", 4);
        return (card, body);
    }

    private static readonly StyleBoxFlat CompactCardStyle = MakeCompactCardStyle();

    private static StyleBoxFlat MakeCompactCardStyle()
    {
        var c = UiConstants.Chrome;
        var box = new StyleBoxFlat
        {
            BgColor = c.Surface,
            BorderColor = c.Border,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 9,
            ContentMarginBottom = 10,
        };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(6);
        return box;
    }

    // ------------------------------------------------------------------
    // Entering through a door
    // ------------------------------------------------------------------

    /// <summary>
    /// Enters a table this app hosts. The host's notices arrive on the
    /// given queue; the connect address is the one the joining words quote.
    /// </summary>
    public void BeginHosted(HostedTable hosted, ConcurrentQueue<string> hostNotices, string connectAddress)
    {
        _door = TableDoor.Host;
        _hosted = hosted;
        _hostNotices = hostNotices;
        _seatPlans = hosted.SeatPlans;
        _mySeat = hosted.LocalSeatCode;
        _connectAddress = connectAddress;
        _modeText = hosted.Mode == GameMode.Duo ? "Duo" : "Standard";
        _seedText = hosted.EffectiveSeed.ToString();
        foreach (var plan in hosted.SeatPlans)
            if (plan.Name.Trim().Length > 0) _names[plan.Color] = plan.Name.Trim();

        BuildHostTab();
        _decisionCard.Visible = _mySeat is not null;
        if (_mySeat is null) _infoTabs.CurrentTab = _infoTabs.GetTabIdxFromControl(_hostTab!);
        _role.SetRole(TableDoor.Host);
        SetStatus($"Listening on port {hosted.ListenPort}.");
        _panel.ShowWaiting("The table is filling.", "The game starts when every seat has joined.");
        RefreshFacts();
        RefreshBanner();

        // The run task is watched from the frame loop, once, under
        // its guard — not from here as well: two watches delivered
        // the result twice, and the history printed it twice.
        StartWatchTap(hosted.WatchTapAddresses.Select(a => a.ToString()).ToList(), hosted.ListenPort);
    }

    /// <summary>Enters a table hosted elsewhere, playing one seat; a watch connection opens alongside.</summary>
    public void BeginJoined(SeatClient client, string mySeat, string myName, string host, int port)
    {
        _door = TableDoor.Join;
        _seatClient = client;
        _mySeat = mySeat;
        if (myName.Length > 0) _names[mySeat] = myName;
        WireSeatClient(client);
        client.StartReading();
        _role.SetRole(TableDoor.Join, $"{host}:{port}");
        SetStatus($"Seated at the table at {host}:{port}. The game starts when every seat has joined.");
        _panel.ShowWaiting("You are seated.", "Your first decision will appear here.");
        RefreshFacts();
        RefreshBanner();
        StartWatchTap(new[] { host }, port);
    }

    /// <summary>Enters a table to watch only: the feed drives everything, no controls exist.</summary>
    public void BeginWatched(SeatClient watchTap)
    {
        _door = TableDoor.Watch;
        _mySeat = null;
        _watchTap = watchTap;
        _tapWanted = true;
        WireTap(watchTap);
        watchTap.StartReading();
        _decisionCard.Visible = false;
        _role.SetRole(TableDoor.Watch);
        SetStatus("Connected as a watcher.");
        RefreshFacts();
        RefreshBanner();
    }

    /// <summary>Opens the help over the table (the screenshot run photographs it).</summary>
    public void ShowHelpForScreenshot() => _helpPanel.Open(_door == TableDoor.Host);

    /// <summary>Closes the help (the screenshot run moves on from it).</summary>
    public void CloseHelpForScreenshot() => _helpPanel.Visible = false;

    /// <summary>The last decision put to this screen's seat (the screenshot run reads its seat and board).</summary>
    public DecisionPrompt? LastLocalPrompt => _lastLocalPrompt;

    /// <summary>
    /// Re-presents the last decision put to this screen, on its own
    /// board, as another kind of ask: the screenshot run photographs each
    /// set of controls on a real position. Nothing is sent from it.
    /// </summary>
    public void ShowAskForScreenshot(DecisionAsk ask, string phase, BoardState? board = null)
    {
        if (_lastLocalPrompt is not { } last) return;
        if (board is not null) _mapView.SetState(board);
        var prompt = new DecisionPrompt
        {
            Number = last.Number,
            Seat = last.Seat,
            Round = last.Round,
            Phase = phase,
            SummaryLines = Array.Empty<string>(),
            Board = board ?? last.Board,
            Ask = ask,
            AskLines = Array.Empty<string>(),
        };
        _myDecisionPending = true;
        _myAsk = ask;
        _myStrikeThree = false;
        _panel.ShowDecision(prompt, _catalog);
        // The header follows the staged phase, as it follows a real prompt's board.
        _phaseFact.Set(UiConstants.PhaseTitle(phase));
        RefreshBanner();
    }

    /// <summary>
    /// Shows a staged round-by-round battle on the route in the dice tray
    /// (the screenshot run photographs the tray's round layout this way:
    /// the stand-ins attack all at once, so a played game rarely has one).
    /// </summary>
    public void ShowRoundBattleForScreenshot(string from, string to)
    {
        if (_lastLocalPrompt is not { } last) return;
        var tracker = new BattleTracker();
        tracker.SeedOwnership(last.Board);
        tracker.FoldSummary(new[]
        {
            $"{last.Seat} ATK {from}>{to} RND",
            "RND 3v2 A[6 4 1] D[5 3] A-0 D-2",
            "RND 3v2 A[5 2 1] D[6 1] A-1 D-1",
            $"RND 3v1 A[6 2 2] D[3] A-0 D-1 | CAP {to} MOV3",
        });
        _diceView.ShowBattle(tracker.LastBattle, _catalog);
    }

    /// <summary>Clicks a territory on the map, as the player would (the screenshot run picks routes this way).</summary>
    public void ClickTerritoryForScreenshot(string code) => _panel.HandleTerritoryClick(code);

    /// <summary>Opens the host edit panel over a paused hosted table (the screenshot run photographs it).</summary>
    public void ShowEditForScreenshot() => OpenEditPanel();

    /// <summary>Closes the host edit panel without applying anything.</summary>
    public void CloseEditForScreenshot() => _editPanel.Close();

    /// <summary>Disconnects everything this screen holds. The shell frees the screen afterwards.</summary>
    public void Shutdown()
    {
        _seatClient?.Dispose();
        _watchTap?.Dispose();
        _seatClient = null;
        _watchTap = null;
    }

    private void WireSeatClient(SeatClient client)
    {
        client.BlockReceived += block => _inbox.Enqueue(new InboxItem.SeatBlock(block));
        client.Closed += () => _inbox.Enqueue(new InboxItem.SeatClosed());
    }

    private void WireTap(SeatClient tap)
    {
        tap.BlockReceived += block => _inbox.Enqueue(new InboxItem.TapBlock(block.Text));
        tap.Closed += () => { /* a lost watch connection only narrows the panel */ };
    }

    /// <summary>
    /// Opens the watch connection on a background thread (the handshake
    /// blocks). Failure is not fatal: the seat panel falls back to the
    /// local seat's own exchanges, and the game plays on.
    /// </summary>
    private void StartWatchTap(IReadOnlyList<string> addresses, int port)
    {
        _tapWanted = true;
        var thread = new Thread(() =>
        {
            foreach (string address in addresses)
            {
                try
                {
                    var tap = SeatClient.Connect(address, port, "WATCH");
                    _inbox.Enqueue(new InboxItem.TapReady(tap));
                    return;
                }
                catch (Exception)
                {
                    // Try the next address; the tap is an enhancement, not a seat.
                }
            }
            _inbox.Enqueue(new InboxItem.TapFailed());
        })
        { IsBackground = true, Name = "Watch tap handshake" };
        thread.Start();
    }

    private void WatchRunTask(Task<SessionResult> runTask)
    {
        _ = runTask.ContinueWith(task =>
        {
            SessionResult? result = null;
            string? error = null;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            _inbox.Enqueue(new InboxItem.RunFinished(result, error));
        });
    }

    // ------------------------------------------------------------------
    // The frame: drain everything into the UI
    // ------------------------------------------------------------------

    public override void _Process(double delta)
    {
        // The hosted table's local channel is polled, not queued through
        // the inbox: its requests are already thread-safe hand-offs.
        if (_hosted?.LocalChannel is { } channel)
        {
            while (channel.TryTakeRequest(out var request))
                _inbox.Enqueue(new InboxItem.UiRequest(request));
            while (channel.TryTakeNotice(out string notice))
                _inbox.Enqueue(new InboxItem.UiNotice(notice));
        }
        if (_hosted is not null && _hosted.Session is not null && _hosted.RunTask is not null && !_runFinishNoted)
        {
            _runFinishNoted = true;
            WatchRunTask(_hosted.RunTask);
        }
        if (_hostNotices is { } hostNotices)
            while (hostNotices.TryDequeue(out string? notice))
                if (notice is not null) SetStatus(notice);

        while (_inbox.TryDequeue(out var item))
        {
            switch (item)
            {
                case InboxItem.SeatBlock seatBlock: HandleSeatBlock(seatBlock.Message); break;
                case InboxItem.SeatClosed: HandleSeatClosed(); break;
                case InboxItem.TapReady ready:
                    _watchTap = ready.Client;
                    WireTap(ready.Client);
                    ready.Client.StartReading();
                    break;
                case InboxItem.TapFailed:
                    _tapWanted = false;
                    _seatPanelDirty = true;
                    break;
                case InboxItem.TapBlock tapBlock: ConsumeFeedBlock(tapBlock.Text); break;
                case InboxItem.UiRequest uiRequest: HandleUiRequest(uiRequest.Request); break;
                case InboxItem.UiNotice uiNotice: HandleLocalBlockText(uiNotice.Text, expectsReply: false); break;
                case InboxItem.RunFinished finished: HandleRunFinished(finished); break;
            }
        }

        _panelClock += delta;
        if (_panelClock >= 0.2)
        {
            _panelClock = 0;
            if (_playersDirty) RefreshPlayers();
            if (_seatPanelDirty) RefreshSeatPanel();
        }

        if (_door == TableDoor.Host && _hosted is not null)
        {
            _snapshotClock += delta;
            if (_snapshotClock >= 0.5)
            {
                _snapshotClock = 0;
                RefreshHostArea();
            }
        }

        if (DemoAutoAnswer && _myDecisionPending && _demoClock >= 0)
        {
            _demoClock += delta;
            if (_demoClock > 0.4)
            {
                _demoClock = -1;
                AnswerByPolicy();
            }
        }
    }

    private void SetStatus(string text)
    {
        // Captioned, because a notice can stand for a while after the
        // moment it describes: it is the latest word, not the state.
        Ui.SetStatus(_status, text.Length == 0 ? "" : $"Latest notice: {text}");
    }

    // ------------------------------------------------------------------
    // Blocks from the local seat's own connection or channel
    // ------------------------------------------------------------------

    private void HandleSeatBlock(BlockMessage block) => HandleLocalBlockText(block.Text, block.ExpectsReply);

    private void HandleLocalBlockText(string text, bool expectsReply)
    {
        var message = PromptParser.ParseBlock(text, expectsReply);
        switch (message)
        {
            case PromptMessage.Briefing briefing:
                _catalog = briefing.Catalog;
                _mapView.SetBoard(_catalog, _map, _layout, _board);
                NoteModeLine(briefing.ModeLine);
                AppendRecord(briefing.ModeLine);
                SetStatus("The game is set. Waiting for the first decision…");
                break;

            case PromptMessage.Decision decision:
                PresentDecision(decision.Prompt, reply => _seatClient?.SendReply(reply));
                break;

            case PromptMessage.StrikeThree strike:
                PresentStrikeThree(strike.Moves, reply => _seatClient?.SendReply(reply));
                break;

            case PromptMessage.GameOver over:
                NoteGameOver(over.Winner);
                break;

            case PromptMessage.Refused refused:
                AppendRecord(refused.RawText);
                break;

            case PromptMessage.Unknown unknown:
                if (unknown.ExpectsReply)
                    PresentRawBlock(unknown.RawText, reply => _seatClient?.SendReply(reply));
                else
                    AppendRecord(unknown.RawText);
                break;
        }
    }

    private void HandleUiRequest(UiSeatChannel.PromptRequest request)
    {
        if (request.IsResend)
        {
            // A host poke re-sends the pending question; the surface is
            // already showing it, so the status line simply says so.
            SetStatus("The host re-sent your question.");
            return;
        }
        var message = PromptParser.ParseBlock(request.Text, expectsReply: true);
        switch (message)
        {
            case PromptMessage.Decision decision:
                PresentDecision(decision.Prompt, reply => request.Answer(reply));
                break;
            case PromptMessage.StrikeThree strike:
                PresentStrikeThree(strike.Moves, reply => request.Answer(reply));
                break;
            default:
                PresentRawBlock(request.Text, reply => request.Answer(reply));
                break;
        }
    }

    private void HandleSeatClosed()
    {
        if (_gameOver) return;
        SetStatus("The table closed the connection.");
        _panel.ShowWaiting("The connection closed.", "The host closed this seat's connection, or the network dropped it.");
        SetBanner("DISCONNECTED", "The connection to the table closed. Leave the table to return to the front door.",
            UiTheme.BannerAlert);
    }

    private void HandleRunFinished(InboxItem.RunFinished finished)
    {
        if (finished.Error is not null)
        {
            SetStatus($"The hosted game stopped: {finished.Error}");
            return;
        }
        var result = finished.Result!;
        _result = result;
        _resultDecisions = result.Decisions;
        GameFinished = true;
        string line = result.Status switch
        {
            SessionStatus.Completed => $"Game over: {UiConstants.SeatDisplayName(result.Winner ?? "")} wins, after {result.Decisions} decisions.",
            SessionStatus.Suspended => $"Game suspended after {result.Decisions} decisions. Resume it from the front door's Resume a game tab.",
            SessionStatus.EndedByHost => $"Game ended by the host after {result.Decisions} decisions.",
            _ => $"Game ended ({result.Status}) after {result.Decisions} decisions.",
        };
        // The banner says how the game ended; the notice line has nothing to add.
        SetStatus("");
        if (!_resultLinesAppended)
        {
            // The result stands in the history once, however the
            // completion reached this screen.
            _resultLinesAppended = true;
            AppendRecord(line);
            AppendRecord($"Record: {result.RecordPath}");
        }
        if (result.Status == SessionStatus.Completed)
            NoteGameOver(result.Winner ?? "", fromResult: true);
        _gameOver = true;
        _myDecisionPending = false;
        _pendingSeat = null;
        // The feed's result can land before the session's own; either
        // way the panel ends on the session's account, with the record.
        _panel.ShowWaiting(line, $"The record and the transcript are in the records folder: {result.RecordPath}");
        RefreshFacts();
        RefreshBanner();
        RefreshHostArea();
    }

    // ------------------------------------------------------------------
    // Decision presentation (the local seat's own questions)
    // ------------------------------------------------------------------

    /// <summary>Presents a decision for this screen's seat; the answer sink carries the reply back.</summary>
    public void PresentDecision(DecisionPrompt prompt, Action<string> answerSink)
    {
        _answerSink = answerSink;
        _myDecisionPending = true;
        _myAsk = prompt.Ask;
        _myStrikeThree = false;
        _pendingSeat = prompt.Seat;
        _pendingAsk = prompt.Ask;
        _pendingStrikeThree = false;

        if (_watchTap is null)
        {
            // No feed to carry this prompt: the history and seat panel
            // fold the local seat's own exchanges instead.
            _history.FoldLocalSummary(prompt.SummaryLines, _seedBoard ?? prompt.Board);
            AppendNewHistoryLines();
            _exchanges.NoteLocalPrompt(prompt.Seat, prompt.Number,
                $"DECISION D-{prompt.Number} | You are {prompt.Seat} | Round {prompt.Round} | Phase {prompt.Phase}",
                prompt.Ask);
            _seatPanelDirty = true;
        }
        _seedBoard = prompt.Board;
        _lastLocalPrompt = prompt;
        NoteHand(prompt.Seat, prompt.Board.Hand);
        SetBoard(prompt.Board);
        _diceView.ShowBattle(_history.LatestBattle, _catalog);

        _panel.ShowDecision(prompt, _catalog);
        // The banner says it is this screen's move; the notice line only
        // adds what the banner does not.
        SetStatus(prompt.LastError is null ? "" : "The last reply was not legal; the decision panel says why.");
        RefreshBanner();

        if (Autopilot)
        {
            string? reply = AutoPolicy.Answer(prompt, _catalog);
            if (reply is not null) OnPanelReply(reply);
            else GD.Print($"AUTOPILOT STUCK on {prompt.Ask.GetType().Name}");
        }
        else if (DemoAutoAnswer)
        {
            _demoClock = 0;
            _demoPrompt = prompt;
        }
    }

    private DecisionPrompt? _demoPrompt;
    private DecisionPrompt? _lastLocalPrompt;

    private void PresentStrikeThree(IReadOnlyList<string> moves, Action<string> answerSink)
    {
        _answerSink = answerSink;
        _myDecisionPending = true;
        _myAsk = null;
        _myStrikeThree = true;
        _panel.ShowStrikeThree(moves);
        SetStatus("Two replies were not accepted: pick one of the listed moves.");
        RefreshBanner();
        if (Autopilot) OnPanelReply(AutoPolicy.AnswerStrikeThree());
        else if (DemoAutoAnswer)
        {
            _demoClock = 0;
            _demoPrompt = null;
        }
    }

    private void PresentRawBlock(string rawText, Action<string> answerSink)
    {
        _answerSink = answerSink;
        _myDecisionPending = true;
        _myAsk = null;
        _myStrikeThree = false;
        _panel.ShowRawBlock(rawText);
        RefreshBanner();
    }

    private void AnswerByPolicy()
    {
        if (_myStrikeThree)
        {
            OnPanelReply(AutoPolicy.AnswerStrikeThree());
            return;
        }
        if (_demoPrompt is not null)
        {
            string? reply = AutoPolicy.Answer(_demoPrompt, _catalog);
            if (reply is not null) OnPanelReply(reply);
        }
    }

    private void OnPanelReply(string replyLine)
    {
        var sink = _answerSink;
        _answerSink = null;
        _myDecisionPending = false;
        _myAsk = null;
        _myStrikeThree = false;
        _pendingSeat = null;
        _pendingAsk = null;
        if (_mySeat is not null) _exchanges.NoteLocalReply(_mySeat, replyLine);
        _seatPanelDirty = true;
        sink?.Invoke(replyLine);
        _panel.ShowWaiting("Reply sent.", $"You answered: {replyLine}");
        SetStatus("");
        RefreshBanner();
    }

    // ------------------------------------------------------------------
    // The spectator feed: one reading path for every door
    // ------------------------------------------------------------------

    private void ConsumeFeedBlock(string text)
    {
        var entry = FeedEntry.Parse(text);
        FeedEntriesSeen++;
        _exchanges.Consume(entry);
        _history.Consume(entry);
        AppendNewHistoryLines();

        switch (entry.Kind)
        {
            case FeedEntryKind.Board:
                if (entry.Board is not null)
                {
                    BoardEntriesSeen++;
                    SetBoard(entry.Board);
                }
                break;
            case FeedEntryKind.Prompt:
                PromptEntriesSeen++;
                _pendingSeat = entry.Seat;
                _pendingAsk = entry.Prompt?.Ask;
                _pendingStrikeThree = entry.Block is PromptMessage.StrikeThree;
                if (entry.Prompt is not null)
                    NoteHand(entry.Prompt.Seat, entry.Prompt.Board.Hand);
                _diceView.ShowBattle(_history.LatestBattle, _catalog);
                RefreshBanner();
                break;
            case FeedEntryKind.Reply:
            case FeedEntryKind.NoReply:
                if (entry.Seat == _pendingSeat)
                {
                    _pendingSeat = null;
                    _pendingAsk = null;
                    _pendingStrikeThree = false;
                }
                RefreshBanner();
                break;
            case FeedEntryKind.Briefing:
            case FeedEntryKind.ResumeBriefing:
                if (PromptParser.ParseBlock(entry.Payload, expectsReply: false) is PromptMessage.Briefing briefing)
                {
                    _catalog = briefing.Catalog;
                    _mapView.SetBoard(_catalog, _map, _layout, _board);
                    NoteModeLine(briefing.ModeLine);
                }
                break;
            case FeedEntryKind.HostEvent:
                // A HOST EDIT event anywhere on the feed flags the
                // table for every door, the host's included.
                if (!_hostEdited && entry.Marker.StartsWith("### HOST EDIT", StringComparison.Ordinal))
                {
                    _hostEdited = true;
                    RefreshEditedBadge();
                }
                break;
            case FeedEntryKind.Result:
                GameFinished = true;
                _resultDecisions ??= DecisionsFromResult(entry.Payload);
                string? winner = WinnerFromResult(entry.Payload);
                if (winner is not null) NoteGameOver(winner, fromResult: true);
                else RefreshBanner();
                break;
        }
        _seatPanelDirty = true;
    }

    private static string? WinnerFromResult(string payload)
    {
        foreach (string line in payload.Replace("\r\n", "\n").Split('\n'))
            if (line.StartsWith("winner: ", StringComparison.Ordinal))
            {
                string winner = line["winner: ".Length..].Trim();
                return winner.Length > 0 && winner != "none" ? winner : null;
            }
        return null;
    }

    private static int? DecisionsFromResult(string payload)
    {
        foreach (string line in payload.Replace("\r\n", "\n").Split('\n'))
            if (line.StartsWith("decisions: ", StringComparison.Ordinal)
                && int.TryParse(line["decisions: ".Length..].Trim(), out int decisions))
                return decisions;
        return null;
    }

    /// <summary>Takes the game's mode from a briefing's mode line ("Mode: Standard. Seats: ...").</summary>
    private void NoteModeLine(string modeLine)
    {
        if (_modeText.Length > 0) return;
        const string prefix = "Mode: ";
        int at = modeLine.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return;
        int end = modeLine.IndexOf('.', at);
        _modeText = (end < 0 ? modeLine[(at + prefix.Length)..] : modeLine[(at + prefix.Length)..end]).Trim();
        RefreshFacts();
    }

    private void NoteGameOver(string winner, bool fromResult = false)
    {
        if (_gameOver) return;
        _gameOver = true;
        _winner = winner;
        _myDecisionPending = false;
        _pendingSeat = null;
        string decisions = _resultDecisions is { } count ? $" after {count} decisions" : "";
        _panel.ShowWaiting(
            winner.Length > 0 ? $"{SeatLabel(winner)} wins the game{decisions}." : "The game is over.",
            _result is { } done
                ? $"The record and transcript are in the records folder: {done.RecordPath}"
                : "The host keeps the record and the transcript of this game.");
        // The banner says how the game ended; the notice line has nothing to add.
        SetStatus("");
        _playersDirty = true;
        RefreshFacts();
        RefreshBanner();
        GameOverWinner?.Invoke(winner);
    }

    // ------------------------------------------------------------------
    // Rendering the standing state
    // ------------------------------------------------------------------

    private void SetBoard(BoardState board)
    {
        _board = board;
        _mapView.SetState(board);
        _playersDirty = true;
        RefreshFacts();
    }

    private void NoteHand(string seat, IReadOnlyList<string> cards)
    {
        // The hand on show is the local seat's own in play modes; at the
        // watch door it is the hand of the seat the prompt was addressed
        // to, labelled as such. Feed boards carry a hand too, but it
        // belongs to the next seat to be asked, so prompts are the
        // labelled source.
        if (_door == TableDoor.Watch || seat == _mySeat)
        {
            _handSeat = seat;
            _handCards = cards;
            RefreshHand();
        }
    }

    /// <summary>The players grid: one row per seat (and the Rebels in Duo), rebuilt only when the seats change.</summary>
    private void RefreshPlayers()
    {
        _playersDirty = false;
        if (_board is null) return;
        var armies = new Dictionary<string, int>(StringComparer.Ordinal);
        var territories = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var territory in _board.Territories.Values)
            if (territory.Owner is not null)
            {
                armies[territory.Owner] = armies.GetValueOrDefault(territory.Owner) + territory.Armies;
                territories[territory.Owner] = territories.GetValueOrDefault(territory.Owner) + 1;
            }

        var codes = _board.Players.Select(p => p.Code).ToList();
        bool rebels = territories.ContainsKey(RulesConstants.RebelCode) && codes.All(c => c != RulesConstants.RebelCode);
        if (rebels) codes.Add(RulesConstants.RebelCode);
        RebuildPlayersGrid(codes);

        foreach (var player in _board.Players)
        {
            if (!_playerRows.TryGetValue(player.Code, out var row)) continue;
            row.Territories.Text = player.Territories.ToString();
            row.Armies.Text = armies.GetValueOrDefault(player.Code).ToString();
            row.Cards.Text = player.Cards.ToString();
            var notes = new List<string>();
            bool toMove = player.Code == _board.TurnSeat && !_gameOver;
            if (player.Code == _mySeat) notes.Add("you");
            if (toMove) notes.Add("to move");
            else if (player.Code == RulesConstants.RebelCode) notes.Add("neutral");
            if (_gameOver && player.Code == _winner) notes.Add("winner");
            // Read as a phrase under the column's caption: "You, to move".
            row.Note.Text = Capitalized(string.Join(", ", notes));
            row.Name.ThemeTypeVariation = toMove || (_gameOver && player.Code == _winner) ? UiTheme.SubheadingLabel : "";
        }
        if (rebels && _playerRows.TryGetValue(RulesConstants.RebelCode, out var rebelRow))
        {
            rebelRow.Territories.Text = territories[RulesConstants.RebelCode].ToString();
            rebelRow.Armies.Text = armies.GetValueOrDefault(RulesConstants.RebelCode).ToString();
            rebelRow.Cards.Text = "–";
            rebelRow.Note.Text = _board.TurnSeat == RulesConstants.RebelCode && !_gameOver ? "To move" : "Neutral";
        }
        RefreshHand();
    }

    private void RebuildPlayersGrid(IReadOnlyList<string> codes)
    {
        string key = string.Join(",", codes);
        if (key == _playersKey && _playersGrid.GetChildCount() > 0) return;
        _playersKey = key;
        foreach (Node child in _playersGrid.GetChildren()) child.QueueFree();
        _playerRows.Clear();

        _playersGrid.AddChild(new Control());
        _playersGrid.AddChild(Caption("PLAYER", expand: true));
        _playersGrid.AddChild(Caption("TERRITORIES"));
        _playersGrid.AddChild(Caption("ARMIES"));
        _playersGrid.AddChild(Caption("CARDS"));
        _playersGrid.AddChild(Caption("STATUS", left: true));

        foreach (string code in codes)
        {
            var row = new PlayerRow(
                Ui.Swatch(UiConstants.SeatColor(code), 16),
                new Label { Text = code == RulesConstants.RebelCode ? "The Rebels" : SeatLabel(code), SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true },
                Number(), Number(), Number(),
                new Label { ThemeTypeVariation = UiTheme.HintLabel, CustomMinimumSize = new Vector2(84, 0) });
            _playersGrid.AddChild(row.Swatch);
            _playersGrid.AddChild(row.Name);
            _playersGrid.AddChild(row.Territories);
            _playersGrid.AddChild(row.Armies);
            _playersGrid.AddChild(row.Cards);
            _playersGrid.AddChild(row.Note);
            _playerRows[code] = row;
        }
        if (codes.Count == 0)
        {
            _playersGrid.AddChild(new Control());
            _playersGrid.AddChild(Ui.Hint("The players appear when the game begins."));
        }
    }

    private static Label Caption(string text, bool expand = false, bool left = false) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.CaptionLabel,
        HorizontalAlignment = expand || left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
        SizeFlagsHorizontal = expand ? SizeFlags.ExpandFill : SizeFlags.Fill,
    };

    private static Label Number() => new() { HorizontalAlignment = HorizontalAlignment.Right };

    private void RefreshHand()
    {
        string trade = _board is null ? "" : $"The next set of cards trades for {_board.NextTradeValue} armies.";
        if (_handSeat is null)
        {
            _hand.Text = trade;
            return;
        }
        string cards = _handCards.Count == 0
            ? "no cards"
            : string.Join(", ", _handCards.Select(c => _catalog.CardDisplayName(c)));
        string hand = _door == TableDoor.Watch || _handSeat != _mySeat
            ? $"{SeatLabel(_handSeat)} holds {cards}."
            : $"Your cards: {cards}.";
        _hand.Text = $"{hand} {trade}";
    }

    /// <summary>The header's facts: game type, seed, round, phase, whose turn (or the winner), and this screen's colour.</summary>
    private void RefreshFacts()
    {
        _modeFact.Set(_modeText.Length > 0 ? _modeText : ModeFromBoard(_board));
        _seedFact.Set(_seedText);
        _roundFact.Set(_board is null ? "" : _board.Round.ToString());
        _phaseFact.Set(_board is null || _gameOver ? "" : UiConstants.PhaseTitle(_board.Phase));
        if (_gameOver)
        {
            _turnFact.SetCaption("Winner");
            string winner = _winner ?? "";
            _turnFact.Set(winner.Length > 0 ? SeatLabel(winner) : "", winner.Length > 0 ? UiConstants.SeatColor(winner) : null);
        }
        else
        {
            _turnFact.SetCaption("Current player");
            _turnFact.Set(_board is null ? "" : SeatLabel(_board.TurnSeat), _board is null ? null : UiConstants.SeatColor(_board.TurnSeat));
        }
        if (_mySeat is not null)
            _colourFact.Set(UiConstants.SeatDisplayName(_mySeat), UiConstants.SeatColor(_mySeat));
        else
            _colourFact.Set("None");
        _colourFact.TooltipText = _mySeat is not null
            ? $"This screen plays {SeatLabel(_mySeat)}. The table knows a seat by its colour."
            : _door == TableDoor.Watch
                ? "A watcher holds no seat: it sees the whole table and makes no decisions."
                : "Every seat at this table is played from elsewhere (agents, clients, or stand-ins); this screen hosts and watches.";
    }

    /// <summary>
    /// The game type a watcher who joined after the briefing reads from
    /// the board: Duo seats exactly two players (the Rebels aside),
    /// Standard three to six.
    /// </summary>
    private static string ModeFromBoard(BoardState? board)
    {
        if (board is null) return "";
        int players = board.Players.Count(p => p.Code != RulesConstants.RebelCode);
        return players == 2 ? "Duo" : players >= 3 ? "Standard" : "";
    }

    private static string Capitalized(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private string _bannerKey = "";

    private void SetBanner(string tag, string text, string variation)
    {
        // The host area refreshes the banner twice a second; restyling it
        // only when it changes keeps those refreshes free.
        string key = $"{tag}|{variation}|{text}";
        if (key == _bannerKey) return;
        _bannerKey = key;
        _bannerTag.Text = tag;
        _banner.Text = text;
        _bannerPanel.ThemeTypeVariation = variation;
        _bannerTag.RemoveThemeColorOverride("font_color");
        if (variation == UiTheme.BannerAction)
            _bannerTag.AddThemeColorOverride("font_color", UiConstants.Chrome.AccentHover);
        else if (variation == UiTheme.BannerAlert)
            _bannerTag.AddThemeColorOverride("font_color", UiConstants.Chrome.Warning);
    }

    /// <summary>
    /// The banner says what the game is waiting for, most urgent first:
    /// the result, a decision on this screen, something only the host
    /// can resolve, seats still joining, then whichever seat is deciding.
    /// </summary>
    private void RefreshBanner()
    {
        if (_gameOver)
        {
            string after = _resultDecisions is { } count ? $" after {count} decisions" : "";
            string saved = _door == TableDoor.Host
                ? " The record and the transcript are saved in the records folder (Host tab)."
                : "";
            string text = _winner is { Length: > 0 }
                ? $"{SeatLabel(_winner)} wins the game{after}.{saved}"
                : _result?.Status == SessionStatus.Suspended
                    ? $"The game is suspended{after}. Resume it from the front door's Resume a game tab."
                    : _result?.Status == SessionStatus.EndedByHost
                        ? $"The host ended the game{after}, with no winner.{saved}"
                        : $"The game is over{after}.{saved}";
            SetBanner(_result?.Status == SessionStatus.Suspended ? "SUSPENDED" : "GAME OVER", text, UiTheme.BannerWaiting);
            return;
        }
        if (_myDecisionPending)
        {
            string text = _myStrikeThree
                ? Guidance.StrikeThreeInstruction
                : _myAsk is not null
                    ? Guidance.InstructionFor(_myAsk, TerritoryDisplayName)
                    : "Read the question in the decision panel and answer with one line.";
            SetBanner("YOUR DECISION", text, UiTheme.BannerAction);
            return;
        }
        if (_door == TableDoor.Host && _hosted?.Session?.CurrentPause is { } pause && _lastSnapshot?.Status == SessionStatus.Paused)
        {
            SetBanner("HOST ACTION NEEDED",
                $"{SeatLabel(pause.Seat)} could not answer decision {pause.DecisionNumber}. In the Host tab, answer " +
                "for it once, replace it, or end the game.",
                UiTheme.BannerAlert);
            return;
        }
        if (_door == TableDoor.Host && _lastSnapshot?.Status == SessionStatus.HostPaused)
        {
            SetBanner("PAUSED", "You paused the game. Resume it from the Host tab.", UiTheme.BannerAlert);
            return;
        }
        if (_door == TableDoor.Host && _hosted is not null && _hosted.OpenSeats.Count > 0)
        {
            string seats = string.Join(", ", _hosted.OpenSeats.Select(SeatLabel));
            SetBanner("WAITING FOR SEATS",
                $"Waiting for {seats} to join on port {_hosted.ListenPort}. The Host tab has their joining instructions.",
                UiTheme.BannerWaiting);
            return;
        }
        if (_door == TableDoor.Host && _lastSnapshot?.Seats.FirstOrDefault(s => s.Stalled && s.Code != _mySeat) is { } stalled)
        {
            SetBanner("SEAT STALLED",
                $"{SeatLabel(stalled.Code)} has not answered decision {stalled.PendingDecisionNumber} in a while. " +
                "You can poke or replace it in the Host tab.",
                UiTheme.BannerAlert);
            return;
        }
        if (_pendingSeat is not null)
        {
            string phrase = _pendingStrikeThree
                ? Guidance.StrikeThreeWaitingPhrase
                : _pendingAsk is not null
                    ? Guidance.WaitingPhraseFor(_pendingAsk)
                    : "make a decision";
            SetBanner("WAITING",
                _pendingSeat == _mySeat ? "The table is waiting on you." : $"Waiting on {SeatLabel(_pendingSeat)} to {phrase}.",
                UiTheme.BannerWaiting);
            return;
        }
        SetBanner(_door == TableDoor.Watch ? "WATCHING" : "WAITING",
            _door == TableDoor.Watch
                ? "Watching the table. Every seat's prompts and replies show in the Seats tab."
                : _board is null ? "Setting up the table…" : "Waiting for the table…",
            UiTheme.BannerWaiting);
    }

    private string TerritoryDisplayName(string code) =>
        _catalog is not null && _catalog.TryTerritory(code, out var info) ? info.Name : code;

    private string SeatLabel(string code)
    {
        string color = code == RulesConstants.RebelCode ? "The Rebels" : UiConstants.SeatDisplayName(code);
        return _names.TryGetValue(code, out string? name) ? $"{color} ({name})" : color;
    }

    // ------------------------------------------------------------------
    // The record of play: plain words, or the protocol lines
    // ------------------------------------------------------------------

    private int _historyShown;

    private void AppendNewHistoryLines()
    {
        var lines = _history.Lines;
        for (int i = _historyShown; i < lines.Count; i++)
            AppendRecord(lines[i]);
        _historyShown = lines.Count;
    }

    private void AppendRecord(string line)
    {
        // Direct lines (briefing mode line, result summaries) join the
        // same view as the folded history; the fold counter only tracks
        // FeedHistory's lines.
        _recordLines.Add(line);
        WriteRecordLine(line);
    }

    private void RenderRecord()
    {
        _historyText.Clear();
        foreach (string line in _recordLines) WriteRecordLine(line);
    }

    private NarrationNames? _narrationNames;

    private NarrationNames Names => _narrationNames ??= new NarrationNames(
        Territory: TerritoryDisplayName,
        Seat: code => code == RulesConstants.RebelCode ? "the Rebels" : UiConstants.SeatDisplayName(code),
        Continent: code => _catalog.Continents.FirstOrDefault(c => c.Code == code)?.Name ?? code,
        Card: code => _catalog.CardDisplayName(code));

    private void WriteRecordLine(string line)
    {
        if (_protocolToggle.ButtonPressed)
        {
            _historyText.PushFont(UiTheme.CodeFont);
            _historyText.AddText(line);
            _historyText.Pop();
            _historyText.Newline();
            return;
        }
        var narrated = EventNarration.Describe(line, Names);
        if (narrated.Actor is { } actor)
        {
            _historyText.PushColor(UiConstants.SeatColor(actor));
            _historyText.AddText("■ ");
            _historyText.Pop();
        }
        else
        {
            _historyText.AddText("   ");
        }
        _historyText.AddText(narrated.Text);
        _historyText.Newline();
    }

    // ------------------------------------------------------------------
    // The seats tab: each seat's latest prompt and reply
    // ------------------------------------------------------------------

    private void RefreshSeatPanel()
    {
        _seatPanelDirty = false;
        var codes = new List<string>();
        if (_board is not null)
            codes.AddRange(_board.Players.Select(p => p.Code));
        foreach (var plan in _seatPlans)
            if (!codes.Contains(plan.Color)) codes.Add(plan.Color);
        if (_mySeat is not null && !codes.Contains(_mySeat)) codes.Add(_mySeat);
        foreach (string code in _exchanges.SeatCodes)
            if (!codes.Contains(code)) codes.Add(code);

        _seatPanelText.Clear();
        var muted = UiConstants.Chrome.TextMuted;
        if (_watchTap is null && _tapWanted == false && _door != TableDoor.Watch)
        {
            _seatPanelText.PushColor(muted);
            _seatPanelText.AddText("The watch connection did not open, so this tab shows your own seat only.");
            _seatPanelText.Pop();
            _seatPanelText.Newline();
            _seatPanelText.Newline();
        }
        if (codes.Count == 0)
        {
            _seatPanelText.PushColor(muted);
            _seatPanelText.AddText("No seat has been asked anything yet.");
            _seatPanelText.Pop();
            return;
        }

        foreach (string code in codes)
        {
            _seatPanelText.PushColor(UiConstants.SeatColor(code));
            _seatPanelText.AddText("■ ");
            _seatPanelText.Pop();
            _seatPanelText.PushFont(UiTheme.HeadingFont);
            _seatPanelText.AddText(SeatLabel(code));
            _seatPanelText.Pop();
            string kind = KindWords(code);
            if (kind.Length > 0)
            {
                _seatPanelText.PushColor(muted);
                _seatPanelText.AddText("    Played by: ");
                _seatPanelText.Pop();
                _seatPanelText.AddText(kind);
            }
            _seatPanelText.Newline();

            if (!_exchanges.TryGet(code, out var exchange) || exchange.LastPromptText is null)
            {
                _seatPanelText.PushColor(muted);
                _seatPanelText.AddText("    Nothing asked yet.");
                _seatPanelText.Pop();
                _seatPanelText.Newline();
                _seatPanelText.Newline();
                continue;
            }
            string asked = exchange.LastPromptIsStrikeThree
                ? Guidance.StrikeThreeWaitingPhrase
                : exchange.LastAsk is not null
                    ? Guidance.WaitingPhraseFor(exchange.LastAsk)
                    : FirstLine(exchange.LastPromptText);
            string number = exchange.LastDecisionNumber is { } n ? $"Decision {n}: " : "";
            string attempt = exchange.LastAttempt is > 1 ? $" (attempt {exchange.LastAttempt})" : "";
            _seatPanelText.AddText($"    {number}asked to {asked}{attempt}.");
            _seatPanelText.Newline();
            _seatPanelText.AddText("    ");
            if (exchange.LastReplyText is { } reply)
            {
                _seatPanelText.PushColor(muted);
                _seatPanelText.AddText("Reply: ");
                _seatPanelText.Pop();
                _seatPanelText.PushFont(UiTheme.CodeFont);
                _seatPanelText.AddText(reply);
                _seatPanelText.Pop();
            }
            else
            {
                _seatPanelText.PushColor(muted);
                _seatPanelText.AddText("Deciding…");
                _seatPanelText.Pop();
            }
            _seatPanelText.Newline();
            _seatPanelText.Newline();
        }
    }

    /// <summary>How a seat is played, where this screen knows (the host's plan, or the local seat).</summary>
    private string KindWords(string code)
    {
        if (code == _mySeat) return "you";
        return _seatPlans.FirstOrDefault(p => p.Color == code)?.Kind switch
        {
            SeatPlanKind.ThisScreen => "this screen",
            SeatPlanKind.Client => "client",
            SeatPlanKind.Agent => "agent",
            SeatPlanKind.StandIn => "stand-in",
            _ => "",
        };
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).TrimEnd('\r');
    }

    // ------------------------------------------------------------------
    // The guarded host edit: the panel, its outcome, the flag
    // ------------------------------------------------------------------

    private void RefreshEditedBadge() => _editedBadge.Visible = _hostEdited;

    /// <summary>
    /// Opens the edit panel over the position as the session's own
    /// game stands: the host door reads the engine directly (it is
    /// trusted tooling), never the feed, so the form opens on the
    /// position the session will actually edit.
    /// </summary>
    private void OpenEditPanel()
    {
        var session = _hosted?.Session;
        if (session is null) return;
        var snapshot = session.Game.Snapshot();
        var byCode = snapshot.Territories.ToDictionary(t => t.Code, StringComparer.Ordinal);
        var position = _catalog.Territories
            .Select(info => new HostEditPosition(
                info.Code, info.Name, byCode[info.Code].Owner, byCode[info.Code].Armies))
            .ToList();
        var owners = session.Config.PlayerCodes.ToList();
        if (session.Config.Mode == GameMode.Duo) owners.Add(RulesConstants.RebelCode);
        _editPanel.Open(position, owners);
    }

    /// <summary>
    /// The panel's Confirm, applied through the host library path the
    /// panel exists to front. An applied edit closes the panel and
    /// flags the table; a refusal is shown in the panel, in the
    /// session's own words, and the position stands unchanged.
    /// </summary>
    private async void OnEditConfirmed(IReadOnlyList<TerritoryEdit> edits)
    {
        var apply = _hosted?.ApplyEditAsync(edits);
        if (apply is null)
        {
            _editPanel.Close();
            return;
        }
        HostEditOutcome outcome;
        try
        {
            outcome = await apply;
        }
        catch (Exception ex)
        {
            // The session's edit task completes with an outcome by
            // design; a fault here is the table closing under the
            // panel, and the panel closes with it.
            _editPanel.Close();
            SetStatus($"The edit could not be applied: {ex.Message}");
            return;
        }
        switch (outcome)
        {
            case HostEditOutcome.Applied:
                _hostEdited = true;
                RefreshEditedBadge();
                _editPanel.Close();
                SetStatus("Position edited. The edit stands in the game record.");
                RefreshHostArea();
                break;
            case HostEditOutcome.Refused refused:
                _editPanel.ShowRefusal(refused.Reason);
                break;
        }
    }

    // ------------------------------------------------------------------
    // The host tab (this app hosts; pause/poke/replace controls + suspend)
    // ------------------------------------------------------------------

    private void BuildHostTab()
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);

        // The game.
        body.AddChild(Ui.Text("THE GAME", UiTheme.CaptionLabel, wrap: false));
        _hostGameStatus = Ui.Text("");
        body.AddChild(_hostGameStatus);
        var controls = new HFlowContainer();
        controls.AddThemeConstantOverride("h_separation", 6);
        controls.AddThemeConstantOverride("v_separation", 6);
        _pauseButton = Ui.Button("Pause", UiTheme.SmallButton, "Hold the game at the next decision boundary.");
        _pauseButton.Disabled = true;
        _pauseButton.Pressed += () => { _hosted?.PauseGame(); RefreshHostArea(); };
        controls.AddChild(_pauseButton);
        _resumeButton = Ui.Button("Resume", UiTheme.SmallButton, "Release your pause; play continues.");
        _resumeButton.Disabled = true;
        _resumeButton.Pressed += () => { _hosted?.ResumeGame(); RefreshHostArea(); };
        controls.AddChild(_resumeButton);
        _suspendButton = Ui.Button("Suspend…", UiTheme.SmallButton,
            "Close the game at the next decision boundary and save it, to resume later from the front door.");
        _suspendButton.Disabled = true;
        _suspendButton.Pressed += () => Confirm("Suspend the game?",
            "The game closes at the next decision boundary and is saved in the records folder. Resume it later " +
            "from the front door's Resume a game tab, exactly where it stopped.",
            "Suspend", () => { _hosted?.SuspendGame(); RefreshHostArea(); });
        controls.AddChild(_suspendButton);
        _endButton = Ui.Button("End the game now…", UiTheme.SmallButton, "End the game with no winner recorded.");
        _endButton.Disabled = true;
        _endButton.Pressed += () => Confirm("End the game now?",
            "The game ends at once with no winner recorded. The record and transcript keep everything played so " +
            "far. This cannot be undone.",
            "End the game", () => { _hosted?.EndGame(); RefreshHostArea(); });
        controls.AddChild(_endButton);
        var openRecords = Ui.Button("Open records folder", UiTheme.SmallButton, "Open the folder this game's record is written to.");
        openRecords.Pressed += OpenRecordsFolder;
        controls.AddChild(openRecords);
        body.AddChild(controls);

        // The guarded edit: one deliberate step off the paused
        // state — never a quick button beside the others, and gated shut
        // unless the table stands host-paused with the pause's edit
        // unspent (RefreshHostArea keeps it so).
        var editRow = new HBoxContainer();
        _editButton = Ui.Button("Edit position…", UiTheme.SmallButton,
            "Change a territory's owner or armies. Only while paused, once per pause; recorded and flagged.");
        _editButton.Disabled = true;
        _editButton.Pressed += OpenEditPanel;
        editRow.AddChild(_editButton);
        var editHint = Ui.Hint("Only while the game is paused, once per pause. The edit is written into the record.");
        editHint.VerticalAlignment = VerticalAlignment.Center;
        editRow.AddChild(editHint);
        body.AddChild(editRow);

        _substituteBox = new VBoxContainer();
        _substituteBox.AddThemeConstantOverride("separation", 4);
        body.AddChild(_substituteBox);

        // Joining instructions, for as long as seats are open.
        _joiningSection = new VBoxContainer();
        body.AddChild(_joiningSection);
        _joiningSection.AddChild(new HSeparator());
        _joiningSection.AddChild(Ui.Text("SEATS STILL TO JOIN", UiTheme.CaptionLabel, wrap: false));
        _joiningBody = new VBoxContainer();
        _joiningBody.AddThemeConstantOverride("separation", 8);
        _joiningSection.AddChild(_joiningBody);
        _joiningSection.Visible = false;

        // The seats.
        body.AddChild(new HSeparator());
        body.AddChild(Ui.Text("THE SEATS", UiTheme.CaptionLabel, wrap: false));
        _hostSeatsBody = new VBoxContainer();
        _hostSeatsBody.AddThemeConstantOverride("separation", 10);
        body.AddChild(_hostSeatsBody);

        _infoTabs.AddChild(scroll);
        int index = _infoTabs.GetTabIdxFromControl(scroll);
        _infoTabs.SetTabTitle(index, "Host");
        _infoTabs.SetTabTooltip(index, "Seat telemetry and the host's controls.");
        _hostTab = scroll;
    }

    private void Confirm(string title, string text, string action, Action confirmed)
    {
        var dialog = new ConfirmationDialog
        {
            Title = title,
            DialogText = text,
            DialogAutowrap = true,
            OkButtonText = action,
            CancelButtonText = "Cancel",
            MinSize = new Vector2I(460, 0),
        };
        dialog.Confirmed += () => confirmed();
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible) dialog.QueueFree();
        };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    private void OpenRecordsFolder()
    {
        string? folder = _result is { } result
            ? System.IO.Path.GetDirectoryName(result.RecordPath)
            : _hosted?.RecordsFolder;
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            OS.ShellOpen(folder);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Could not open the records folder {folder}: {ex.Message}");
        }
    }

    private void RefreshHostArea()
    {
        if (_hosted is null || _hostTab is null) return;
        var snapshot = _hosted.Snapshot();
        _lastSnapshot = snapshot;
        var session = _hosted.Session;

        RefreshJoining();

        // Seats.
        if (snapshot is null)
        {
            if (_hostSeatsKey != "filling")
            {
                _hostSeatsKey = "filling";
                foreach (Node child in _hostSeatsBody.GetChildren()) child.QueueFree();
                _hostSeatRows.Clear();
                _hostSeatsBody.AddChild(Ui.Hint("Seat telemetry appears when the game starts."));
            }
        }
        else
        {
            string key = string.Join(",", snapshot.Seats.Select(s => s.Code));
            if (key != _hostSeatsKey) BuildHostSeatRows(snapshot);
            foreach (var seat in snapshot.Seats)
                if (_hostSeatRows.TryGetValue(seat.Code, out var row)) UpdateHostSeatRow(row, seat);
        }

        // The game.
        string status = snapshot is null
            ? $"The table is open on port {_hosted.ListenPort}. The game starts when every seat has joined."
            : snapshot.Status switch
            {
                SessionStatus.Running => "Running.",
                SessionStatus.Paused => "Paused: a seat could not answer. See below.",
                SessionStatus.HostPaused => "Paused by you.",
                SessionStatus.Completed => "The game is over.",
                SessionStatus.EndedByHost => "Ended by the host.",
                SessionStatus.Suspended => "Suspended and saved.",
                var other => other.ToString(),
            };
        if (GameFinished && _result is { } result) status += $" Record: {result.RecordPath}";
        _hostGameStatus.Text = status;

        // The game controls stand live exactly while the session
        // would honor them: pause only in play, resume only out of
        // the host's own pause, and nothing once the game is over —
        // an ended game's buttons used to stay live-looking.
        var phase = snapshot is null
            ? HostGamePhase.Filling
            : GameFinished
                ? HostGamePhase.Over
                : snapshot.Status switch
                {
                    SessionStatus.Running => HostGamePhase.Running,
                    SessionStatus.HostPaused => HostGamePhase.HostPaused,
                    SessionStatus.Paused => HostGamePhase.StrikePaused,
                    _ => HostGamePhase.Over,
                };
        var controls = HostGameControls.For(phase);
        _pauseButton.Disabled = !controls.PauseEnabled;
        _resumeButton.Disabled = !controls.ResumeEnabled;
        _suspendButton.Disabled = !controls.SuspendEnabled;
        _endButton.Disabled = !controls.EndEnabled;

        // The host-edited flag and the edit gate follow the snapshot.
        if (snapshot is not null && snapshot.HostEdited && !_hostEdited)
        {
            _hostEdited = true;
            RefreshEditedBadge();
        }
        _editButton.Disabled = !HostEditForm.EditorAvailable(
            isHost: _door == TableDoor.Host,
            hostPaused: snapshot?.Status == SessionStatus.HostPaused,
            gameOver: GameFinished
                      || snapshot?.Status is SessionStatus.Completed
                          or SessionStatus.EndedByHost or SessionStatus.Suspended,
            editUsedThisPause: snapshot?.EditUsedThisPause ?? true);

        // The substitute list, rebuilt only when the pause it answers
        // changes: rebuilding it every poll replaced the buttons under
        // the host's pointer and could swallow a click.
        var pause = snapshot?.Status == SessionStatus.Paused ? session?.CurrentPause : null;
        string pauseKey = pause is null ? "" : $"{pause.Seat}:{pause.DecisionNumber}:{pause.LegalMoves.Count}";
        if (pauseKey != _substituteKey)
        {
            _substituteKey = pauseKey;
            foreach (Node child in _substituteBox.GetChildren()) child.QueueFree();
            if (pause is not null)
            {
                _substituteBox.AddChild(Ui.Text(
                    $"{SeatLabel(pause.Seat)} could not answer decision {pause.DecisionNumber}. {pause.Reason}",
                    UiTheme.WarningLabel));
                _substituteBox.AddChild(Ui.Hint(
                    $"Answer for {SeatLabel(pause.Seat)} this once with one of its legal moves, replace the seat below, " +
                    "or end the game. The answer is recorded as the host's."));
                for (int i = 0; i < pause.LegalMoves.Count; i++)
                {
                    int number = i + 1;
                    var button = Ui.Button($"{number}.  {pause.LegalMoves[i]}", UiTheme.SmallButton);
                    button.Alignment = HorizontalAlignment.Left;
                    button.Pressed += () => { _hosted.Substitute(number); RefreshHostArea(); };
                    _substituteBox.AddChild(button);
                }
            }
        }
        if (pauseKey.Length > 0 && pauseKey != _alertedPauseKey)
        {
            // A strike-pause needs the host: bring the Host tab forward, once per pause.
            _alertedPauseKey = pauseKey;
            _infoTabs.CurrentTab = _infoTabs.GetTabIdxFromControl(_hostTab);
        }

        RefreshBanner();
    }

    private void BuildHostSeatRows(SessionSnapshot snapshot)
    {
        _hostSeatsKey = string.Join(",", snapshot.Seats.Select(s => s.Code));
        foreach (Node child in _hostSeatsBody.GetChildren()) child.QueueFree();
        _hostSeatRows.Clear();
        foreach (var seat in snapshot.Seats)
        {
            string code = seat.Code;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 3);

            var top = new HBoxContainer();
            top.AddChild(Ui.Swatch(UiConstants.SeatColor(code)));
            var name = new Label { Text = SeatLabel(code), ThemeTypeVariation = UiTheme.SubheadingLabel };
            top.AddChild(name);
            box.AddChild(top);

            // The seat's telemetry, each value under its own caption.
            var facts = FactField.Row(16);
            var kind = new FactField("Played by", "Who answers this seat's prompts.", small: true);
            var state = new FactField("Connection",
                "How the seat reaches the table. STALLED: slower than the stall warning on its pending decision.", small: true);
            var deciding = new FactField("Deciding", "The decision the table is waiting on this seat for, if any.", small: true);
            var strikes = new FactField("Strikes",
                "Replies to the pending decision the table could not accept. The third prompt lists the legal moves; " +
                "after that the game pauses for the host.", small: true);
            var lastReply = new FactField("Last reply", "How long ago this seat last answered.", small: true);
            foreach (var fact in new[] { kind, state, deciding, strikes, lastReply }) facts.AddChild(fact);
            box.AddChild(facts);

            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 6);
            buttons.AddThemeConstantOverride("v_separation", 4);
            var poke = Ui.Button("Poke", UiTheme.SmallButton, "Send this seat its pending prompt again, unchanged.");
            poke.Pressed += () => _hosted?.Poke(code);
            buttons.AddChild(poke);
            var standIn = Ui.Button("Replace with stand-in", UiTheme.SmallButton,
                "The built-in player takes this seat over, starting with its pending decision.");
            standIn.Pressed += () => { _hosted?.ReplaceSeatWithStandIn(code); RefreshHostArea(); };
            buttons.AddChild(standIn);
            var release = Ui.Button("Release to an agent", UiTheme.SmallButton,
                $"Reopen this seat for a new connection, which claims it with JOIN {code}.");
            release.Pressed += () => { _hosted?.ReleaseSeatToAgent(code); RefreshHostArea(); };
            buttons.AddChild(release);
            box.AddChild(buttons);

            _hostSeatsBody.AddChild(box);
            _hostSeatRows[code] = new HostSeatRow(name, kind, state, deciding, strikes, lastReply, poke, standIn, release);
        }
    }

    private void UpdateHostSeatRow(HostSeatRow row, SeatSnapshot seat)
    {
        row.Kind.Set(seat.Kind == SeatKind.Scripted
            ? "stand-in"
            : _seatPlans.FirstOrDefault(p => p.Color == seat.Code)?.Kind switch
            {
                SeatPlanKind.ThisScreen => "this screen",
                SeatPlanKind.Client => "client",
                _ => "agent",
            });
        if (seat.Stalled)
        {
            row.State.Set("STALLED");
            row.State.SetValueColor(UiConstants.Chrome.Warning);
        }
        else
        {
            row.State.Set(seat.Connection switch
            {
                SeatConnection.InProcess => "in this app",
                SeatConnection.Connected => "connected",
                SeatConnection.Disconnected => "disconnected",
                SeatConnection.AwaitingJoin => "waiting to join",
                var other => other.ToString(),
            });
            row.State.SetValueColor(seat.Connection == SeatConnection.Disconnected ? UiConstants.Chrome.Warning : null);
        }
        row.Deciding.Set(seat.PendingDecisionNumber is { } n
            ? $"decision {n} ({DecisionWords(seat.PendingDecisionKind)})"
            : "nothing");
        // Strikes count against the pending decision; with none pending there is nothing to count.
        row.Strikes.Set(seat.PendingDecisionNumber is null ? "" : seat.StrikesOnCurrentDecision.ToString());
        row.Strikes.SetValueColor(seat.StrikesOnCurrentDecision > 0 ? UiConstants.Chrome.Warning : null);
        row.LastReply.Set(seat.SinceLastReply is { } span ? $"{(int)span.TotalSeconds} s ago" : "none yet");

        bool live = !GameFinished;
        row.Poke.Disabled = !live || seat.PendingDecisionNumber is null;
        row.StandIn.Disabled = !live || seat.Kind == SeatKind.Scripted;
        row.Release.Disabled = !live;
    }

    private static string DecisionWords(DecisionKind? kind) => kind switch
    {
        DecisionKind.Claim => "claim",
        DecisionKind.ClaimForRebels => "claim for the Rebels",
        DecisionKind.SetupPlace => "setup placement",
        DecisionKind.Trade => "card trade",
        DecisionKind.Place => "reinforcements",
        DecisionKind.AttackDeclare => "attack",
        DecisionKind.AttackDice => "attack dice",
        DecisionKind.DefendDice => "defence dice",
        DecisionKind.CaptureMove => "move in",
        DecisionKind.Fortify => "fortify",
        null => "decision",
        var other => other.ToString()!,
    };

    /// <summary>
    /// The joining words for every seat still open, kept on the table so
    /// the host can send them after starting (and for a seat released to
    /// an agent mid-game). Rebuilt only when the open seats change.
    /// </summary>
    private void RefreshJoining()
    {
        var open = _hosted!.OpenSeats.OrderBy(code => code, StringComparer.Ordinal).ToList();
        string key = string.Join(",", open);
        if (key == _joiningKey) return;
        _joiningKey = key;
        _joiningSection.Visible = open.Count > 0;
        foreach (Node child in _joiningBody.GetChildren()) child.QueueFree();
        foreach (string code in open)
        {
            var plan = _seatPlans.FirstOrDefault(p => p.Color == code) ?? new SeatPlan(code, SeatPlanKind.Agent);
            bool client = plan.Kind == SeatPlanKind.Client;
            var words = client
                ? AgentInstructions.ForClientSeat(_connectAddress, _hosted.ListenPort, plan)
                : AgentInstructions.ForAgentSeat(_connectAddress, _hosted.ListenPort,
                    new SeatPlan(code, SeatPlanKind.Agent) { Name = plan.Name });
            var header = new HBoxContainer();
            header.AddChild(Ui.Swatch(UiConstants.SeatColor(code)));
            header.AddChild(new Label { Text = SeatLabel(code), ThemeTypeVariation = UiTheme.SubheadingLabel });
            var joinsAs = new FactField("Joins as", "Who these words are for: an agent's program, or a person with a copy of this app.", small: true);
            joinsAs.Set(client ? "client" : "agent");
            header.AddChild(joinsAs);
            header.AddChild(Ui.Spacer());
            var copy = Ui.Button("Copy", UiTheme.SmallButton, "Copy these words to the clipboard.");
            copy.Pressed += () => DisplayServer.ClipboardSet(words);
            header.AddChild(copy);
            _joiningBody.AddChild(header);
            if (client)
                _joiningBody.AddChild(Ui.Hint(words));
            else
                _joiningBody.AddChild(new TextEdit
                {
                    Text = words,
                    Editable = false,
                    WrapMode = TextEdit.LineWrappingMode.Boundary,
                    CustomMinimumSize = new Vector2(0, 150),
                });
        }
    }
}
