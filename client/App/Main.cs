using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentRisk.Board;
using AgentRisk.Client.Protocol;
using AgentRisk.Engine;
using AgentRisk.Host;
using Godot;
using BoardModel = AgentRisk.Board.Board;

namespace AgentRisk.Client.App;

/// <summary>
/// The app shell: it owns the front door (the setup screen) and the
/// table screen, and moves between them. One app hosts, joins, and
/// watches games through the shared host library; the shell's
/// work is assembly and hand-off only — forms live in the setup screen,
/// the table lives in the table screen, parsing lives in the protocol
/// layer.
///
/// Modes: normal (the front door), <c>--selfcheck</c> (headless: the
/// shipped samples through the real parse-and-present path, the setup
/// screens constructed, guidance coverage cross-checked against the
/// engine's decision kinds, and an in-process hosted game played end to
/// end), <c>--autopilot HOST PORT SEAT</c> (headless: joins a live table
/// and answers with the plain legal-move policy; the short form
/// <c>--autopilot PORT SEAT</c> joins this machine's IPv6 loopback),
/// <c>--hosttest [RECORDS DIR]</c> (headless: hosts the fixed
/// four-seat table of <see cref="HostTestDriver"/> for two autopilot
/// instances to join), <c>--edittest [RECORDS DIR]</c> (headless:
/// hosts the fixed stand-in table of <see cref="EditTestDriver"/>,
/// pauses it, edits the position through the host library path the
/// edit panel uses, resumes, and plays out), <c>--wedgetest
/// [RECORDS DIR]</c> (headless: the failure drill of
/// <see cref="WedgeTestDriver"/> — an agent seat wedges mid-game and
/// the host pokes it, then replaces it with a stand-in), <c>--watchtest HOST
/// PORT</c> (headless: watches
/// a live table and counts the feed), and <c>--screenshot DIR</c>
/// (photographs every door of the front door and the table in its
/// main states: played on this screen, with its help open, hosted and
/// paused, finished, and watched).
/// </summary>
public partial class Main : Control
{
    private BoardCatalog _catalog = null!;
    private MapData _map = null!;
    private TerritoryLayout _layout = null!;
    private BoardModel _boardModel = null!;

    private SetupScreen _setup = null!;
    private TableScreen? _table;
    private HostedTable? _hosted;

    // Headless drivers.
    private bool _autopilot;
    private string _watchTestHost = "";
    private int _watchTestPort;
    private bool _watchTestDone;
    private HostTestDriver? _hostTest;
    private bool _hostTestReported;
    private EditTestDriver? _editTest;
    private bool _editTestReported;
    private WedgeTestDriver? _wedgeTest;
    private bool _wedgeTestReported;
    private int _hostTestMilestones;

    private abstract record ShellInboxItem
    {
        public sealed record JoinConnected(SeatClient Client, string Seat, string Name, string Host, int Port) : ShellInboxItem;
        public sealed record JoinFailed(Exception Error) : ShellInboxItem;
        public sealed record WatchConnected(SeatClient Client) : ShellInboxItem;
        public sealed record WatchFailed(Exception Error) : ShellInboxItem;
    }

    private readonly ConcurrentQueue<ShellInboxItem> _inbox = new();

