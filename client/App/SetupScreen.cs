using System;
using System.Collections.Generic;
using System.Linq;
using AgentRisk.Client.Protocol;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>What the host chose for one suspended game: the game, and how each seat is driven from here.</summary>
public sealed record ResumeRequest(
    SuspendedGame Game,
    IReadOnlyList<SeatPlan> SeatChoices,
    string Address,
    int Port,
    int StallWarningSeconds);

/// <summary>The front door's tabs, in the order they stand.</summary>
public enum SetupDoor
{
    Overview,
    Host,
    Resume,
    Join,
    Watch,
}

/// <summary>
/// The front door. It opens on an overview that says what the software
/// is, what it records, and how a game is played, then offers the four
/// ways in: host a game, resume a suspended one, join one, or watch one.
/// Every form field carries a line saying what it does, so a first-time
/// host can set a table up without the manual, and the host door shows
/// what is missing before the table can start rather than after.
///
/// The screen owns the forms and their validation only; starting
/// anything is the shell's work, reported back through the status lines
/// when a door refuses (a taken seat, an unresumable game). On the host
/// door, the per-seat joining instructions rebuild live as the form
/// changes, so what the host copies always names the address, port, and
/// seat the form currently holds.
/// </summary>
public partial class SetupScreen : PanelContainer
{
    private static readonly string[] KindLabels = { "This screen", "Client", "Agent", "Stand-in" };

    private readonly TableSetup _setup = TableSetup.Fresh();
    private TabContainer _tabs = null!;

    // Host door
    private OptionButton _modePicker = null!;
    private Label _modeHint = null!;
    private VBoxContainer _seatRows = null!;
    private Button _addSeatButton = null!;
    private Label _seatCountHint = null!;
    private LineEdit _addressEdit = null!;
    private LineEdit _connectAddressEdit = null!;
    private SpinBox _portSpin = null!;
    private LineEdit _seedEdit = null!;
    private OptionButton _territoryPicker = null!;
    private LineEdit _recordsEdit = null!;
    private SpinBox _stallSpin = null!;
    private Control _advancedRow = null!;
    private Label _readySummary = null!;
    private Label _readyProblems = null!;
    private Label _readyNext = null!;
    private Button _startButton = null!;
    private Label _hostStatus = null!;
    private VBoxContainer _instructions = null!;

    // Resume door
    private Label _resumeFolder = null!;
    private ItemList _resumeList = null!;
    private Label _resumeStatus = null!;
    private Label _resumeListHint = null!;
    private VBoxContainer _resumeSeatRows = null!;
    private Label _resumeConnection = null!;
    private Button _resumeButton = null!;
    private IReadOnlyList<SuspendedGame> _suspended = Array.Empty<SuspendedGame>();
    private readonly List<SeatPlan> _resumeChoices = new();

    // Join door
    private LineEdit _joinAddressEdit = null!;
    private SpinBox _joinPortSpin = null!;
    private OptionButton _joinSeatPicker = null!;
    private LineEdit _joinNameEdit = null!;
    private Button _joinButton = null!;
    private Label _joinStatus = null!;

    // Watch door
    private LineEdit _watchAddressEdit = null!;
    private SpinBox _watchPortSpin = null!;
    private Button _watchButton = null!;
    private Label _watchStatus = null!;

    public event Action<TableSetup>? HostStartRequested;
    public event Action<ResumeRequest>? ResumeRequested;
    public event Action<string, int, string, string>? JoinRequested;
    public event Action<string, int>? WatchRequested;

    /// <summary>The records folder the forms open with: the app's own game-records folder.</summary>
    public string DefaultRecordsFolder { get; set; } = "";

    public override void _Ready()
    {
        _setup.RecordsFolder = DefaultRecordsFolder;
        BuildDoors();
        RefreshSeatRows();
        RefreshInstructionBlocks();
        RefreshResumeList();
        RefreshReadiness();
    }

    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    private void BuildDoors()
    {
        var margin = Ui.Margins(28, 18, 28, 16);
        AddChild(margin);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 16);
        var titles = new VBoxContainer();
        titles.AddThemeConstantOverride("separation", 0);
        titles.AddChild(Ui.Text("Agent Risk", UiTheme.TitleLabel, wrap: false));
        titles.AddChild(Ui.Text(
            "A turn-based strategy benchmark for AI agents, on the classic world-conquest board.",
            UiTheme.HintLabel, wrap: false));
        header.AddChild(titles);
        root.AddChild(header);

        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(_tabs);
        _tabs.AddChild(BuildOverviewDoor());
        _tabs.AddChild(BuildHostDoor());
        _tabs.AddChild(BuildResumeDoor());
        _tabs.AddChild(BuildJoinDoor());
        _tabs.AddChild(BuildWatchDoor());
        _tabs.SetTabTitle((int)SetupDoor.Overview, "Overview");
        _tabs.SetTabTitle((int)SetupDoor.Host, "Host a game");
        _tabs.SetTabTitle((int)SetupDoor.Resume, "Resume a game");
        _tabs.SetTabTitle((int)SetupDoor.Join, "Join a game");
        _tabs.SetTabTitle((int)SetupDoor.Watch, "Watch a game");
        _tabs.TabChanged += tab =>
        {
            if (tab == (int)SetupDoor.Resume) RefreshResumeList();
        };