    public override void _Ready()
    {
        _catalog = BoardCatalog.FromBoardJson(LoadTextResource("res://data/board.json"));
        _map = MapData.FromJson(LoadTextResource("res://data/map.json"));
        _layout = TerritoryLayout.FromJson(LoadTextResource("res://data/layout.json"));
        _boardModel = BoardLoader.FromJson(LoadTextResource("res://data/board.json"));

        // One theme for every screen; the clear colour matches it so no
        // frame ever shows the engine's default grey behind a screen.
        Theme = UiTheme.Build();
        RenderingServer.SetDefaultClearColor(UiConstants.Chrome.Background);

        _setup = new SetupScreen
        {
            DefaultRecordsFolder = ProjectSettings.GlobalizePath("user://game-records"),
        };
        _setup.SetAnchorsPreset(LayoutPreset.FullRect);
        _setup.HostStartRequested += OnHostStart;
        _setup.ResumeRequested += OnResume;
        _setup.JoinRequested += OnJoin;
        _setup.WatchRequested += OnWatch;
        AddChild(_setup);

        var args = OS.GetCmdlineUserArgs();
        if (args.Contains("--selfcheck"))
        {
            RunSelfCheck();
        }
        else if (args.Length >= 4 && args[0] == "--autopilot")
        {
            _autopilot = true;
            OnJoin(args[1], int.Parse(args[2]), args[3].ToUpperInvariant(), "");
        }
        else if (args.Length == 3 && args[0] == "--autopilot")
        {
            // --autopilot PORT SEAT: the host is this machine's IPv6
            // loopback, the address the hosttest table listens on.
            _autopilot = true;
            OnJoin(HostTestDriver.ListenAddress, int.Parse(args[1]), args[2].ToUpperInvariant(), "");
        }
        else if (args.Length >= 1 && args[0] == "--hosttest")
        {
            string records = args.Length >= 2
                ? args[1]
                : ProjectSettings.GlobalizePath("user://hosttest-records");
            try
            {
                _hostTest = new HostTestDriver(_boardModel, _catalog, records);
                _hostTest.Start();
            }
            catch (Exception ex)
            {
                GD.Print($"HOSTTEST FAILED reason={ex.Message}");
                GetTree().Quit(1);
            }
        }
        else if (args.Length >= 1 && args[0] == "--edittest")
        {
            // One real hosted game, paused mid-flight,
            // edited through the host library path the edit panel
            // uses, resumed, and played out — the guarded host edit's
            // live exercise. Records land where the caller says
            // (default: the app's user folder).
            string records = args.Length >= 2
                ? args[1]
                : ProjectSettings.GlobalizePath("user://edittest-records");
            try
            {
                _editTest = new EditTestDriver(_boardModel, records);
                _editTest.Start();
            }
            catch (Exception ex)
            {
                GD.Print($"EDITTEST FAILED reason={ex.Message}");
                GetTree().Quit(1);
            }
        }
        else if (args.Length >= 1 && args[0] == "--wedgetest")
        {
            // The failure drill. An agent seat joins,
            // plays a few prompts, and goes silent with its socket
            // open; the driver hosts the table the way the Host door
            // would — telemetry, a poke, a replacement — and the game
            // plays out to its recorded finish.
            string records = args.Length >= 2
                ? args[1]
                : ProjectSettings.GlobalizePath("user://wedgetest-records");
            try
            {
                _wedgeTest = new WedgeTestDriver(_boardModel, _catalog, records);
                _wedgeTest.Start();
            }
            catch (Exception ex)
            {
                GD.Print($"WEDGETEST FAILED reason={ex.Message}");
                GetTree().Quit(1);
            }
        }
        else if (args.Length >= 3 && args[0] == "--watchtest")
        {
            _watchTestHost = args[1];
            _watchTestPort = int.Parse(args[2]);
            OnWatch(_watchTestHost, _watchTestPort);
        }
        else if (args.Length >= 2 && args[0] == "--screenshot")
        {
            RunScreenshots(args[1]);
        }
    }

    public override void _Process(double delta)
    {
        while (_inbox.TryDequeue(out var item))
        {
            switch (item)
            {
                case ShellInboxItem.JoinConnected connected:
                    _setup.SetJoinBusy(false);
                    ShowTable(table => table.BeginJoined(
                        connected.Client, connected.Seat, connected.Name, connected.Host, connected.Port));
                    if (_autopilot) _table!.Autopilot = true;
                    break;
                case ShellInboxItem.JoinFailed failed:
                    _setup.SetJoinBusy(false);
                    string reason = failed.Error is SeatRefusedException
                        ? "That seat is taken. Pick another."
                        : $"Could not join: {failed.Error.Message}";
                    _setup.ShowJoinStatus(reason);
                    if (_autopilot)
                    {
                        GD.Print($"AUTOPILOT CONNECT FAILED: {reason}");
                        GetTree().Quit();
                    }
                    break;
                case ShellInboxItem.WatchConnected connected:
                    _setup.SetWatchBusy(false);
                    ShowTable(table => table.BeginWatched(connected.Client));
                    break;
                case ShellInboxItem.WatchFailed failed:
                    _setup.SetWatchBusy(false);
                    _setup.ShowWatchStatus($"Could not watch: {failed.Error.Message}");
                    if (_watchTestPort > 0 && !_watchTestDone)
                    {
                        _watchTestDone = true;
                        GD.Print($"WATCHTEST FAILED: {failed.Error.Message}");
                        GetTree().Quit();
                    }
                    break;
            }
        }

        if (_watchTestPort > 0 && !_watchTestDone && _table is not null && _table.GameFinished)
        {
            _watchTestDone = true;
            GD.Print($"WATCHTEST RESULT entries={_table.FeedEntriesSeen} boards={_table.BoardEntriesSeen} prompts={_table.PromptEntriesSeen}");
            GetTree().Quit();
        }

        if (_hostTest is not null && !_hostTestReported)
        {
            int milestonesDue = _hostTest.DecisionsSeen / HostTestDriver.MilestoneInterval;
            while (_hostTestMilestones < milestonesDue)
            {
                _hostTestMilestones++;
                GD.Print($"HOSTTEST DECISIONS {_hostTestMilestones * HostTestDriver.MilestoneInterval}");
            }
            if (_hostTest.Done)
            {
                _hostTestReported = true;
                ReportHostTest();
            }
        }

        if (_editTest is not null && !_editTestReported && _editTest.Done)
        {
            _editTestReported = true;
            ReportEditTest();
        }

        if (_wedgeTest is not null && !_wedgeTestReported && _wedgeTest.Done)
        {
            _wedgeTestReported = true;
            ReportWedgeTest();
        }
    }

    // The failure drill's closing lines, printed once the driver has
    // an outcome; the process exit code carries the verdict (0 only
    // for a completed game whose record and transcript both carry the
    // poke and the replacement).
    private void ReportWedgeTest()
    {
        var driver = _wedgeTest!;
        if (driver.Result is { } result)
        {
            string transcript = GameTranscriptWriter.PathForRecord(result.RecordPath);
            GD.Print($"WEDGETEST RESULT status={result.Status} winner={result.Winner ?? "none"} decisions={result.Decisions}");
            GD.Print($"WEDGETEST RECORD path={result.RecordPath} exists={System.IO.File.Exists(result.RecordPath)}");
            GD.Print($"WEDGETEST TRANSCRIPT path={transcript} exists={System.IO.File.Exists(transcript)}");
            GetTree().Quit(result.Status == SessionStatus.Completed && driver.EvidenceOk ? 0 : 1);
        }
        else
        {
            GD.Print($"WEDGETEST FAILED reason={driver.Failure}");
            GetTree().Quit(1);
        }
    }

    // The edittest exercise's closing lines, printed once the driver
    // has an outcome; the process exit code carries the verdict (0
    // only for a completed game whose edit applied).
    private void ReportEditTest()
    {
        var driver = _editTest!;
        if (driver.Result is { } result)
        {
            string transcript = GameTranscriptWriter.PathForRecord(result.RecordPath);
            GD.Print($"EDITTEST RESULT status={result.Status} winner={result.Winner ?? "none"} decisions={result.Decisions}");
            GD.Print($"EDITTEST RECORD path={result.RecordPath} exists={System.IO.File.Exists(result.RecordPath)}");
            GD.Print($"EDITTEST TRANSCRIPT path={transcript} exists={System.IO.File.Exists(transcript)}");
            GetTree().Quit(driver.EditSummary.Length > 0 && result.Status == SessionStatus.Completed ? 0 : 1);
        }
        else
        {
            GD.Print($"EDITTEST FAILED reason={driver.Failure}");
            GetTree().Quit(1);
        }
    }

    // The hosttest run's closing lines, printed on the main thread once
    // the driver has an outcome; the process exit code carries the
    // verdict (0 only for a completed game).
    private void ReportHostTest()
    {
        var driver = _hostTest!;
        if (driver.Result is { } result)
        {
            string transcript = GameTranscriptWriter.PathForRecord(result.RecordPath);
            GD.Print($"HOSTTEST RESULT status={result.Status} winner={result.Winner ?? "none"} decisions={result.Decisions} wall_seconds={driver.Elapsed.TotalSeconds:0.0}");
            GD.Print($"HOSTTEST RECORD path={result.RecordPath} exists={System.IO.File.Exists(result.RecordPath)}");
            GD.Print($"HOSTTEST TRANSCRIPT path={transcript} exists={System.IO.File.Exists(transcript)}");
            GetTree().Quit(result.Status == SessionStatus.Completed ? 0 : 1);
        }
        else
        {
            GD.Print($"HOSTTEST FAILED reason={driver.Failure}");
            GetTree().Quit(1);
        }
    }

    // ------------------------------------------------------------------
    // The doors
    // ------------------------------------------------------------------