        root.AddChild(Ui.Text(
            "Agent Risk is an unofficial project. It is not affiliated with, endorsed by, or connected to Hasbro. " +
            "Risk is a trademark of Hasbro.",
            UiTheme.CaptionLabel));
    }

    /// <summary>A scrolling page with two columns; returns the page and both columns.</summary>
    private static (ScrollContainer Page, VBoxContainer Left, VBoxContainer Right) TwoColumnPage(
        float leftRatio = 1.2f, float rightRatio = 1f)
    {
        var page = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 18);
        page.AddChild(columns);
        var left = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = leftRatio,
        };
        left.AddThemeConstantOverride("separation", 14);
        var right = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = rightRatio,
        };
        right.AddThemeConstantOverride("separation", 14);
        columns.AddChild(left);
        columns.AddChild(right);
        return (page, left, right);
    }

    // ------------------------------------------------------------------
    // Overview: what this is, what it records, the ways in
    // ------------------------------------------------------------------

    private Control BuildOverviewDoor()
    {
        var (page, left, right) = TwoColumnPage(1.15f, 1f);

        var (what, whatBody) = Ui.Card("What Agent Risk is");
        whatBody.AddChild(Ui.Text(
            "Agent Risk plays complete games of classic world conquest (42 territories, six continents, " +
            "territory cards, world domination as the only way to win) with AI agents and people at the " +
            "same table. Every seat, human or agent, receives the same text prompt for each decision and " +
            "answers with one line."));
        whatBody.AddChild(Ui.Hint(
            "Duo, the two-player game, ends as soon as one of the two players is eliminated; the neutral " +
            "Rebels never have to be cleared."));
        whatBody.AddChild(Ui.Subheading("Each prompt holds"));
        whatBody.AddChild(Ui.Bullets(
            "a summary of everything that happened since the seat's last decision, every die included;",
            "the full board: every territory's owner and armies, and every player's public counts;",
            "the seat's own cards; and",
            "the one decision being asked, with the exact reply it accepts."));
        whatBody.AddChild(Ui.Subheading("What every game produces"));
        whatBody.AddChild(Ui.Bullets(
            "A game record: every event, decision and die roll, which can be replayed and checked.",
            "A transcript: every prompt and reply, word for word, for every seat.",
            "A seed. The same seed with the same replies replays the same game exactly.",
            "Every host intervention (pause, poke, replacement, position edit), marked in the record."));
        left.AddChild(what);

        var (flow, flowBody) = Ui.Card("How a decision works");
        flowBody.AddChild(Ui.Steps(
            "The host sends the seat whose decision it is a prompt.",
            "The seat replies with one line in the grammar the prompt shows. A person answers with the map and " +
            "the buttons; the app writes the line.",
            "An illegal reply is sent back with the reason. The third attempt lists the legal moves by number.",
            "If the seat still cannot answer, or its connection drops, the game pauses for the host. " +
            "The host never makes a move for a seat on its own."));
        flowBody.AddChild(Ui.Hint(
            "The table shows only what the rules make visible: no hints, odds or suggested moves, for people " +
            "or for agents."));
        left.AddChild(flow);

        var (kinds, kindsBody) = Ui.Card("Who can sit at a table");
        kindsBody.AddChild(KindLine("This screen", "You, playing in this window."));
        kindsBody.AddChild(KindLine("Client", "A person playing in this app on another computer."));
        kindsBody.AddChild(KindLine("Agent", "An AI agent connected over the network, playing in text."));
        kindsBody.AddChild(KindLine("Stand-in", "A built-in scripted player that fills a seat and plays to win."));
        kindsBody.AddChild(Ui.Hint(
            "Standard games seat three to six players. Two players play Duo, where a neutral Rebel army that " +
            "the game itself plays holds part of the board."));
        left.AddChild(kinds);

        var (models, modelsBody) = Ui.Card("For training and evaluating models");
        modelsBody.AddChild(Ui.Bullets(
            "An agent is a TCP connection that speaks a line-based text protocol: a one-time briefing with the " +
            "board and rules, then one prompt per decision, each stating the exact reply it accepts.",
            "Any model or framework can play; no SDK is needed.",
            "Built-in stand-in players fill seats for practice games and fixed opponents.",
            "Spectators can follow any table live, and a suspended game resumes exactly where it stopped."));

        var (start, startBody) = Ui.Card("Get started", "Choose what you want to do. Each tab explains its own steps.");
        startBody.AddChild(DoorEntry("Host a game",
            "Run a table on this computer. Choose who plays each colour: you, other people, AI agents, or " +
            "built-in stand-ins. Agents get ready-to-paste joining instructions.",
            "Set up a table", SetupDoor.Host));
        startBody.AddChild(DoorEntry("Resume a game",
            "Continue a game that was suspended, from its record, exactly where it stopped.",
            "Find suspended games", SetupDoor.Resume));
        startBody.AddChild(DoorEntry("Join a game",
            "Play one seat at a table hosted on another computer. You need the host's address, the port, " +
            "and your seat's colour.",
            "Join a table", SetupDoor.Join));
        startBody.AddChild(DoorEntry("Watch a game",
            "Follow a table as it plays, without a seat: the board, every seat's prompts and replies, and the " +
            "record of play.",
            "Watch a table", SetupDoor.Watch));
        right.AddChild(start);
        right.AddChild(models);

        return page;
    }

    private Control DoorEntry(string title, string text, string action, SetupDoor door)
    {
        var entry = new HBoxContainer();
        entry.AddThemeConstantOverride("separation", 14);
        var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        words.AddThemeConstantOverride("separation", 2);
        words.AddChild(Ui.Subheading(title));
        words.AddChild(Ui.Hint(text));
        entry.AddChild(words);
        var button = Ui.Button(action);
        button.CustomMinimumSize = new Vector2(170, 0);
        button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        button.Pressed += () => ShowDoor(door);
        entry.AddChild(button);
        return entry;
    }

    private static Control KindLine(string kind, string meaning)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label
        {
            Text = kind,
            ThemeTypeVariation = UiTheme.SubheadingLabel,
            CustomMinimumSize = new Vector2(110, 0),
        });
        row.AddChild(Ui.Text(meaning));
        return row;
    }

    // ------------------------------------------------------------------
    // Host a game
    // ------------------------------------------------------------------

    private Control BuildHostDoor()
    {
        var (page, left, right) = TwoColumnPage(1.25f, 1f);

        // 1. The game.
        var (game, gameBody) = Ui.Card("1. The game");
        _modePicker = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
        _modePicker.AddItem("Standard");
        _modePicker.AddItem("Duo");
        _modePicker.ItemSelected += index =>
        {
            _setup.SetMode(index == 1 ? TableMode.Duo : TableMode.Standard);
            RefreshModeHint();
            RefreshSeatRows();
            RefreshInstructionBlocks();
            RefreshReadiness();
        };
        var (modeRow, modeHint) = Ui.Field("Game type", _modePicker, "");
        _modeHint = modeHint!;
        gameBody.AddChild(modeRow);

        _territoryPicker = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
        _territoryPicker.AddItem("Claimed in turn");
        _territoryPicker.AddItem("Dealt at random");
        _territoryPicker.ItemSelected += index =>
            _setup.Territories = index == 1 ? SetupTerritories.RandomDeal : SetupTerritories.ManualClaims;
        gameBody.AddChild(Ui.Field("Territories", _territoryPicker,
            "Claimed: the players take turns choosing territories before placing their starting armies. " +
            "Dealt: all 42 territories are dealt at random first.").Row);

        _seedEdit = new LineEdit
        {
            PlaceholderText = "Blank: picked when the table starts",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _seedEdit.TextChanged += _ => RefreshReadiness();
        gameBody.AddChild(Ui.Field("Seed", _seedEdit,
            "The seed fixes the dice, the card deck and any deal. Leave it blank for a random seed; the table " +
            "shows the one it used. The same seed with the same replies replays the same game.").Row);
        left.AddChild(game);

        // 2. The seats.
        var (seats, seatsBody) = Ui.Card("2. The seats",
            "Who plays each colour. Turn order runs down this list.");
        var captions = new HBoxContainer();
        captions.AddThemeConstantOverride("separation", 8);
        captions.AddChild(Caption("COLOUR", 150));
        captions.AddChild(Caption("PLAYED BY", 160));
        captions.AddChild(Caption("NAME (OPTIONAL)", 0));
        seatsBody.AddChild(captions);
        _seatRows = new VBoxContainer();
        _seatRows.AddThemeConstantOverride("separation", 6);
        seatsBody.AddChild(_seatRows);
        var addRow = new HBoxContainer();
        _addSeatButton = Ui.Button("Add a seat");
        _addSeatButton.Pressed += () =>
        {
            if (_setup.AddSeat())
            {
                RefreshSeatRows();
                RefreshInstructionBlocks();
                RefreshReadiness();
            }
        };
        addRow.AddChild(_addSeatButton);
        _seatCountHint = Ui.Hint("");
        _seatCountHint.VerticalAlignment = VerticalAlignment.Center;
        addRow.AddChild(_seatCountHint);
        seatsBody.AddChild(addRow);
        seatsBody.AddChild(Ui.Hint(
            "This screen: you, in this window (one seat at most). Client: a person in this app on another " +
            "computer. Agent: an AI agent connecting over the network. Stand-in: the built-in scripted player."));
        left.AddChild(seats);

        // 3. Connection.
        var (connection, connectionBody) = Ui.Card("3. Connection",
            "Agent and Client seats connect to this computer over the network.");
        _portSpin = PortSpin(_setup.Port);
        _portSpin.ValueChanged += _ =>
        {
            RefreshInstructionBlocks();
            RefreshReadiness();
        };
        connectionBody.AddChild(Ui.Field("Port", _portSpin,
            "The port the table listens on. The default is 4000.").Row);
        _connectAddressEdit = new LineEdit
        {
            Text = _setup.ConnectAddress,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _connectAddressEdit.TextChanged += _ =>
        {
            RefreshInstructionBlocks();
            RefreshReadiness();
        };
        connectionBody.AddChild(Ui.Field("Players connect to", _connectAddressEdit,
            "This computer's address as the other players reach it; the joining instructions quote it. It is " +
            "filled in from this computer's network settings. Change it if players reach you another way " +
            "(a VPN, or a forwarded internet address).").Row);

        var advancedToggle = new CheckBox { Text = "Show advanced network settings" };
        connectionBody.AddChild(advancedToggle);
        _addressEdit = new LineEdit { Text = _setup.Address, CustomMinimumSize = new Vector2(180, 0) };
        _addressEdit.TextChanged += _ => RefreshReadiness();
        _advancedRow = Ui.Field("Listen address", _addressEdit,
            "Which of this computer's network addresses accepts connections. 0.0.0.0 accepts them on every " +
            "address; 127.0.0.1 only from this computer.").Row;
        _advancedRow.Visible = false;
        advancedToggle.Toggled += on => _advancedRow.Visible = on;
        connectionBody.AddChild(_advancedRow);
        left.AddChild(connection);

        // 4. Records and monitoring.
        var (records, recordsBody) = Ui.Card("4. Records and monitoring");
        var folderRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _recordsEdit = new LineEdit
        {
            Text = _setup.RecordsFolder,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _recordsEdit.TextChanged += text =>
        {
            _setup.RecordsFolder = text;
            RefreshResumeList();
            RefreshReadiness();
        };
        folderRow.AddChild(_recordsEdit);
        var openFolder = Ui.Button("Open", UiTheme.SmallButton, "Open the records folder in the file browser.");
        openFolder.Pressed += () => OpenFolder(_recordsEdit.Text.Trim());
        folderRow.AddChild(openFolder);
        recordsBody.AddChild(Ui.Field("Records folder", folderRow,
            "Each game writes two files here: the record (every event and die roll, replayable) and the " +
            "transcript (every prompt and reply, word for word). Suspended games are resumed from here.").Row);

        var stallRow = new HBoxContainer();
        _stallSpin = new SpinBox
        {
            MinValue = 1, MaxValue = 3600, Value = _setup.StallWarningSeconds, Step = 5,
            CustomMinimumSize = new Vector2(110, 0),
        };
        _stallSpin.ValueChanged += _ => RefreshReadiness();
        stallRow.AddChild(_stallSpin);
        stallRow.AddChild(new Label { Text = "seconds", VerticalAlignment = VerticalAlignment.Center });
        recordsBody.AddChild(Ui.Field("Stall warning", stallRow,
            "A seat that takes longer than this to answer is flagged as stalled in the host controls, so you " +
            "can poke or replace it. Nothing happens to the seat by itself.").Row);
        left.AddChild(records);

        // Right column: readiness and the start, then the joining words.
        var (ready, readyBody) = Ui.Card("Start the table");
        _readySummary = Ui.Text("");
        readyBody.AddChild(_readySummary);
        _readyProblems = Ui.Text("", UiTheme.WarningLabel);
        readyBody.AddChild(_readyProblems);
        _startButton = Ui.Button("Start the table", UiTheme.PrimaryButton);
        _startButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _startButton.Pressed += OnStartPressed;
        readyBody.AddChild(_startButton);
        _hostStatus = Ui.Text("", UiTheme.WarningLabel);
        _hostStatus.Visible = false;
        readyBody.AddChild(_hostStatus);
        _readyNext = Ui.Hint("");
        readyBody.AddChild(_readyNext);
        right.AddChild(ready);

        var (joining, joiningBody) = Ui.Card("Joining instructions",
            "Send each remote player their words. An Agent seat gets a complete instruction block to paste into " +
            "the agent's prompt or harness as it stands; a Client seat gets one line to pass on. Both update as " +
            "you edit the form.");
        _instructions = new VBoxContainer();
        _instructions.AddThemeConstantOverride("separation", 10);
        joiningBody.AddChild(_instructions);
        right.AddChild(joining);

        RefreshModeHint();
        return page;
    }

    private static Label Caption(string text, float width) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.CaptionLabel,
        CustomMinimumSize = new Vector2(width, 0),
    };

    private void RefreshModeHint()
    {
        if (_modeHint is null) return;
        _modeHint.Text = _setup.Mode == TableMode.Duo
            ? "Duo: exactly two seats, plus the Rebels, a neutral army the game itself plays. The Rebels are not " +
              "a seat. The game ends when one of the two players is eliminated."
            : "Standard: three to six seats.";
    }

    // ------------------------------------------------------------------
    // Resume a game
    // ------------------------------------------------------------------

    private Control BuildResumeDoor()
    {
        var (page, left, right) = TwoColumnPage(1.2f, 1f);

        var (list, listBody) = Ui.Card("Suspended games",
            "A game suspended from the host controls is saved in the records folder. Resuming it replays its " +
            "recorded decisions, checks them, and continues live from where it stopped, in the same record.");
        _resumeFolder = Ui.Hint("");
        listBody.AddChild(_resumeFolder);
        var buttons = new HBoxContainer();
        var refresh = Ui.Button("Look again", UiTheme.SmallButton, "Scan the records folder again.");
        refresh.Pressed += RefreshResumeList;
        buttons.AddChild(refresh);
        var open = Ui.Button("Open folder", UiTheme.SmallButton, "Open the records folder in the file browser.");
        open.Pressed += () => OpenFolder(_setup.RecordsFolder);
        buttons.AddChild(open);
        listBody.AddChild(buttons);
        _resumeList = new ItemList
        {
            CustomMinimumSize = new Vector2(0, 200),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _resumeList.ItemSelected += _ => RefreshResumeSeatRows();
        listBody.AddChild(_resumeList);
        _resumeListHint = Ui.Hint("");
        listBody.AddChild(_resumeListHint);
        left.AddChild(list);

        var (seatsCard, seatsBody) = Ui.Card("Who plays each seat from here",
            "A seat may come back differently from how it was recorded: on this screen, as a Client or Agent " +
            "connection, or as a stand-in.");
        _resumeSeatRows = new VBoxContainer();
        _resumeSeatRows.AddThemeConstantOverride("separation", 6);
        seatsBody.AddChild(_resumeSeatRows);
        _resumeConnection = Ui.Hint("");
        seatsBody.AddChild(_resumeConnection);
        _resumeButton = Ui.Button("Resume this game", UiTheme.PrimaryButton);
        _resumeButton.Disabled = true;
        _resumeButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _resumeButton.Pressed += OnResumePressed;
        seatsBody.AddChild(_resumeButton);
        _resumeStatus = Ui.Text("", UiTheme.WarningLabel);
        _resumeStatus.Visible = false;
        seatsBody.AddChild(_resumeStatus);
        right.AddChild(seatsCard);

        return page;
    }

    // ------------------------------------------------------------------
    // Join a game
    // ------------------------------------------------------------------

    private Control BuildJoinDoor()
    {
        var (page, left, right) = TwoColumnPage(1.2f, 1f);

        var (form, body) = Ui.Card("Join a game",
            "Play one seat at a table hosted on another computer. Ask the host for their address, the port, " +
            "and which colour is yours.");
        _joinAddressEdit = new LineEdit { Text = UiConstants.DefaultHost, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(Ui.Field("Host address", _joinAddressEdit).Row);
        _joinPortSpin = PortSpin(UiConstants.DefaultPort);
        body.AddChild(Ui.Field("Port", _joinPortSpin).Row);
        _joinSeatPicker = ColorPicker();
        body.AddChild(Ui.Field("Your seat", _joinSeatPicker,
            "The colour the host gave you. If another player already holds it, the host refuses and you can " +
            "pick again.").Row);
        _joinNameEdit = new LineEdit { PlaceholderText = "Optional", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(Ui.Field("Your name", _joinNameEdit,
            "Shown beside your colour on your own screen. The table knows your seat by its colour.").Row);
        _joinButton = Ui.Button("Join the game", UiTheme.PrimaryButton);
        _joinButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _joinButton.Pressed += () => JoinRequested?.Invoke(
            _joinAddressEdit.Text.Trim(),
            (int)_joinPortSpin.Value,
            TableSetup.SeatColors[_joinSeatPicker.Selected],
            _joinNameEdit.Text.Trim());
        body.AddChild(_joinButton);
        _joinStatus = Ui.Text("", UiTheme.WarningLabel);
        _joinStatus.Visible = false;
        body.AddChild(_joinStatus);
        left.AddChild(form);

        var (next, nextBody) = Ui.Card("What happens next");
        nextBody.AddChild(Ui.Steps(
            "The host accepts your seat and the table opens.",
            "The game starts when every seat has joined.",
            "When a decision is yours, the banner across the top says what to do, the board marks the " +
            "territories you can pick, and the controls appear on the right. Click the map and the buttons; " +
            "the app sends your one-line reply.",
            "Everything else (each roll, each move, each seat's last prompt and reply) shows in the panels " +
            "beside the map."));
        nextBody.AddChild(Ui.Hint(
            "Your screen also opens a watch connection, which shows every seat's prompts and replies. If it " +
            "cannot open, the game still plays and the panels show your own seat."));
        right.AddChild(next);
        return page;
    }

    // ------------------------------------------------------------------
    // Watch a game
    // ------------------------------------------------------------------

    private Control BuildWatchDoor()
    {
        var (page, left, right) = TwoColumnPage(1.2f, 1f);

        var (form, body) = Ui.Card("Watch a game",
            "Follow a table as it plays, without a seat: the board, every seat's prompts and replies, the dice, " +
            "and the record of play. Watchers make no decisions, and can join at any point in the game.");
        _watchAddressEdit = new LineEdit { Text = UiConstants.DefaultHost, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(Ui.Field("Host address", _watchAddressEdit).Row);
        _watchPortSpin = PortSpin(UiConstants.DefaultPort);
        body.AddChild(Ui.Field("Port", _watchPortSpin).Row);
        _watchButton = Ui.Button("Watch the game", UiTheme.PrimaryButton);
        _watchButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _watchButton.Pressed += () => WatchRequested?.Invoke(
            _watchAddressEdit.Text.Trim(), (int)_watchPortSpin.Value);
        body.AddChild(_watchButton);
        _watchStatus = Ui.Text("", UiTheme.WarningLabel);
        _watchStatus.Visible = false;
        body.AddChild(_watchStatus);
        left.AddChild(form);

        var (note, noteBody) = Ui.Card("Before you watch");
        noteBody.AddChild(Ui.Text(
            "A watcher sees every seat's cards, which the players themselves cannot. Hosts should share the " +
            "address only with people they trust with that."));
        noteBody.AddChild(Ui.Hint(
            "Agents and scripts can watch too: send the line JOIN WATCH after the host's greeting and read the " +
            "live transcript."));
        right.AddChild(note);
        return page;
    }

    private static SpinBox PortSpin(int value) => new()
    {
        MinValue = 1, MaxValue = 65535, Value = value, Step = 1,
        CustomMinimumSize = new Vector2(120, 0),
    };

    private static OptionButton ColorPicker()
    {
        var picker = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
        foreach (string color in TableSetup.SeatColors)
            picker.AddIconItem(Ui.SwatchIcon(color), UiConstants.SeatDisplayName(color));
        return picker;
    }

    private static void OpenFolder(string folder)
    {
        if (folder.Length == 0) return;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            OS.ShellOpen(folder);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Could not open the folder {folder}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Seat rows and instruction blocks (host door, rebuilt live)
    // ------------------------------------------------------------------

    private void RefreshSeatRows()
    {
        foreach (Node child in _seatRows.GetChildren()) child.QueueFree();
        for (int i = 0; i < _setup.Seats.Count; i++)
        {
            int index = i;
            var seat = _setup.Seats[i];
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);

            var colorPicker = ColorPicker();
            colorPicker.Selected = Array.IndexOf(TableSetup.SeatColors, seat.Color);
            colorPicker.ItemSelected += picked =>
            {
                _setup.SetSeatColor(index, TableSetup.SeatColors[picked]);
                RefreshSeatRows();
                RefreshInstructionBlocks();
                RefreshReadiness();
            };
            row.AddChild(colorPicker);

            var kindPicker = new OptionButton { CustomMinimumSize = new Vector2(160, 0) };
            foreach (string label in KindLabels) kindPicker.AddItem(label);
            kindPicker.Selected = (int)seat.Kind;
            kindPicker.ItemSelected += picked =>
            {
                _setup.SetSeatKind(index, (SeatPlanKind)(int)picked);
                RefreshSeatRows();
                RefreshInstructionBlocks();
                RefreshReadiness();
            };
            row.AddChild(kindPicker);

            var nameEdit = new LineEdit
            {
                Text = seat.Name,
                PlaceholderText = "Name",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            nameEdit.TextChanged += text =>
            {
                _setup.Seats[index].Name = text;
                RefreshInstructionBlocks();
                RefreshReadiness();
            };
            row.AddChild(nameEdit);

            var remove = Ui.Button("Remove", UiTheme.SmallButton, "Remove this seat from the table.");
            remove.Disabled = _setup.Seats.Count <= TableSetup.MinSeats;
            remove.Pressed += () =>
            {
                if (_setup.RemoveSeat(index))
                {
                    RefreshSeatRows();
                    RefreshInstructionBlocks();
                    RefreshReadiness();
                }
            };
            row.AddChild(remove);
            _seatRows.AddChild(row);
        }
        if (_addSeatButton is not null)
        {
            int cap = _setup.Mode == TableMode.Duo ? TableSetup.DuoSeatCount : TableSetup.MaxSeats;
            _addSeatButton.Disabled = _setup.Seats.Count >= cap;
            _seatCountHint.Text = _setup.Mode == TableMode.Duo
                ? "Duo seats exactly two."
                : $"{_setup.Seats.Count} of at most {TableSetup.MaxSeats} seats.";
        }
    }

    private void RefreshInstructionBlocks()
    {
        if (_instructions is null) return;
        foreach (Node child in _instructions.GetChildren()) child.QueueFree();

        // The words a player follows must name the address they can
        // actually dial: the connect address, never the listen address.
        string address = _connectAddressEdit?.Text.Trim() ?? _setup.ConnectAddress;
        if (address.Length == 0) address = _setup.ConnectAddress;
        int port = _portSpin is null ? _setup.Port : (int)_portSpin.Value;

        foreach (var seat in _setup.Seats)
        {
            if (seat.Kind == SeatPlanKind.Agent)
            {
                string block = AgentInstructions.ForAgentSeat(address, port, seat);
                _instructions.AddChild(InstructionHeader(seat, "Agent", block));
                _instructions.AddChild(new TextEdit
                {
                    Text = block,
                    Editable = false,
                    WrapMode = TextEdit.LineWrappingMode.Boundary,
                    CustomMinimumSize = new Vector2(0, 200),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                });
            }
            else if (seat.Kind == SeatPlanKind.Client)
            {
                string line = AgentInstructions.ForClientSeat(address, port, seat);
                _instructions.AddChild(InstructionHeader(seat, "Client", line));
                _instructions.AddChild(Ui.Text(line));
            }
        }

        if (_instructions.GetChildCount() == 0)
            _instructions.AddChild(Ui.Hint(
                "No seat needs joining instructions yet: every seat is played on this screen or by a stand-in. " +
                "Set a seat to Agent or Client and its words appear here."));
    }

    private static Control InstructionHeader(SeatPlan seat, string kind, string text)
    {
        var header = new HBoxContainer();
        header.AddChild(Ui.Swatch(UiConstants.SeatColor(seat.Color)));
        string name = seat.Name.Trim().Length > 0 ? $" ({seat.Name.Trim()})" : "";
        header.AddChild(new Label
        {
            Text = $"{UiConstants.SeatDisplayName(seat.Color)}{name}",
            ThemeTypeVariation = UiTheme.SubheadingLabel,
        });
        var playedBy = new FactField("Played by", "Who these words are for: an agent's program, or a person with a copy of this app.", small: true);
        playedBy.Set(kind);
        header.AddChild(playedBy);
        header.AddChild(Ui.Spacer());
        var copy = Ui.Button("Copy", UiTheme.SmallButton, "Copy these words to the clipboard.");
        copy.Pressed += () =>
        {
            DisplayServer.ClipboardSet(text);
            copy.Text = "Copied";
            copy.GetTree().CreateTimer(1.5).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(copy)) copy.Text = "Copy";
            };
        };
        header.AddChild(copy);
        return header;
    }

    // ------------------------------------------------------------------
    // Readiness: what the table is, and what stops it starting
    // ------------------------------------------------------------------

    /// <summary>Copies the typed fields into the plan (everything but the seed, which is parsed at the start).</summary>
    private void SyncFormToSetup()
    {
        _setup.Address = _addressEdit.Text.Trim();
        _setup.ConnectAddress = _connectAddressEdit.Text.Trim();
        _setup.Port = (int)_portSpin.Value;
        _setup.RecordsFolder = _recordsEdit.Text.Trim();
        _setup.StallWarningSeconds = (int)_stallSpin.Value;
    }

    private void RefreshReadiness()
    {
        if (_readySummary is null || _addressEdit is null) return;
        SyncFormToSetup();

        var parts = new List<string>();
        foreach (var group in _setup.Seats.GroupBy(s => s.Kind).OrderBy(g => g.Key))
        {
            string names = string.Join(", ", group.Select(s => UiConstants.SeatDisplayName(s.Color)));
            parts.Add(group.Key switch
            {
                SeatPlanKind.ThisScreen => $"{names} on this screen",
                SeatPlanKind.Client => $"{names} by a client",
                SeatPlanKind.Agent => $"{names} by an agent",
                _ => $"{names} by a stand-in",
            });
        }
        string mode = _setup.Mode == TableMode.Duo ? "Duo" : "Standard";
        _readySummary.Text = $"{mode} game, {_setup.Seats.Count} seats: {string.Join("; ", parts)}.";

        var problems = _setup.Validate().ToList();
        string seedText = _seedEdit.Text.Trim();
        if (seedText.Length > 0 && !long.TryParse(seedText, out _))
            problems.Add("The seed must be a whole number, or blank.");
        _readyProblems.Text = problems.Count == 0 ? "" : "Before the table can start: " + string.Join(" ", problems);
        _readyProblems.Visible = problems.Count > 0;
        _startButton.Disabled = problems.Count > 0;

        int remote = _setup.Seats.Count(s => s.Kind is SeatPlanKind.Agent or SeatPlanKind.Client);
        _readyNext.Text = remote == 0
            ? "The game starts as soon as the table opens."
            : $"The table opens and listens on port {_setup.Port}. The game starts when the {remote} " +
              (remote == 1 ? "remote seat has" : "remote seats have") + " joined with the instructions below.";
    }

    // ------------------------------------------------------------------
    // Resume list
    // ------------------------------------------------------------------

    private void RefreshResumeList()
    {
        if (_resumeList is null) return;
        _suspended = SuspendedGames.Scan(_setup.RecordsFolder);
        _resumeFolder.Text = $"Records folder: {_setup.RecordsFolder} (set on the Host a game tab).";
        _resumeList.Clear();
        foreach (var game in _suspended)
        {
            string seats = string.Join(", ", game.SeatCodes.Select(UiConstants.SeatDisplayName));
            _resumeList.AddItem(
                $"Game type: {game.Mode}     Seed: {game.Seed}     Seats: {seats}     Decisions so far: {game.DecisionsSoFar}");
        }
        _resumeListHint.Text = _suspended.Count == 0
            ? "No suspended games in the records folder. A game is suspended at the table, from the Host tab " +
              "(Suspend)."
            : "Select a game to choose its seats.";
        Ui.SetStatus(_resumeStatus, "");
        _resumeChoices.Clear();
        foreach (Node child in _resumeSeatRows.GetChildren()) child.QueueFree();
        _resumeSeatRows.AddChild(Ui.Hint("No game selected."));
        _resumeButton.Disabled = true;
        RefreshResumeConnection();
    }

    private void RefreshResumeConnection()
    {
        if (_resumeConnection is null || _portSpin is null) return;
        _resumeConnection.Text =
            $"The resumed table listens on port {(int)_portSpin.Value} at {_addressEdit.Text.Trim()}, with a " +
            $"{(int)_stallSpin.Value}-second stall warning (set on the Host a game tab).";
    }

    private void RefreshResumeSeatRows()
    {
        foreach (Node child in _resumeSeatRows.GetChildren()) child.QueueFree();
        _resumeChoices.Clear();
        int selected = _resumeList.GetSelectedItems().FirstOrDefault(-1);
        _resumeButton.Disabled = selected < 0 || selected >= _suspended.Count;
        RefreshResumeConnection();
        if (selected < 0 || selected >= _suspended.Count) return;

        var game = _suspended[selected];
        for (int i = 0; i < game.SeatCodes.Count; i++)
        {
            int index = i;
            string code = game.SeatCodes[i];
            string recordedKind = i < game.SeatKinds.Count ? game.SeatKinds[i] : "scripted";
            var choice = new SeatPlan(code,
                recordedKind == "socket" ? SeatPlanKind.Agent : SeatPlanKind.StandIn);
            _resumeChoices.Add(choice);

            var row = new HBoxContainer();
            row.AddChild(Ui.Swatch(UiConstants.SeatColor(code)));
            row.AddChild(new Label
            {
                Text = UiConstants.SeatDisplayName(code),
                CustomMinimumSize = new Vector2(70, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var kindPicker = new OptionButton { CustomMinimumSize = new Vector2(160, 0) };
            foreach (string label in KindLabels) kindPicker.AddItem(label);
            kindPicker.Selected = (int)choice.Kind;
            kindPicker.ItemSelected += picked =>
            {
                var kind = (SeatPlanKind)(int)picked;
                if (kind == SeatPlanKind.ThisScreen)
                    foreach (var other in _resumeChoices)
                        if (!ReferenceEquals(other, _resumeChoices[index]) &&
                            other.Kind == SeatPlanKind.ThisScreen)
                            other.Kind = SeatPlanKind.StandIn;
                _resumeChoices[index].Kind = kind;
                RefreshResumeSeatRowSelections();
            };
            row.AddChild(kindPicker);
            var recorded = Ui.Hint(recordedKind == "socket"
                ? "recorded as a network seat"
                : "recorded as a stand-in");
            recorded.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(recorded);
            _resumeSeatRows.AddChild(row);
        }
    }

    private void RefreshResumeSeatRowSelections()
    {
        for (int i = 0; i < _resumeSeatRows.GetChildCount() && i < _resumeChoices.Count; i++)
        {
            var row = (HBoxContainer)_resumeSeatRows.GetChild(i);
            foreach (Node child in row.GetChildren())
                if (child is OptionButton picker)
                    picker.Selected = (int)_resumeChoices[i].Kind;
        }
    }

    // ------------------------------------------------------------------
    // Starting
    // ------------------------------------------------------------------

    private void OnStartPressed()
    {
        SyncFormToSetup();

        string seedText = _seedEdit.Text.Trim();
        if (seedText.Length == 0)
        {
            _setup.Seed = null;
        }
        else if (long.TryParse(seedText, out long seed))
        {
            _setup.Seed = seed;
        }
        else
        {
            ShowHostStatus("The seed must be a whole number, or blank.");
            return;
        }

        var problems = _setup.Validate();
        if (problems.Count > 0)
        {
            ShowHostStatus(string.Join(" ", problems));
            return;
        }
        ShowHostStatus("");
        HostStartRequested?.Invoke(_setup);
    }

    private void OnResumePressed()
    {
        int selected = _resumeList.GetSelectedItems().FirstOrDefault(-1);
        if (selected < 0 || selected >= _suspended.Count) return;
        ResumeRequested?.Invoke(new ResumeRequest(
            _suspended[selected],
            _resumeChoices.ToList(),
            _addressEdit.Text.Trim(),
            (int)_portSpin.Value,
            (int)_stallSpin.Value));
    }

    // ------------------------------------------------------------------
    // Status surfaces for the shell
    // ------------------------------------------------------------------

    /// <summary>Chooses which door the screen opens on (the shell's drivers use this too).</summary>
    public void ShowDoor(SetupDoor door) => _tabs.CurrentTab = (int)door;

    public void ShowHostStatus(string text) => Ui.SetStatus(_hostStatus, text);

    public void ShowResumeStatus(string text) => Ui.SetStatus(_resumeStatus, text);

    public void ShowJoinStatus(string text) => Ui.SetStatus(_joinStatus, text);

    public void ShowWatchStatus(string text) => Ui.SetStatus(_watchStatus, text);

    public void SetJoinBusy(bool busy) => _joinButton.Disabled = busy;

    public void SetWatchBusy(bool busy) => _watchButton.Disabled = busy;

    /// <summary>Shows the seed the host picked for a blank-seed table, in the form the host returns to.</summary>
    public void ShowEffectiveSeed(long seed) => _seedEdit.Text = seed.ToString();

    /// <summary>Fills the royal-court configuration the screenshot run photographs.</summary>
    public void FillRoyalCourt()
    {
        _setup.SetMode(TableMode.Standard);
        _setup.Seats.Clear();
        _setup.Seats.Add(new SeatPlan("RED", SeatPlanKind.ThisScreen) { Name = "Alex" });
        _setup.Seats.Add(new SeatPlan("BLU", SeatPlanKind.Client) { Name = "Sam" });
        _setup.Seats.Add(new SeatPlan("GRN", SeatPlanKind.Agent) { Name = "Jordan" });
        _setup.Seats.Add(new SeatPlan("YEL", SeatPlanKind.Agent) { Name = "Robin" });
        _modePicker.Selected = 0;
        RefreshModeHint();
        RefreshSeatRows();
        RefreshInstructionBlocks();
        RefreshReadiness();
    }
}