    private void OnHostStart(TableSetup setup)
    {
        try
        {
            var notices = new ConcurrentQueue<string>();
            var hosted = new HostedTable(_boardModel, setup, message => notices.Enqueue(message));
            StartOrDispose(hosted);
            _hosted = hosted;
            _setup.ShowEffectiveSeed(hosted.EffectiveSeed);
            ShowTable(table => table.BeginHosted(hosted, notices, setup.ConnectAddress));
        }
        catch (ArgumentException)
        {
            // The plan passed the form but does not make a legal game
            // (the engine guards its own seat rules): the host reads
            // plain words, never the exception's.
            _setup.ShowHostStatus("The table could not start: those seats do not make a legal game. " +
                                  "A Standard game seats three to six players; two players play a Duo game.");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            // A start can fail at the socket for different reasons and
            // they are not interchangeable (a busy port, a listen
            // address this computer does not have, a port Windows
            // reserves): log the real exception, code included, and
            // let the host's words match what the code proves.
            GD.PrintErr($"The table could not start: {ex}");
            _setup.ShowHostStatus(SocketStartMessage(ex, setup));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"The table could not start: {ex}");
            _setup.ShowHostStatus("The table could not start. Check the address, the port, and the records folder, then try again.");
        }
    }

    /// <summary>
    /// Plain words for a socket failure at the host door, chosen by
    /// the error the operating system actually returned: the words
    /// must never claim a cause the code does not establish.
    /// </summary>
    private static string SocketStartMessage(System.Net.Sockets.SocketException ex, TableSetup setup) =>
        ex.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.AddressAlreadyInUse =>
                $"The table could not start: port {setup.Port} is already in use. " +
                "Another table may already be running on it; stop that one or choose a different port.",
            System.Net.Sockets.SocketError.AddressNotAvailable =>
                $"The table could not start: this computer has no address {setup.Address} to listen on. " +
                "Set the listen address to 0.0.0.0 to listen on every address, then try again.",
            System.Net.Sockets.SocketError.AccessDenied =>
                $"The table could not start: Windows refused the listener on port {setup.Port}. " +
                "Windows reserves some port ranges for itself; choose a different port, then try again.",
            _ =>
                $"The table could not start: a network error ({ex.SocketErrorCode}) stopped it. " +
                "The app's log file has the details.",
        };

    private void OnResume(ResumeRequest request)
    {
        ResumePlan plan;
        try
        {
            plan = ResumePlan.FromFiles(request.Game.RecordPath, request.Game.TranscriptPath);
        }
        catch (Exception ex) when (ex is ResumeException or TranscriptFormatException or System.IO.IOException)
        {
            _setup.ShowResumeStatus($"That game cannot be resumed: {ex.Message}");
            return;
        }

        try
        {
            var notices = new ConcurrentQueue<string>();
            var hosted = HostedTable.ForResume(
                _boardModel, plan, request.SeatChoices,
                request.Address, request.Port, request.StallWarningSeconds,
                message => notices.Enqueue(message));
            StartOrDispose(hosted);
            _hosted = hosted;
            ShowTable(table => table.BeginHosted(hosted, notices, LanAddress.DefaultConnectAddress()));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"The resumed table could not start: {ex}");
            _setup.ShowResumeStatus("That game cannot be resumed at this table. Check the address and the port on the " +
                                    "Host a game tab, then try again.");
        }
    }

    /// <summary>
    /// Starts a hosted table, closing it again if the start fails (a
    /// records folder that cannot be created, a resumed session the
    /// host library refuses). The table's listener is bound from
    /// construction, so a table dropped without disposal would keep
    /// the port, and the host's retry would share it with a listener
    /// nobody serves.
    /// </summary>
    private static void StartOrDispose(HostedTable hosted)
    {
        try
        {
            hosted.Start();
        }
        catch
        {
            hosted.Dispose();
            throw;
        }
    }

    private void OnJoin(string host, int port, string seat, string name)
    {
        _setup.SetJoinBusy(true);
        _setup.ShowJoinStatus($"Joining the table as {UiConstants.SeatDisplayName(seat)}…");
        var thread = new Thread(() =>
        {
            try
            {
                var client = SeatClient.Connect(host, port, seat);
                _inbox.Enqueue(new ShellInboxItem.JoinConnected(client, seat, name, host, port));
            }
            catch (Exception ex)
            {
                _inbox.Enqueue(new ShellInboxItem.JoinFailed(ex));
            }
        })
        { IsBackground = true, Name = "SeatClient handshake" };
        thread.Start();
    }

    private void OnWatch(string host, int port)
    {
        _setup.SetWatchBusy(true);
        _setup.ShowWatchStatus("Connecting to the table…");
        var thread = new Thread(() =>
        {
            try
            {
                var client = SeatClient.Connect(host, port, "WATCH");
                _inbox.Enqueue(new ShellInboxItem.WatchConnected(client));
            }
            catch (Exception ex)
            {
                _inbox.Enqueue(new ShellInboxItem.WatchFailed(ex));
            }
        })
        { IsBackground = true, Name = "Watch handshake" };
        thread.Start();
    }

    // ------------------------------------------------------------------
    // Moving between the door and the table
    // ------------------------------------------------------------------

    private void ShowTable(Action<TableScreen> begin)
    {
        _setup.Visible = false;
        var table = new TableScreen();
        table.Configure(_catalog, _map, _layout);
        table.SetAnchorsPreset(LayoutPreset.FullRect);
        table.LeaveRequested += LeaveTable;
        table.GameOverWinner += winner =>
        {
            if (_autopilot)
            {
                GD.Print($"AUTOPILOT RESULT winner={winner}");
                GetTree().Quit();
            }
        };
        AddChild(table);
        _table = table;
        begin(table);
    }

    private void LeaveTable()
    {
        if (_table is not null)
        {
            _table.Shutdown();
            RemoveChild(_table);
            _table.QueueFree();
            _table = null;
        }
        _hosted?.Dispose();
        _hosted = null;
        _setup.Visible = true;
    }

    private static string LoadTextResource(string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file is null)
            throw new InvalidOperationException($"Resource missing: {path} ({FileAccess.GetOpenError()})");
        return file.GetAsText();
    }

    // ------------------------------------------------------------------
    // Headless self-check: the shipped samples through the real
    // parse-and-present path, the setup screens constructed, guidance
    // coverage cross-checked, and an in-process hosted game played end
    // to end in the app.
    // ------------------------------------------------------------------

    private async void RunSelfCheck()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            GD.Print($"SELF CHECK {(ok ? "PASS" : "FAIL")}: {what}");
            if (!ok) failures++;
        }

        Check(_catalog.Territories.Count == 42, "shipped catalog holds 42 territories");
        Check(_layout.TryPositionOf("KAM", out _), "layout places every territory");
        Check(_map.Territories.Count == 42, "map dataset holds 42 territories");
        Check(_map.SeaLinks.Contains(("ALA", "KAM")), "map dataset carries the Alaska-Kamchatka sea link");

        // The setup screens construct for every door.
        foreach (var door in Enum.GetValues<SetupDoor>()) _setup.ShowDoor(door);
        _setup.ShowDoor(SetupDoor.Overview);
        Check(true, "setup screens construct for every door");

        // Guidance covers every decision kind the engine can ask.
        var engineKinds = Enum.GetNames<DecisionKind>();
        Check(engineKinds.All(kind => Guidance.CoveredDecisionKinds.Contains(kind)),
            "guidance covers every engine decision kind");
        Check(Guidance.CoveredDecisionKinds.Count == engineKinds.Length,
            "guidance coverage names no kind the engine does not have");

        // The agent instruction block carries the live values and the
        // required content.
        var sampleSeat = new SeatPlan("GRN", SeatPlanKind.Agent) { Name = "Jordan" };
        string block = AgentInstructions.ForAgentSeat("192.0.2.10", 4321, sampleSeat);
        Check(block.Contains("JOIN GRN") && block.Contains("192.0.2.10") && block.Contains("4321"),
            "instruction block fills the seat, address, and port");
        Check(block.Contains("one PLC reply per territory") && block.Contains("full send")
            && block.Contains("defender is still asked") && block.Contains("hundreds of decisions")
            && block.Contains("decide for yourself") && block.Contains("summary of everything"),
            "instruction block carries the required content");

        var briefing = PromptParser.ParseBlock(LoadTextResource("res://data/sample_briefing.txt"), expectsReply: false);
        Check(briefing is PromptMessage.Briefing, "sample briefing parses as a briefing");

        // The sample prompt through the table screen's real present path.
        ShowTable(table => { });
        var parsed = PromptParser.ParseBlock(LoadTextResource("res://data/sample_prompt.txt"), expectsReply: true);
        Check(parsed is PromptMessage.Decision, "sample prompt parses as a decision");
        if (parsed is PromptMessage.Decision decision)
        {
            _table!.PresentDecision(decision.Prompt, _ => { });
            Check(decision.Prompt.Ask is DecisionAsk.AttackDice, "sample ask classifies as attack dice");
            Check(decision.Prompt.Board.Territories.Count == 42, "sample board holds 42 territories");
            Check(_table.LastBattle is { Attacker: "YEL", Defender: "RED" },
                "sample battle folds with the defender who held the target");
            Check(ReplyComposer.Dice(3) == "DICE 3", "reply composer speaks the grammar");
        }

        var strike = PromptParser.ParseBlock(LoadTextResource("res://data/sample_strike3.txt"), expectsReply: true);
        Check(strike is PromptMessage.StrikeThree { Moves.Count: 4 }, "sample strike-3 parses with its four moves");
        LeaveTable();

        await RunHostedSelfCheck(Check);
        await RunResumeSelfCheck(Check);

        // Let the presented state actually draw for two frames. (Reading
        // pixels back is not part of the check: the headless dummy driver
        // has no texture to read, and asking it for one errors.)
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print("SELF CHECK INFO: sample state presented and drawn for two frames without error");

        GD.Print(failures == 0 ? "SELF CHECK PASS" : $"SELF CHECK FAIL: {failures} check(s) failed");
        GetTree().Quit();
    }

    // ------------------------------------------------------------------
    // The unified app's hosting path, exercised end to end: an in-process
    // session of three stand-ins at the reference seed must play the
    // reference game into the app's own records folder, while a watch
    // connection to the app's own listener receives the feed.
    // ------------------------------------------------------------------

    private async Task RunHostedSelfCheck(Action<bool, string> check)
    {
        string records = ProjectSettings.GlobalizePath("user://selfcheck-records");
        var setup = new TableSetup
        {
            Address = HostTestDriver.ListenAddress,
            Port = 0,
            Seed = 43,
            RecordsFolder = records,
            StallWarningSeconds = 120,
        };
        setup.Seats.Clear();
        setup.Seats.Add(new SeatPlan("RED", SeatPlanKind.StandIn));
        setup.Seats.Add(new SeatPlan("BLU", SeatPlanKind.StandIn));
        setup.Seats.Add(new SeatPlan("GRN", SeatPlanKind.StandIn));

        var notices = new ConcurrentQueue<string>();
        using var table = new HostedTable(_boardModel, setup, message => notices.Enqueue(message));
        table.Start();
        check(table.ListenPort > 0, "hosted table's listener is up");

        var feedEntries = new ConcurrentQueue<FeedEntry>();
        SeatClient? tap = null;
        try
        {
            tap = SeatClient.Connect(HostTestDriver.ListenAddress, table.ListenPort, "WATCH");
            tap.BlockReceived += block => feedEntries.Enqueue(FeedEntry.Parse(block.Text));
            tap.StartReading();

            var finished = await Task.WhenAny(table.RunTask!, Task.Delay(TimeSpan.FromSeconds(120)));
            check(finished == table.RunTask, "hosted all-stand-in game finished");
            if (finished == table.RunTask)
            {
                var result = table.RunTask!.Result;
                check(result.Status == SessionStatus.Completed && result.Winner == "RED" && result.Decisions == 402,
                    "hosted game plays the reference game (RED, 402 decisions)");
                check(System.IO.File.Exists(result.RecordPath), "hosted game wrote its record");
            }
            await Task.Delay(300); // let the final feed entries land
        }
        catch (Exception ex)
        {
            check(false, $"watch tap on the hosted table failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            tap?.Dispose();
        }

        check(feedEntries.Any(e => e.Kind == FeedEntryKind.Board && e.Board is not null),
            "watch tap received board entries from the hosted game");
        check(feedEntries.Any(e => e.Kind == FeedEntryKind.Result),
            "watch tap received the hosted game's result");
    }

    // ------------------------------------------------------------------
    // Screenshots (run under Xvfb): every door of the front door, the
    // table mid-game with a seat on this screen, its help, a host-only
    // table paused for the host, the same table finished, and the table
    // in watch mode.
    // ------------------------------------------------------------------

    private async void RunScreenshots(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);

        async Task Frames(int count)
        {
            for (int i = 0; i < count; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        void SaveShot(string name)
        {
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng(System.IO.Path.Combine(directory, name));
            GD.Print($"SCREENSHOT {name} {image.GetWidth()}x{image.GetHeight()}");
        }
        TableSetup DemoSetup(params SeatPlan[] seats)
        {
            var setup = new TableSetup
            {
                Address = HostTestDriver.ListenAddress,
                Port = 0,
                Seed = 43,
                RecordsFolder = ProjectSettings.GlobalizePath("user://screenshot-records"),
                StallWarningSeconds = 120,
            };
            setup.Seats.Clear();
            setup.Seats.AddRange(seats);
            return setup;
        }

        // 1. The front door: the overview, then the host door in the
        // royal-court configuration, then the other doors.
        _setup.ShowDoor(SetupDoor.Overview);
        await Frames(4);
        SaveShot("overview.png");
        _setup.FillRoyalCourt();
        _setup.ShowDoor(SetupDoor.Host);
        await Frames(4);
        SaveShot("setup.png");
        _setup.ShowDoor(SetupDoor.Resume);
        await Frames(4);
        SaveShot("resume-door.png");
        _setup.ShowDoor(SetupDoor.Join);
        await Frames(4);
        SaveShot("join-door.png");
        _setup.ShowDoor(SetupDoor.Watch);
        await Frames(4);
        SaveShot("watch-door.png");

        // 2. A hosted table mid-game: Alex plays RED on this screen,
        // answered by the demo policy so the game runs for the camera;
        // three stand-ins fill the rest of the court.
        OnHostStart(DemoSetup(
            new SeatPlan("RED", SeatPlanKind.ThisScreen) { Name = "Alex" },
            new SeatPlan("BLU", SeatPlanKind.StandIn) { Name = "Sam" },
            new SeatPlan("GRN", SeatPlanKind.StandIn) { Name = "Jordan" },
            new SeatPlan("YEL", SeatPlanKind.StandIn) { Name = "Robin" }));
        _table!.DemoAutoAnswer = true;

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (_table.PromptEntriesSeen < 195 && DateTime.UtcNow < deadline)
            await Task.Delay(250);
        _table.DemoAutoAnswer = false;
        await Task.Delay(800); // the next local decision stands on screen
        await Frames(4);
        SaveShot("table.png");
        GD.Print($"SCREENSHOT table at prompt entries={_table.PromptEntriesSeen}");
        _table.ShowHelpForScreenshot();
        await Frames(4);
        SaveShot("help.png");
        _table.CloseHelpForScreenshot();
        // Play on a while, so the position has stacks to attack and
        // fortify from, then photograph the decision controls on it.
        _table.DemoAutoAnswer = true;
        deadline = DateTime.UtcNow.AddSeconds(60);
        while (_table.PromptEntriesSeen < 420 && !_table.GameFinished && DateTime.UtcNow < deadline)
            await Task.Delay(250);
        _table.DemoAutoAnswer = false;
        await Task.Delay(800);
        await ShootDecisionControls(_table, Frames, SaveShot);
        LeaveTable();

        // 3. A host-only table: every seat a stand-in, paused by the host
        // a moment in, so the Host tab and the pause banner stand on
        // screen; then resumed and played to its finish.
        OnHostStart(DemoSetup(
            new SeatPlan("RED", SeatPlanKind.StandIn),
            new SeatPlan("BLU", SeatPlanKind.StandIn),
            new SeatPlan("GRN", SeatPlanKind.StandIn),
            new SeatPlan("YEL", SeatPlanKind.StandIn),
            new SeatPlan("PNK", SeatPlanKind.StandIn)));
        _hosted?.PauseGame();
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (_hosted?.Snapshot()?.Status != SessionStatus.HostPaused && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await Task.Delay(1200); // the feed and the host tab catch up
        await Frames(4);
        SaveShot("host.png");
        _table!.ShowEditForScreenshot();
        await Frames(4);
        SaveShot("edit.png");
        _table.CloseEditForScreenshot();
        _hosted?.ResumeGame();
        deadline = DateTime.UtcNow.AddSeconds(60);
        while (!_table!.GameFinished && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        await Task.Delay(1000);
        await Frames(4);
        SaveShot("gameover.png");
        LeaveTable();

        // 3b. A Duo table, paused the same way: two seats and the Rebels.
        var duo = DemoSetup(
            new SeatPlan("BLU", SeatPlanKind.StandIn),
            new SeatPlan("YEL", SeatPlanKind.StandIn));
        duo.SetMode(TableMode.Duo);
        duo.Seats.Clear();
        duo.Seats.Add(new SeatPlan("BLU", SeatPlanKind.StandIn));
        duo.Seats.Add(new SeatPlan("YEL", SeatPlanKind.StandIn));
        OnHostStart(duo);
        _hosted?.PauseGame();
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (_hosted?.Snapshot()?.Status != SessionStatus.HostPaused && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await Task.Delay(1200);
        await Frames(4);
        SaveShot("duo.png");
        LeaveTable();

        // 4. The table in watch mode: a fresh hosted game plays in the
        // background (its this-screen seat answered by a plain thread,
        // the way the self-check answers), and the only screen reads it
        // through the feed alone, from the game's first decisions.
        var watchSetup = DemoSetup(
            new SeatPlan("RED", SeatPlanKind.ThisScreen) { Name = "Alex" },
            new SeatPlan("BLU", SeatPlanKind.StandIn) { Name = "Sam" },
            new SeatPlan("GRN", SeatPlanKind.StandIn) { Name = "Jordan" },
            new SeatPlan("YEL", SeatPlanKind.StandIn) { Name = "Robin" });
        var watchNotices = new ConcurrentQueue<string>();
        var watchHosted = new HostedTable(_boardModel, watchSetup, message => watchNotices.Enqueue(message));
        watchHosted.Start();
        var answerChannel = watchHosted.LocalChannel!;
        bool answering = true;
        var answerer = new Thread(() =>
        {
            while (answering)
            {
                if (answerChannel.TryTakeRequest(out var request) && !request.IsResend)
                {
                    var message = PromptParser.ParseBlock(request.Text, expectsReply: true);
                    string? reply = message switch
                    {
                        PromptMessage.Decision decision => AutoPolicy.Answer(decision.Prompt, _catalog),
                        PromptMessage.StrikeThree => AutoPolicy.AnswerStrikeThree(),
                        _ => null,
                    };
                    request.Answer(reply);
                }
                Thread.Sleep(2);
            }
        })
        { IsBackground = true, Name = "Screenshot seat answerer" };
        answerer.Start();

        ShowTable(table =>
        {
            var tap = SeatClient.Connect(HostTestDriver.ListenAddress, watchHosted.ListenPort, "WATCH");
            table.BeginWatched(tap);
        });
        deadline = DateTime.UtcNow.AddSeconds(60);
        while (_table!.PromptEntriesSeen < 150 && DateTime.UtcNow < deadline)
            await Task.Delay(250);
        await Frames(4);
        SaveShot("watch.png");
        GD.Print($"SCREENSHOT watch at prompt entries={_table.PromptEntriesSeen}");

        answering = false;
        watchHosted.Dispose();
        GetTree().Quit();
    }

    /// <summary>
    /// The decision controls on the demo table's own position: an attack
    /// with its route picked, the dice, a capture move-in, a fortify with
    /// its route picked, and a card bonus with a choice of territories.
    /// Routes are found the way a player reads the board (own land,
    /// printed adjacency); nothing is sent.
    /// </summary>
    private async Task ShootDecisionControls(TableScreen table, Func<int, Task> frames, Action<string> saveShot)
    {
        if (table.LastLocalPrompt is not { } prompt) return;
        var board = prompt.Board;
        string me = prompt.Seat;
        var mine = board.Territories.Values.Where(t => t.Owner == me).OrderByDescending(t => t.Armies).ToList();
        IEnumerable<string> Neighbors(string code) =>
            _catalog.TryTerritory(code, out var info) ? info.Neighbors : Array.Empty<string>();

        var attack = mine
            .Where(t => t.Armies >= 2)
            .Select(t => (From: t.Code, To: Neighbors(t.Code).FirstOrDefault(n =>
                board.Territories.TryGetValue(n, out var other) && other.Owner is not null && other.Owner != me)))
            .FirstOrDefault(route => route.To is not null);
        if (attack.To is not null)
        {
            table.ShowAskForScreenshot(new DecisionAsk.AttackDeclare(true), "ATK");
            table.ClickTerritoryForScreenshot(attack.From);
            table.ClickTerritoryForScreenshot(attack.To);
            await frames(4);
            saveShot("attack.png");

            table.ShowAskForScreenshot(new DecisionAsk.AttackDice(attack.From, attack.To, 3, true), "ATK");
            table.ShowRoundBattleForScreenshot(attack.From, attack.To);
            await frames(4);
            saveShot("dice.png");

            table.ShowAskForScreenshot(new DecisionAsk.CaptureMove(attack.From, attack.To, 3, 11), "ATK");
            await frames(4);
            saveShot("capture.png");
        }

        var fortify = mine
            .Where(t => t.Armies >= 2)
            .Select(t => (From: t.Code, To: Neighbors(t.Code).FirstOrDefault(n =>
                board.Territories.TryGetValue(n, out var other) && other.Owner == me)))
            .FirstOrDefault(route => route.To is not null);
        BoardState? fortifyBoard = null;
        if (fortify.To is null && mine.FirstOrDefault(t => t.Armies >= 2) is { } stack
            && Neighbors(stack.Code).FirstOrDefault() is { } neighbor)
        {
            // No fortify route on this position: arrange one, for this
            // photograph only, by handing the seat one bordering territory
            // on a copy of the board (map and panel both show the copy).
            var territories = board.Territories.ToDictionary(kv => kv.Key, kv => kv.Value);
            territories[neighbor] = territories[neighbor] with { Owner = me };
            fortifyBoard = new BoardState
            {
                Round = board.Round,
                TurnSeat = me,
                Phase = "FRT",
                NextTradeValue = board.NextTradeValue,
                Territories = territories,
                ContinentGroups = board.ContinentGroups,
                Players = board.Players,
                Hand = board.Hand,
                ReinOwed = board.ReinOwed,
            };
            fortify = (stack.Code, neighbor);
        }
        if (fortify.To is not null)
        {
            table.ShowAskForScreenshot(new DecisionAsk.Fortify(), "FRT", fortifyBoard);
            table.ClickTerritoryForScreenshot(fortify.From);
            table.ClickTerritoryForScreenshot(fortify.To);
            await frames(4);
            saveShot("fortify.png");
        }

        if (mine.Count >= 2)
        {
            table.ShowAskForScreenshot(new DecisionAsk.MatchPlace(new[] { mine[0].Code, mine[1].Code }, 2), "REIN");
            await frames(4);
            saveShot("bonus-choice.png");
        }
    }

    // ------------------------------------------------------------------
    // The resume door's plumbing, exercised end to end: a hosted game
    // with a slowly answered this-screen seat is suspended by the host,
    // found by the records scan, and resumed with stand-ins — and the
    // resumed game completes the reference game exactly.
    // ------------------------------------------------------------------

    private async Task RunResumeSelfCheck(Action<bool, string> check)
    {
        string records = ProjectSettings.GlobalizePath("user://selfcheck-records");
        var setup = new TableSetup
        {
            Address = HostTestDriver.ListenAddress,
            Port = 0,
            Seed = 43,
            RecordsFolder = records,
            StallWarningSeconds = 120,
        };
        setup.Seats.Clear();
        setup.Seats.Add(new SeatPlan("RED", SeatPlanKind.ThisScreen));
        setup.Seats.Add(new SeatPlan("BLU", SeatPlanKind.StandIn));
        setup.Seats.Add(new SeatPlan("GRN", SeatPlanKind.StandIn));

        var notices = new ConcurrentQueue<string>();
        string recordPath;
        int suspendedDecisions;
        using (var table = new HostedTable(_boardModel, setup, message => notices.Enqueue(message)))
        {
            table.Start();
            var channel = table.LocalChannel!;
            int promptsSeen = 0;
            bool answering = true;
            var standIn = new ScriptedSeatPolicy(BoardProjectBoardData.FromBoard(_boardModel));
            var answerer = new Thread(() =>
            {
                while (answering)
                {
                    if (channel.TryTakeRequest(out var request) && !request.IsResend)
                    {
                        Interlocked.Increment(ref promptsSeen);
                        Thread.Sleep(15); // a thinking human, briefly: the suspend must land mid-game
                        // The seat answers as the stand-in would, so the game
                        // before the suspend is the reference game's, and the
                        // resumed game (all stand-ins) must finish exactly as
                        // the uninterrupted reference does.
                        request.Answer(standIn.Respond(request.Text));
                    }
                    Thread.Sleep(2);
                }
            })
            { IsBackground = true, Name = "Self-check seat answerer" };
            answerer.Start();

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Volatile.Read(ref promptsSeen) < 10 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            check(Volatile.Read(ref promptsSeen) >= 10, "resume-check game is under way before the suspend");

            table.SuspendGame();
            var finished = await Task.WhenAny(table.RunTask!, Task.Delay(TimeSpan.FromSeconds(60)));
            check(finished == table.RunTask, "hosted game stopped for the suspend");
            var suspended = finished == table.RunTask ? table.RunTask!.Result : null;
            check(suspended is { Status: SessionStatus.Suspended },
                "host word suspends the hosted game");
            answering = false;
            recordPath = suspended?.RecordPath ?? "";
            suspendedDecisions = suspended?.Decisions ?? 0;
        }

        var listed = SuspendedGames.Scan(records);
        check(listed.Any(g => g.RecordPath == recordPath),
            "suspended game is listed for resume");
        var game = listed.First(g => g.RecordPath == recordPath);
        check(game.SeatCodes.SequenceEqual(new[] { "RED", "BLU", "GRN" }) && game.DecisionsSoFar == suspendedDecisions,
            "resume listing reads the suspended game's seats and stopping point");

        var plan = ResumePlan.FromFiles(game.RecordPath, game.TranscriptPath);
        var choices = game.SeatCodes.Select(code => new SeatPlan(code, SeatPlanKind.StandIn)).ToList();
        using var resumed = HostedTable.ForResume(_boardModel, plan, choices, HostTestDriver.ListenAddress, 0, 120,
            message => notices.Enqueue(message));
        resumed.Start();
        var done = await Task.WhenAny(resumed.RunTask!, Task.Delay(TimeSpan.FromSeconds(120)));
        check(done == resumed.RunTask, "resumed game finished");
        if (done == resumed.RunTask)
        {
            var result = resumed.RunTask!.Result;
            check(result.Status == SessionStatus.Completed && result.Winner == "RED" && result.Decisions == 402,
                "resumed game completes the reference game (RED, 402 decisions)");
        }
    }
}
