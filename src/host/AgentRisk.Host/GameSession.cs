using AgentRisk.Board;
using AgentRisk.Engine;
using BoardModel = AgentRisk.Board.Board;

namespace AgentRisk.Host;

/// <summary>
/// One hosted game: owns the engine, the seats' channels, the prompt
/// contract, the failure policy, the game record,
/// and the per-seat prompt/reply transcript written beside the record
/// (<see cref="GameTranscriptWriter"/>). Every transcript entry is also
/// published to the spectator feed when one is attached (see
/// <see cref="Emit"/>), together with a fresh board block after
/// every applied decision, so watchers follow the same game the
/// transcript proves.
///
/// THREADING MODEL. <see cref="Run"/> drives the decision loop on its
/// calling thread. The host drives its side from another thread through
/// the Host* methods, in two ways, because the loop has two shapes of
/// waiting:
///
/// - While the loop waits for the host (a strike-pause), the pause
///   controls apply directly: <see cref="HostSubstitute"/>,
///   <see cref="HostReplaceSeat"/> (a ready channel), and
///   <see cref="HostEndGame"/> act through the pause plumbing.
/// - Everything else is a command the loop applies itself at a
///   decision boundary — the top of the loop, the wait inside a host
///   pause, and the entry to (and wait inside) a strike-pause:
///   <see cref="HostReplaceSeatKind"/> queues a change of how a seat is
///   driven, <see cref="HostPause"/>/<see cref="HostResume"/> set and
///   clear a flag the loop honors between decisions, and
///   <see cref="HostPoke"/> is the one immediate action, taken on the
///   host's thread against the tracked pending prompt, because the
///   loop is blocked inside that prompt's read and no queue can reach
///   it. Replacing the seat the table is waiting on closes its channel
///   (a closable one: a socket), which fails the outstanding exchange
///   into the strike-pause the queued replacement then resolves — so a
///   wedged seat can always be replaced, and an in-process seat, which
///   cannot be interrupted, is replaced when its current answer lands.
///
/// Every host intervention is recorded at the moment it takes effect,
/// in the game record as a HOST line and in the transcript
/// as the mirrored ### event. A host's touch is never invisible.
///
/// THE GUARDED EDIT. <see cref="HostApplyEditAsync"/> queues
/// the one intervention that touches the position itself. The loop
/// applies it only inside the host pause, atomically, through the
/// engine's one audited edit entry point, which validates the closed
/// set itself (a territory's owner and army count; nothing else
/// exists to change). The edit lands in the record (HOST EDIT lines
/// and the header's host-edited flag), in the transcript, and in a
/// notice to every seat; one edit is applied per pause. A suspended
/// game's transcript carries its edits in the choice stream, so the
/// replay applies them at the same points and the resumed game is the
/// edited game, flag included.
///
/// SUSPENSION AND RESUME. <see cref="HostSuspend"/> asks the
/// loop to close the game in a suspended state — at the next decision
/// boundary, or from inside a strike-pause with the standing decision
/// left unanswered — so the transcript's choice stream always ends at
/// a well-defined point. A session built by <see cref="CreateResumed"/>
/// over the suspended game's <see cref="ResumePlan"/> replays that
/// stream through a fresh engine (the seed makes re-execution exact),
/// verifies the rebuilt game against the record at the boundary, and
/// only then goes live in the same record and transcript. Until that
/// verification passes, nothing is appended to either file: a diverged
/// replay abandons the resume and leaves the suspended game untouched.
///
/// The Rebels are never a channel: in Duo the engine resolves every Rebel
/// decision itself (setup placement, their whole turn, defense at maximum)
/// through the game's seeded RNG, so this class refuses a channel for the
/// Rebel seat outright, and a pending decision addressed to REB would be
/// an engine contract breach, not something to answer here.
/// </summary>
public sealed class GameSession
{
    private readonly SessionConfig _config;
    private readonly GameConfig _gameConfig;
    private readonly PromptBuilder _prompts;
    private readonly ScriptedSeatPolicy _scriptedPolicy;
    private readonly RiskGame _game;
    private readonly Action<string>? _notifyHost;
    private readonly SpectatorFeed? _spectators;
    private readonly SocketSeatConnector? _seatConnector;

    // The seats' channels and current kinds. Channels are swapped by
    // replacements from both the loop thread and (one legacy path) the
    // host thread, so every access takes _channelsGate; kinds are read
    // by telemetry on host threads, so they sit under _stateGate with
    // the rest of the telemetry state.
    private readonly object _channelsGate = new();
    private readonly Dictionary<string, IAgentChannel> _channels;
    private readonly Dictionary<string, SeatKind> _currentKinds;

    // Summary cursors: per seat, how far into the engine log
    // that seat has been shown. A cursor advances only when one of the
    // seat's decisions resolves, and only to where the log stood when
    // that decision was first presented, so the events the decision
    // itself caused (its own action lines, the EARN line a trade
    // triggers, an opponent's defense) appear in the seat's NEXT summary.
    private readonly Dictionary<string, int> _summaryCursor = new(StringComparer.Ordinal);

    // Strike-pause plumbing: Run blocks on _hostSignal while paused; the
    // Host* pause controls stash the chosen action under _gate and set
    // the signal.
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _hostSignal = new(false);
    private HostAction? _hostAction;
    private volatile bool _endRequested;

    // Standing host controls: a command queue the loop drains
    // at decision boundaries, a signal that wakes the loop's host-side
    // waits when a command lands, and the host-pause flag the loop
    // honors between decisions.
    private readonly object _commandGate = new();
    private readonly List<HostCommand> _commandQueue = new();
    private readonly ManualResetEventSlim _commandSignal = new(false);
    private volatile bool _hostPauseRequested;

    // Telemetry state, guarded by _stateGate: the decision the table is
    // waiting on (its seat, number, kind, how long it has waited, the
    // replies rejected so far, and the exact prompt outstanding — a
    // poke re-sends it), and when each seat last replied.
    private readonly object _stateGate = new();
    private PendingState? _pending;
    private readonly Dictionary<string, DateTimeOffset> _lastReplyAt = new(StringComparer.Ordinal);

    // Suspension: a flag the loop honors at decision
    // boundaries and inside strike-pauses, exactly as it honors the
    // host pause — set by HostSuspend from any thread, acted on by the
    // loop thread, so the completed choice stream always ends at a
    // well-defined point (see FinishSuspended).
    private volatile bool _suspendRequested;

    // The guarded host edit: whether the loop is currently
    // inside RunHostPause (the only place an edit may be applied),
    // whether this pause has already spent its one edit, and whether
    // the game has ever been edited — the flag its record header and
    // every host surface carry for the rest of its life. The first two
    // are written by the loop thread and read by host threads (the
    // snapshot); the third also starts set in a resumed session whose
    // recorded game was edited.
    private volatile bool _inHostPause;
    private volatile bool _editUsedThisPause;
    private volatile bool _hostEdited;

    // Resume state: a session built over a ResumePlan starts
    // in replay: its channels are the plan's replay channels, its
    // writers are open over the suspended game's files in catch-up
    // mode, and every emit/append is held back until the replay
    // reaches the suspend boundary and GoLive swaps in the live
    // channels the host supplied. Null / false in an ordinary session.
    private readonly ResumePlan? _resume;
    private readonly ReplayDriver? _replayDriver;
    private readonly IReadOnlyDictionary<string, IAgentChannel> _liveChannels;
    private bool _replaying;

    private GameRecordWriter? _record;
    private GameTranscriptWriter? _transcript;
    private int _decisionsPresented;
    private int _decisionsResolved;
    private volatile SessionStatus _status = SessionStatus.NotStarted;

    /// <param name="board">The validated board the game is played on.</param>
    /// <param name="config">
    /// The session configuration: the game (mode, seats, seed, setup),
    /// the records directory, the listen endpoint, and the
    /// stall-warning interval.
    /// </param>
    /// <param name="channels">One channel per player seat, exactly the game's seats.</param>
    /// <param name="notifyHost">Host-facing notices (pauses, interventions, the result).</param>
    /// <param name="spectators">The spectator feed, when the host runs one.</param>
    /// <param name="seatConnector">
    /// The host's seat connector, when it has one. A session that can be
    /// told to reopen a seat over a socket needs it: the replacement's
    /// claim comes through the connector's accept pump.
    /// </param>
    public GameSession(
        BoardModel board,
        SessionConfig config,
        IReadOnlyDictionary<string, IAgentChannel> channels,
        Action<string>? notifyHost = null,
        SpectatorFeed? spectators = null,
        SocketSeatConnector? seatConnector = null)
        : this(board, config, channels, notifyHost, spectators, seatConnector, resume: null)
    {
    }

    /// <summary>
    /// Builds the session that resumes a suspended game. The
    /// session starts in replay: the plan's recorded choice stream is
    /// fed back through a fresh game, and when the replay reaches the
    /// suspend boundary the session goes live in the same record and
    /// transcript. The configuration must describe the suspended game
    /// (mode, seats, seed, setup — the plan's identity); how each seat
    /// is driven from the resume on is the host's current choice,
    /// carried by the configuration's seat kinds.
    /// </summary>
    /// <param name="liveChannels">
    /// Channels the host drives itself for individual seats (the app's
    /// own seat, a test's gated channel). Any seat not named here is
    /// built from its configured kind when the table goes live: the
    /// built-in scripted policy, or a socket seat reopened through
    /// <paramref name="seatConnector"/> — which is then required.
    /// </param>
    public static GameSession CreateResumed(
        BoardModel board,
        SessionConfig config,
        ResumePlan resume,
        IReadOnlyDictionary<string, IAgentChannel>? liveChannels = null,
        Action<string>? notifyHost = null,
        SpectatorFeed? spectators = null,
        SocketSeatConnector? seatConnector = null)
    {
        ArgumentNullException.ThrowIfNull(resume);
        return new GameSession(
            board, config,
            liveChannels ?? new Dictionary<string, IAgentChannel>(),
            notifyHost, spectators, seatConnector, resume);
    }

    private GameSession(
        BoardModel board,
        SessionConfig config,
        IReadOnlyDictionary<string, IAgentChannel> channels,
        Action<string>? notifyHost,
        SpectatorFeed? spectators,
        SocketSeatConnector? seatConnector,
        ResumePlan? resume)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _gameConfig = config.Game ?? throw new ArgumentNullException(nameof(config));
        _notifyHost = notifyHost;
        _spectators = spectators;
        _seatConnector = seatConnector;
        _resume = resume;

        // No channel may ever be built for the automated Rebel faction.
        if (channels.ContainsKey(RulesConstants.RebelCode))
            throw new ArgumentException("The Rebels are automated by the engine and never take a channel.", nameof(channels));
        var playerCodes = _gameConfig.PlayerCodes;
        if (resume is null)
        {
            // The channel set must be exactly the player seats: no missing player.
            if (channels.Count != playerCodes.Count || playerCodes.Any(code => !channels.ContainsKey(code)))
                throw new ArgumentException("Channels must cover exactly the game's player seats.", nameof(channels));
            _channels = new Dictionary<string, IAgentChannel>(channels, StringComparer.Ordinal);
            _liveChannels = _channels;
        }
        else
        {
            // A resumed session starts on replay channels; the channels
            // handed in are live channels for individual seats, used at
            // the boundary (any seat not handed in is built from its
            // configured kind there).
            if (channels.Keys.Any(code => !playerCodes.Contains(code)))
                throw new ArgumentException("Live channels may only name the game's player seats.", nameof(channels));
            var original = resume.OriginalConfig;
            if (_gameConfig.Mode != original.Mode || _gameConfig.Seed != original.Seed
                || _gameConfig.TerritorySetup != original.TerritorySetup
                || !_gameConfig.PlayerCodes.SequenceEqual(original.PlayerCodes))
                throw new ArgumentException(
                    "The session configuration does not describe the suspended game in the resume plan " +
                    "(mode, seats, seed, and setup must be the recorded game's).", nameof(config));
            foreach (var seat in _gameConfig.Seats)
            {
                if (seat.Kind == SeatKind.Socket && !channels.ContainsKey(seat.Code) && seatConnector is null)
                    throw new ArgumentException(
                        $"Seat {seat.Code} is to be driven over a socket from the resume on, " +
                        "but the session has no seat connector.", nameof(seatConnector));
            }
            _liveChannels = channels;
            _replayDriver = new ReplayDriver(resume.Stream);
            _channels = _replayDriver.Channels.ToDictionary(
                pair => pair.Key, pair => (IAgentChannel)pair.Value, StringComparer.Ordinal);
            _replaying = true;
        }
        _currentKinds = _gameConfig.Seats.ToDictionary(s => s.Code, s => s.Kind, StringComparer.Ordinal);
        _hostEdited = resume?.WasHostEdited ?? false;

        var boardData = BoardProjectBoardData.FromBoard(board);
        _prompts = new PromptBuilder(board);
        _scriptedPolicy = new ScriptedSeatPolicy(boardData);
        _game = new RiskGame(
            boardData,
            playerCodes,
            _gameConfig.Mode,
            new SeededRandom(_gameConfig.Seed),
            territorySetup: _gameConfig.TerritorySetup);
        foreach (string code in playerCodes) _summaryCursor[code] = 0;
    }

    /// <summary>The engine, exposed host-side for trusted tooling (the table view, tests).</summary>
    public RiskGame Game => _game;

    /// <summary>
    /// The replay under way in a resumed session, for tests
    /// and host auditing: its channels keep every prompt the replay
    /// generated, which is what the exactness checks compare against
    /// the original transcript. Null in a session that was not resumed.
    /// </summary>
    internal ReplayDriver? ActiveReplay => _replayDriver;

    public GameConfig Config => _gameConfig;

    public SessionStatus Status => _status;

    /// <summary>
    /// The game has been edited by the host at least once.
    /// The flag is permanent: it stands in the record header, and a
    /// game resumed from suspension carries it from the start.
    /// </summary>
    public bool HostEdited => _hostEdited;

    /// <summary>Why the session is paused, while <see cref="Status"/> is Paused; otherwise null.</summary>
    public PauseInfo? CurrentPause { get; private set; }

    // ------------------------------------------------------------------
    // The decision loop
    // ------------------------------------------------------------------

    /// <summary>Runs the game to completion (or a host ending) on the calling thread.</summary>
    public SessionResult Run()
    {
        if (Status != SessionStatus.NotStarted)
            throw new InvalidOperationException("A session runs once.");
        _status = SessionStatus.Running;

        if (_resume is not null)
        {
            // The resumed game continues the suspended game's own
            // files; both are opened in catch-up mode (nothing is
            // appended until the replay is verified at the boundary).
            // Unlike a fresh game, a resume cannot shrug off a lost
            // transcript: it is the artifact the replay is proving
            // itself against, so a failure to open either file fails
            // the run loudly, before anything is touched.
            _record = GameRecordWriter.OpenForResume(_resume.RecordPath);
            _transcript = GameTranscriptWriter.OpenForResume(_record.Path, Notify);
        }
        else
        {
            _record = GameRecordWriter.Create(_config.RecordDirectory, _gameConfig, _gameConfig.KindOf);
            try
            {
                _transcript = GameTranscriptWriter.Create(_record.Path, _gameConfig, _gameConfig.KindOf, Notify);
            }
            catch (Exception ex)
            {
                // The transcript is the secondary artifact: losing it must
                // never cost the game (or the record, the primary audit).
                Notify($"Transcript could not be started ({ex.Message}); the game continues without one. The game record is unaffected.");
            }
        }
        try
        {
            PublishBoard(); // the setup state, so watchers can render the table from the start
            SendBriefings();
            while (true)
            {
                if (_replaying) ApplyRecordedEditsDue();
                if (_replaying && _resume!.OpenDecision is null
                    && _decisionsResolved == _resume.AppliedDecisionCount)
                {
                    // The replay has rebuilt the recorded game up to
                    // the suspend boundary: verify it, go live.
                    GoLive(_decisionsPresented + 1);
                }
                DrainHostCommands(_decisionsPresented + 1);
                if (_endRequested)
                {
                    WriteHostIntervention("HOST END", GameTranscriptWriter.HostEndEntry());
                    return Finish(SessionStatus.EndedByHost, winner: null, "Ended by the host.");
                }
                if (_suspendRequested && _game.Snapshot().Winner is null)
                {
                    // Suspension takes effect at decision boundaries,
                    // so the completed choice stream always ends exactly
                    // after an applied decision. (A winner already on
                    // the board outranks a suspend that raced it.)
                    return FinishSuspended(_decisionsPresented + 1);
                }
                if (_hostPauseRequested)
                {
                    RunHostPause();
                    continue; // re-drain and re-check the end at the top
                }

                var decision = _game.PendingDecision();
                if (decision is null)
                {
                    if (_replaying)
                        throw new ResumeDivergedException(
                            "Replay diverged: the rebuilt game reached a winner before the recorded stream " +
                            "was exhausted, but the recorded game was suspended, so its stream cannot end in a win. " +
                            "The resume is abandoned; the suspended record and transcript are untouched.");
                    string winner = _game.Snapshot().Winner!;
                    SendGameOver(winner);
                    return Finish(SessionStatus.Completed, winner, endReason: null);
                }

                _decisionsPresented++;
                if (_decisionsPresented > HostConstants.MaxDecisionsPerGame)
                {
                    if (_replaying)
                        throw new ResumeDivergedException(
                            "Replay diverged: the recorded stream exceeds the decision cap. " +
                            "The resume is abandoned; the suspended record and transcript are untouched.");
                    return Finish(SessionStatus.EndedByHost, winner: null,
                        $"Decision cap of {HostConstants.MaxDecisionsPerGame} reached; the game was not converging.");
                }

                var outcome = ResolveDecision(decision, _decisionsPresented);
                if (outcome == DecisionOutcome.GameEnded)
                    return Finish(SessionStatus.EndedByHost, winner: null, "Ended by the host while paused.");
                if (outcome == DecisionOutcome.Suspended)
                    return FinishSuspended(_decisionsPresented);
                _decisionsResolved++;
            }
        }
        finally
        {
            _record.Dispose();
            _transcript?.Dispose();
            // Close what the session connected: a socket seat's stream goes
            // quiet when the game ends, so remote agents see the end too.
            // The host built every channel and stays responsible for any it
            // still holds; only the final set is closed here.
            foreach (var channel in ChannelSnapshot())
                if (channel is IDisposable disposable)
                    disposable.Dispose();
        }
    }

    /// <summary>
    /// The host pause: entered at a decision boundary when the
    /// host has asked for it, left when the host resumes or ends the
    /// game. While it lasts the loop issues no new decisions; queued
    /// host commands (seat replacements) are still applied, because
    /// fixing the table is what a pause is for.
    /// </summary>
    private void RunHostPause()
    {
        _inHostPause = true;
        _editUsedThisPause = false;
        try
        {
            _status = SessionStatus.HostPaused;
            WriteHostIntervention("HOST PAUSE", GameTranscriptWriter.HostPauseEntry());
            Notify("PAUSED BY HOST: the table is stopped between decisions. " +
                   "Resume it, replace a seat, edit the position, or end the game.");
            while (_hostPauseRequested && !_endRequested && !_suspendRequested)
            {
                _commandSignal.Wait();
                _commandSignal.Reset();
                DrainHostCommands(_decisionsPresented + 1);
            }
            if (_endRequested) return; // the loop top records HOST END and finishes
            if (_suspendRequested) return; // the loop top suspends; the table never resumed, so no HOST RESUME
            _status = SessionStatus.Running;
            WriteHostIntervention("HOST RESUME", GameTranscriptWriter.HostResumeEntry());
            Notify("Host resumed the game.");
        }
        finally
        {
            _inHostPause = false;
        }
    }

    private enum DecisionOutcome { Resolved, GameEnded, Suspended }

    /// <summary>
    /// Presents one decision to its seat and works the failure policy
    /// until the decision resolves, the seat is replaced, or
    /// the host ends the game.
    /// </summary>
    private DecisionOutcome ResolveDecision(PendingDecision decision, int decisionNumber)
    {
        string seat = decision.Seat;
        if (seat == RulesConstants.RebelCode)
            throw new InvalidOperationException("Engine contract breach: a decision was pended for the automated Rebels.");

        // Fixed for the whole exchange: re-prompts re-show the same summary,
        // and the seat's cursor later advances to exactly this point.
        var summaryLines = _game.Log.Skip(_summaryCursor[seat]).ToList();
        int summaryEnd = _game.Log.Count;
        var moves = PromptBuilder.LegalMoves(decision);

        BeginPending(seat, decisionNumber, decision.Kind);
        try
        {
            while (true) // re-entered when the host replaces the seat mid-decision
            {
                var channel = ChannelOf(seat);
                string? error = null;
                string? pauseReason = null;

                for (int attempt = 1; attempt <= HostConstants.MaxAttemptsPerDecision; attempt++)
                {
                    string prompt = attempt < HostConstants.MaxAttemptsPerDecision
                        ? _prompts.FullPrompt(_game, _game.Snapshot(), decision, decisionNumber, summaryLines, error)
                        : _prompts.StrikeThreePrompt(moves);

                    string? reply;
                    NotePendingPrompt(channel, prompt);
                    Emit(GameTranscriptWriter.PromptEntry(seat, decisionNumber, attempt, prompt));
                    try
                    {
                        reply = channel.Ask(prompt);
                    }
                    catch (Exception ex)
                    {
                        EmitStaleReplies(seat, decisionNumber, channel);
                        Emit(GameTranscriptWriter.ChannelFailedEntry(seat, "prompt", decisionNumber, ex.Message));
                        pauseReason = $"Seat {seat}'s channel failed: {ex.Message}";
                        break;
                    }
                    EmitStaleReplies(seat, decisionNumber, channel);
                    if (reply is null)
                    {
                        Emit(GameTranscriptWriter.NoReplyEntry(seat, decisionNumber));
                        pauseReason = $"Seat {seat} disconnected without answering decision D-{decisionNumber}.";
                        break;
                    }
                    Emit(GameTranscriptWriter.ReplyEntry(seat, decisionNumber, reply));
                    NoteReply(seat);

                    try
                    {
                        var choice = attempt < HostConstants.MaxAttemptsPerDecision
                            ? ReplyParser.Parse(decision, reply)
                            : ReplyParser.ParseNumberedMove(decision, reply, moves);
                        _game.ApplyDecision(choice);
                        _summaryCursor[seat] = summaryEnd;
                        AfterDecisionApplied();
                        return DecisionOutcome.Resolved;
                    }
                    catch (ReplyFormatException ex) { error = ex.Reason; }
                    catch (IllegalChoiceException ex) { error = ex.Reason; }
                    NoteFailedReply();
                }

                // Three strikes, or a dead channel: the game pauses for the host.
                var pause = PauseForHost(decision, decisionNumber, pauseReason ?? error!, moves, summaryEnd);
                if (pause == PauseOutcome.Ended) return DecisionOutcome.GameEnded;
                if (pause == PauseOutcome.Suspended) return DecisionOutcome.Suspended;
                if (pause == PauseOutcome.Substituted)
                {
                    AfterDecisionApplied();
                    return DecisionOutcome.Resolved;
                }
                // PauseOutcome.Replaced: loop and present the decision afresh
                // to the replacement seat, strikes reset, summary intact.
                BeginPending(seat, decisionNumber, decision.Kind);
            }
        }
        finally
        {
            EndPending(decisionNumber);
        }
    }

    private enum PauseOutcome { Substituted, Replaced, Ended, Suspended }

    /// <summary>
    /// Halts the decision loop and waits for a host action. The state the
    /// host needs (seat, decision, reason, legal moves) is published on
    /// <see cref="CurrentPause"/> and pushed through the notify callback.
    /// A standing replacement command already queued for the paused
    /// seat — the usual case when the host replaced a wedged seat and
    /// the interruption landed here — resolves the pause at once.
    /// </summary>
    private PauseOutcome PauseForHost(
        PendingDecision decision, int decisionNumber, string reason,
        IReadOnlyList<string> moves, int summaryEnd)
    {
        string seat = decision.Seat;
        _status = SessionStatus.Paused;
        CurrentPause = new PauseInfo(seat, decision, decisionNumber, reason, moves);
        if (_replaying)
        {
            // A pause during a replay is the recorded pause happening
            // again; the recorded stream says how it was resolved.
            return ResolveReplayPause(decision, decisionNumber, moves, summaryEnd);
        }
        Emit(GameTranscriptWriter.PausedEntry(seat, decisionNumber, reason));
        Notify($"PAUSED: seat {seat} cannot answer decision D-{decisionNumber} ({decision.Kind}). " +
               $"Reason: {reason}{(reason.EndsWith('.') ? "" : ".")} " +
               "The game is paused for the host: substitute a move, replace the seat, or end the game.");

        // A suspend requested while the strike-pause stands takes
        // effect from the pause: the decision is left unanswered, and
        // the resume presents it again. (A replacement already queued
        // does not outrank it here; the host asked to stop the game.)
        if (_suspendRequested) return PauseOutcome.Suspended;

        if (DrainHostCommands(decisionNumber, seat))
        {
            DropStaleHostAction();
            _status = SessionStatus.Running;
            CurrentPause = null;
            Notify($"Seat {seat} replaced; the game resumes.");
            return PauseOutcome.Replaced;
        }

        while (true)
        {
            WaitHandle.WaitAny(new WaitHandle[] { _hostSignal.WaitHandle, _commandSignal.WaitHandle });
            if (_commandSignal.IsSet)
            {
                _commandSignal.Reset();
                if (DrainHostCommands(decisionNumber, seat))
                {
                    DropStaleHostAction();
                    _status = SessionStatus.Running;
                    CurrentPause = null;
                    Notify($"Seat {seat} replaced; the game resumes.");
                    return PauseOutcome.Replaced;
                }
            }
            if (_suspendRequested) return PauseOutcome.Suspended;
            if (!_hostSignal.IsSet) continue;

            HostAction action;
            lock (_gate)
            {
                action = _hostAction!;
                _hostAction = null;
                _hostSignal.Reset();
            }

            switch (action)
            {
                case HostAction.End:
                    WriteHostIntervention("HOST END", GameTranscriptWriter.HostEndEntry());
                    return PauseOutcome.Ended;

                case HostAction.Replace replace when replace.SeatCode == seat:
                    PerformReplacement(seat, replace.Channel, InferKind(replace.Channel), decisionNumber, brief: true);
                    DropQueuedReplacementsFor(seat);
                    _status = SessionStatus.Running;
                    CurrentPause = null;
                    Notify($"Seat {seat} replaced; the game resumes.");
                    return PauseOutcome.Replaced;

                case HostAction.Replace replace:
                    // Some other seat's channel was swapped while paused;
                    // the pause itself is unchanged, so keep waiting.
                    PerformReplacement(replace.SeatCode, replace.Channel, InferKind(replace.Channel), decisionNumber, brief: false);
                    DropQueuedReplacementsFor(replace.SeatCode);
                    Notify($"Seat {replace.SeatCode} replaced (the pause on seat {seat} stands).");
                    break;

                case HostAction.Substitute substitute:
                    try
                    {
                        var choice = ReplyParser.Parse(decision, moves[substitute.MoveNumber - 1]);
                        // The record line lands before the decision's own
                        // events; the transcript's HOST SUBSTITUTE entry
                        // keeps its established place after the apply.
                        WriteHostIntervention(
                            $"HOST SUBSTITUTE {seat} D-{decisionNumber} move={substitute.MoveNumber}",
                            transcriptEntry: null);
                        _game.ApplyDecision(choice);
                        Emit(GameTranscriptWriter.HostSubstituteEntry(
                            seat, decisionNumber, substitute.MoveNumber, moves[substitute.MoveNumber - 1]));
                        _summaryCursor[seat] = summaryEnd;
                        _status = SessionStatus.Running;
                        CurrentPause = null;
                        Notify($"Host substituted move {substitute.MoveNumber} for seat {seat}; the game resumes.");
                        return PauseOutcome.Substituted;
                    }
                    catch (IllegalChoiceException ex)
                    {
                        // Should not happen (the list is legal by construction),
                        // but a rejected substitute must never unpause wrongly.
                        Notify($"Substitute rejected: {ex.Reason} The game stays paused.");
                        break;
                    }
            }
        }
    }

    // ------------------------------------------------------------------
    // Replay resolution and the go-live boundary
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves a strike-pause the replay has re-entered, from the
    /// recorded stream: the same substitute the host chose, the same
    /// replacement (the seat's replay channel simply answers on), or —
    /// for the decision a pause-suspension left open — the suspend
    /// boundary itself, where the table goes live and the decision is
    /// presented again to the live seat. Everything the recorded flow
    /// wrote is held back by the replay guards, so the file gains
    /// nothing for a span it already holds.
    /// </summary>
    private PauseOutcome ResolveReplayPause(
        PendingDecision decision, int decisionNumber,
        IReadOnlyList<string> moves, int summaryEnd)
    {
        string seat = decision.Seat;
        var resolution = _replayDriver!.NextResolution(decisionNumber);
        switch (resolution)
        {
            case RecordedResolution.Substitute substitute:
            {
                if (substitute.MoveNumber < 1 || substitute.MoveNumber > moves.Count
                    || moves[substitute.MoveNumber - 1] != substitute.MoveText)
                    throw new ResumeDivergedException(
                        $"Replay diverged at decision D-{decisionNumber}: the recorded substitute " +
                        $"(move {substitute.MoveNumber}, '{substitute.MoveText}') does not match the replay's " +
                        "enumerated moves. The resume is abandoned; the suspended record and transcript are untouched.");
                var choice = ReplyParser.Parse(decision, moves[substitute.MoveNumber - 1]);
                _game.ApplyDecision(choice);
                _summaryCursor[seat] = summaryEnd;
                _status = SessionStatus.Running;
                CurrentPause = null;
                return PauseOutcome.Substituted;
            }
            case RecordedResolution.Replaced:
                // The replacement the host chose then is answered now
                // by the seat's replay channel; PerformReplacement's
                // writes are held back by the replay guards.
                PerformReplacement(seat, _replayDriver.Channels[seat], SeatKind.Scripted, decisionNumber, brief: true);
                _status = SessionStatus.Running;
                CurrentPause = null;
                return PauseOutcome.Replaced;
            case RecordedResolution.Suspended:
                // The pause the game was suspended from: the replay has
                // arrived. Go live in this same pause and let the
                // decision be presented again, to the live seat.
                GoLive(decisionNumber);
                _status = SessionStatus.Running;
                CurrentPause = null;
                return PauseOutcome.Replaced;
            default:
                throw new ResumeDivergedException(
                    $"Replay diverged at decision D-{decisionNumber}: an unrecorded pause resolution. " +
                    "The resume is abandoned; the suspended record and transcript are untouched.");
        }
    }

    /// <summary>
    /// Applies the recorded host edits that stand before the
    /// decision the replay is about to present, through the engine's
    /// edit entry point — engine state only: the record lines, the
    /// transcript entries and the seat notices already stand in the
    /// files from the original span, and the writers are catching up.
    /// The repairs the edit performs (a called-off attack's held round
    /// line, above all) re-derive the same engine events the original
    /// edit produced, which is what the go-live verification counts.
    /// An edit the engine refuses on replay means the rebuilt game is
    /// not the recorded game: divergence, and the resume is abandoned.
    /// </summary>
    private void ApplyRecordedEditsDue()
    {
        int nextDecision = _decisionsPresented + 1;
        while (_replayDriver!.TryTakeEditBefore(nextDecision, out var edit))
        {
            try
            {
                _game.ApplyHostEdit(edit.Edits);
            }
            catch (HostEditRefusedException ex)
            {
                throw new ResumeDivergedException(
                    $"Replay diverged at the host edit before decision D-{nextDecision}: " +
                    $"the recorded edit is refused on replay ({ex.Reason}). " +
                    "The resume is abandoned; the suspended record and transcript are untouched.");
            }
            _hostEdited = true;
        }
    }

    /// <summary>
    /// The suspend boundary, reached by the replay: first verify the
    /// rebuilt game IS the recorded game (its event and dice counts
    /// equal the record's, and the recorded stream is fully consumed),
    /// then catch the writers up, swap in the live channels, write the
    /// HOST RESUME join into both artifacts, and re-brief every seat.
    /// Nothing is appended to either file before the verification
    /// passes, so a diverged replay leaves the suspended game exactly
    /// as it was.
    /// </summary>
    private void GoLive(int resumeDecisionNumber)
    {
        if (_game.Log.Count != _resume!.RecordEventCount || _game.DiceRolls.Count != _resume.RecordDiceCount)
            throw new ResumeDivergedException(
                $"Replay diverged from the suspended record: the rebuilt game produced {_game.Log.Count} event(s) " +
                $"and {_game.DiceRolls.Count} dice record(s), but the record holds {_resume.RecordEventCount} " +
                $"and {_resume.RecordDiceCount}. The resume is abandoned before anything was appended; " +
                "the suspended record and transcript are untouched.");
        if (_replayDriver is not null && !_replayDriver.Exhausted)
            throw new ResumeDivergedException(
                "Replay diverged: the recorded stream still holds unconsumed choices at the suspend boundary. " +
                "The resume is abandoned before anything was appended; the suspended record and transcript are untouched.");

        _record!.CatchUp(_game);
        _transcript?.CatchUp();
        if (_hostEdited) _record.MarkHostEdited(); // patches the header flag if the record predates it
        _replaying = false;

        foreach (string code in _gameConfig.PlayerCodes)
        {
            // Replacements during the replay recorded their kinds as
            // they went; the live table's kinds are the host's resume
            // configuration, from here on.
            SetCurrentKind(code, _gameConfig.KindOf(code));
            SwapChannel(code, BuildLiveChannel(code));
        }

        string context =
            $"resumed by the host; {_resume.AppliedDecisionCount} decision(s) replayed from the transcript; " +
            $"seats now driven as: {string.Join(" ", _gameConfig.Seats.Select(s => $"{s.Code} ({s.Kind.ToString().ToLowerInvariant()})"))}";
        WriteHostIntervention(
            $"HOST RESUME D-{resumeDecisionNumber}",
            GameTranscriptWriter.HostResumeEntry(resumeDecisionNumber, context));

        foreach (var (code, channel) in ChannelPairsSnapshot())
        {
            string briefing = _prompts.Briefing(code, _gameConfig);
            Emit(GameTranscriptWriter.ResumeBriefingEntry(code, briefing));
            try { channel.Send(briefing); }
            catch (Exception ex)
            {
                Emit(GameTranscriptWriter.ChannelFailedEntry(code, "briefing", null, ex.Message));
                /* a dead live channel surfaces at its first prompt, through the pause */
            }
        }
        PublishBoard();
        Notify($"Game resumed from suspension at decision D-{resumeDecisionNumber}: " +
               $"{_resume.AppliedDecisionCount} decisions replayed; the table is live.");
    }

    /// <summary>
    /// The live channel for one seat at the resume boundary: the
    /// host's own channel when it supplied one for the seat, otherwise
    /// built from the seat's configured kind — the built-in scripted
    /// policy, or a socket seat reopened through the connector (whose
    /// presence the constructor required for such seats).
    /// </summary>
    private IAgentChannel BuildLiveChannel(string seat)
    {
        if (_liveChannels.TryGetValue(seat, out var custom)) return custom;
        return _gameConfig.KindOf(seat) switch
        {
            SeatKind.Scripted => new DelegateAgentChannel($"scripted seat {seat}", _scriptedPolicy.Respond),
            SeatKind.Socket => new PendingJoinChannel(seat, _seatConnector!),
            _ => throw new InvalidOperationException($"Unknown seat kind for {seat}."),
        };
    }

    /// <summary>
    /// Closes the game as suspended: the HOST SUSPEND lines
    /// land in both artifacts, the result sections record the
    /// suspension (the decision play stopped at, no winner), and the
    /// writers close over a complete-up-to-the-boundary record and
    /// transcript. <paramref name="decisionNumber"/> is the decision
    /// play resumes at: the next one at a boundary, or the unanswered
    /// one when the suspension came out of a strike-pause — the
    /// presented/resolved counters say which (see the class remarks).
    /// </summary>
    private SessionResult FinishSuspended(int decisionNumber)
    {
        RefuseQueuedEdits();
        _status = SessionStatus.Suspended;
        CurrentPause = null;
        bool decisionOpen = _decisionsPresented > _decisionsResolved;
        string reason = $"Suspended by the host at decision D-{decisionNumber}.";
        var result = new SessionResult(SessionStatus.Suspended, null, _decisionsResolved, _record!.Path, reason);
        WriteHostIntervention(
            $"HOST SUSPEND D-{decisionNumber}",
            GameTranscriptWriter.HostSuspendEntry(decisionNumber, decisionOpen));
        _record.Finish(result);
        Emit(GameTranscriptWriter.ResultEntry(result));
        Notify($"Game suspended by the host at decision D-{decisionNumber}. Record: {result.RecordPath}");
        return result;
    }

    // ------------------------------------------------------------------
    // Host controls: the strike-pause controls (act while Paused)
    // ------------------------------------------------------------------

    /// <summary>Substitutes the n-th enumerated legal move for the paused seat's decision.</summary>
    public void HostSubstitute(int moveNumber)
    {
        lock (_gate)
        {
            var pause = RequirePaused();
            if (moveNumber < 1 || moveNumber > pause.LegalMoves.Count)
                throw new ArgumentOutOfRangeException(nameof(moveNumber),
                    $"Move number must be between 1 and {pause.LegalMoves.Count}.");
            _hostAction = new HostAction.Substitute(moveNumber);
            _hostSignal.Set();
        }
    }

    /// <summary>
    /// Replaces a seat's channel with a ready-made one, while the game
    /// sits in a strike-pause. Replacing the paused seat resumes the
    /// game (the pending decision is presented to the new channel,
    /// strikes reset); replacing any other seat takes effect at its
    /// next decision and the pause stands. For a replacement by kind —
    /// including while the game is running — use
    /// <see cref="HostReplaceSeatKind"/>.
    /// </summary>
    public void HostReplaceSeat(string seatCode, IAgentChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            var pause = RequirePaused();
            if (!HasChannel(seatCode))
                throw new ArgumentException($"'{seatCode}' is not a seat in this game.", nameof(seatCode));
            if (seatCode == pause.Seat)
            {
                _hostAction = new HostAction.Replace(seatCode, channel);
                _hostSignal.Set();
            }
            else
            {
                PerformReplacement(seatCode, channel, InferKind(channel), pause.DecisionNumber, brief: false);
            }
        }
    }

    /// <summary>
    /// Ends the game at the host's word. From a pause, immediately; while
    /// running (host-paused included), at the next decision boundary (a
    /// seat mid-thought finishes its current reply first).
    /// </summary>
    public void HostEndGame()
    {
        lock (_gate)
        {
            if (Status is SessionStatus.Completed or SessionStatus.EndedByHost)
                throw new InvalidOperationException("The game is already over.");
            if (Status == SessionStatus.Paused)
            {
                _hostAction = new HostAction.End();
                _hostSignal.Set();
            }
            else
            {
                _endRequested = true;
                _commandSignal.Set(); // wake a host-pause wait, if that is where the loop sits
            }
        }
    }

    private PauseInfo RequirePaused()
    {
        if (Status != SessionStatus.Paused || CurrentPause is null)
            throw new InvalidOperationException("Host decision controls require a paused game.");
        return CurrentPause;
    }

    private abstract record HostAction
    {
        public sealed record Substitute(int MoveNumber) : HostAction;
        public sealed record Replace(string SeatCode, IAgentChannel Channel) : HostAction;
        public sealed record End : HostAction;
    }

    // ------------------------------------------------------------------
    // Host controls: the standing controls
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-sends the seat's currently pending prompt, unchanged (a poke):
    /// no attempt or strike is consumed and no state changes; the
    /// seat's outstanding read collects its answer, whichever sending
    /// it answers. A seat that answers both copies has answered once —
    /// the duplicate is discarded under the stale-reply rule.
    /// The poke is recorded (record: HOST POKE; transcript:
    /// ### HOST POKE) at the moment it is sent.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The named seat is not the seat the table is currently waiting on,
    /// or it has not joined yet and has no live prompt to re-send.
    /// </exception>
    public void HostPoke(string seatCode)
    {
        PendingState pending;
        lock (_stateGate)
        {
            if (Status != SessionStatus.Running || _pending is null || _pending.Seat != seatCode
                || _pending.PromptText.Length == 0 || _pending.Channel is null)
                throw new InvalidOperationException($"Seat {seatCode} is not the seat the table is waiting on.");
            pending = _pending;
        }
        if (pending.Channel is PendingJoinChannel join && !join.HasJoined)
            throw new InvalidOperationException($"Seat {seatCode} has not joined yet; there is no live prompt to re-send.");
        WriteHostIntervention(
            $"HOST POKE {seatCode} D-{pending.DecisionNumber}",
            GameTranscriptWriter.HostPokeEntry(seatCode, pending.DecisionNumber));
        pending.Channel.Resend(pending.PromptText);
        Notify($"Host poked seat {seatCode}: the prompt for decision D-{pending.DecisionNumber} was re-sent.");
    }

    /// <summary>
    /// Sets how a seat is driven, as a standing control: the change is
    /// queued and takes effect at the seat's next decision boundary,
    /// board state untouched. Scripted seats are driven by the host's
    /// built-in policy; a seat set to socket is reopened and claims a
    /// new client through the session's connector (the session must
    /// have been built with one).
    ///
    /// If the table is currently waiting on this seat, its outstanding
    /// exchange is interrupted — a closable channel (a socket) is
    /// closed, the exchange fails, and the queued replacement resolves
    /// the strike-pause that follows by re-presenting the unanswered
    /// decision to the new channel. An in-process channel cannot be
    /// interrupted; its current answer lands first and the replacement
    /// takes the seat from the decision after.
    /// </summary>
    public void HostReplaceSeatKind(string seatCode, SeatKind kind)
    {
        if (Status is SessionStatus.NotStarted or SessionStatus.Completed or SessionStatus.EndedByHost)
            throw new InvalidOperationException("Host seat controls require a game in progress.");
        if (!HasChannel(seatCode))
            throw new ArgumentException($"'{seatCode}' is not a seat in this game.", nameof(seatCode));
        if (kind == SeatKind.Socket && _seatConnector is null)
            throw new InvalidOperationException(
                "This session has no socket connector; a seat cannot be reopened over a socket.");

        EnqueueHostCommand(new HostCommand.ReplaceKind(seatCode, kind));

        // Interrupt the outstanding exchange if the table is waiting on
        // this seat: closing its channel fails the pending read, and
        // the loop reaches the replacement through the strike-pause.
        PendingState? pending;
        lock (_stateGate) pending = _pending;
        if (Status == SessionStatus.Running && pending is not null && pending.Seat == seatCode)
            (pending.Channel as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Asks the loop to stop at the next decision boundary: no new
    /// decisions are issued while the table is host-paused; a decision
    /// in flight completes, and a strike-pause in progress concludes
    /// first. Recorded (HOST PAUSE / ### HOST PAUSE) when the pause
    /// takes effect.
    /// </summary>
    public void HostPause()
    {
        if (Status is SessionStatus.NotStarted or SessionStatus.Completed or SessionStatus.EndedByHost)
            throw new InvalidOperationException("Host pause requires a game in progress.");
        _hostPauseRequested = true;
    }

    /// <summary>Releases a host pause. Recorded (HOST RESUME / ### HOST RESUME) when play resumes.</summary>
    public void HostResume()
    {
        if (!_hostPauseRequested && Status != SessionStatus.HostPaused)
            throw new InvalidOperationException("The table is not host-paused.");
        _hostPauseRequested = false;
        _commandSignal.Set();
    }

    /// <summary>
    /// Suspends the game: the session closes its record and
    /// transcript in a suspended state — no winner, the decision play
    /// stopped at named — and <see cref="Run"/> returns a
    /// <see cref="SessionStatus.Suspended"/> result. The suspended game
    /// can later be resumed exactly, by replaying its transcript's
    /// choice stream (see <see cref="CreateResumed"/>).
    ///
    /// Like the host pause, the request is honored by the loop at a
    /// decision boundary, so the completed choice stream always ends
    /// at a well-defined point: at the next boundary, when the decision
    /// in flight resolves first; or from inside a strike-pause, where
    /// the standing decision is left unanswered and the resume
    /// presents it again. A decision the seat is still thinking about
    /// is never cut off mid-thought, and a game that reaches a winner
    /// first completes instead.
    /// </summary>
    public void HostSuspend()
    {
        if (Status is SessionStatus.NotStarted or SessionStatus.Completed
            or SessionStatus.EndedByHost or SessionStatus.Suspended)
            throw new InvalidOperationException("Host suspend requires a game in progress.");
        _suspendRequested = true;
        _commandSignal.Set(); // wake a host-pause wait, if that is where the loop sits
    }

    /// <summary>
    /// Applies a guarded host edit to the position: the closed
    /// set only — a territory's owner (a seat of this game; the Rebels
    /// in Duo) and a territory's army count (never below one). Cards,
    /// the deck, the dice and the turn order cannot be edited, by
    /// construction of <see cref="TerritoryEdit"/> and by the engine's
    /// own validation of the set.
    ///
    /// An edit is possible only while the table stands host-paused,
    /// between decisions: the request is queued and the loop applies
    /// it inside the pause, atomically, through the engine's one
    /// audited entry point (<see cref="RiskGame.ApplyHostEdit"/>); the
    /// next decision is then presented fresh, from the edited
    /// position. One edit is applied per pause — after one lands, the
    /// host resumes and pauses again for another.
    ///
    /// The returned task always completes with a
    /// <see cref="HostEditOutcome"/>: Applied, carrying the changes and
    /// the notes the record carries, or Refused, carrying the
    /// plain-language reason (not host-paused, this pause's edit
    /// already used, the game over, or the engine's own refusal of an
    /// edit outside the closed set). It never faults, and a refusal
    /// changes nothing.
    /// </summary>
    public Task<HostEditOutcome> HostApplyEditAsync(IReadOnlyList<TerritoryEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (Status != SessionStatus.HostPaused)
            return Task.FromResult<HostEditOutcome>(new HostEditOutcome.Refused(
                "The table is not host-paused; an edit can only be applied while the host has the game paused."));
        if (_game.PendingDecision() is null)
            return Task.FromResult<HostEditOutcome>(new HostEditOutcome.Refused(
                "The game is over; its final position cannot be edited."));
        var completion = new TaskCompletionSource<HostEditOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueHostCommand(new HostCommand.Edit(edits, completion));
        return completion.Task;
    }

    /// <summary>
    /// The table's standing right now, for any host surface: the
    /// session's state and one snapshot per seat (kind, connection,
    /// time since its last reply, strikes and the pending decision when
    /// the table waits on it, and the stall flag). Safe to call from
    /// any thread at any time; reading it never touches the game.
    /// </summary>
    public SessionSnapshot GetSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        PendingState? pending;
        Dictionary<string, DateTimeOffset> lastReplies;
        lock (_stateGate)
        {
            pending = _pending;
            lastReplies = new Dictionary<string, DateTimeOffset>(_lastReplyAt, StringComparer.Ordinal);
        }

        var seats = new List<SeatSnapshot>(_gameConfig.PlayerCodes.Count);
        foreach (string code in _gameConfig.PlayerCodes)
        {
            bool waiting = pending is not null && pending.Seat == code;
            bool stalled = waiting
                           && Status == SessionStatus.Running
                           && now - pending!.Since > TimeSpan.FromSeconds(_config.StallWarningSeconds);
            seats.Add(new SeatSnapshot(
                code,
                CurrentKindOf(code),
                ConnectionOf(ChannelOf(code)),
                lastReplies.TryGetValue(code, out var repliedAt) ? now - repliedAt : null,
                waiting ? pending!.FailedReplies : 0,
                waiting ? pending!.DecisionNumber : null,
                waiting ? pending!.Kind : null,
                stalled));
        }
        return new SessionSnapshot(Status, seats, _hostEdited,
            Status == SessionStatus.HostPaused && _editUsedThisPause);
    }

    private static SeatConnection ConnectionOf(IAgentChannel channel) => channel switch
    {
        SocketAgentChannel socket => socket.Disconnected ? SeatConnection.Disconnected : SeatConnection.Connected,
        PendingJoinChannel join => join.InnerOrNull is { } inner ? ConnectionOf(inner) : SeatConnection.AwaitingJoin,
        _ => SeatConnection.InProcess,
    };

    private static SeatKind InferKind(IAgentChannel channel) =>
        channel is SocketAgentChannel or PendingJoinChannel ? SeatKind.Socket : SeatKind.Scripted;

    private static string KindToken(SeatKind kind) => kind == SeatKind.Socket ? "socket" : "script";

    // ------------------------------------------------------------------
    // The standing-command queue and the replacement machinery
    // ------------------------------------------------------------------

    private abstract record HostCommand
    {
        public sealed record ReplaceKind(string SeatCode, SeatKind Kind) : HostCommand;

        /// <summary>
        /// A guarded host edit, completed by the loop with
        /// its outcome: edits are applied by the loop thread, inside
        /// the host pause, and from nowhere else.
        /// </summary>
        public sealed record Edit(
            IReadOnlyList<TerritoryEdit> Edits,
            TaskCompletionSource<HostEditOutcome> Completion) : HostCommand;
    }

    private void EnqueueHostCommand(HostCommand command)
    {
        lock (_commandGate) _commandQueue.Add(command);
        _commandSignal.Set();
    }

    private List<HostCommand> TakeHostCommands()
    {
        lock (_commandGate)
        {
            if (_commandQueue.Count == 0) return new List<HostCommand>();
            var taken = new List<HostCommand>(_commandQueue);
            _commandQueue.Clear();
            return taken;
        }
    }

    /// <summary>
    /// Applies every queued host command. Returns true when a queued
    /// replacement targeted <paramref name="strikePausedSeat"/> (the
    /// seat a strike-pause is being entered for), which the caller
    /// treats as the pause resolving by replacement.
    /// </summary>
    /// <param name="decisionNumberForRecord">
    /// The decision the record lines name: the decision in progress at
    /// a strike-pause, the next decision at an open boundary.
    /// </param>
    private bool DrainHostCommands(int decisionNumberForRecord, string? strikePausedSeat = null)
    {
        bool pausedSeatReplaced = false;
        foreach (var command in TakeHostCommands())
        {
            switch (command)
            {
                case HostCommand.ReplaceKind replace:
                    PerformReplacement(
                        replace.SeatCode,
                        BuildReplacementChannel(replace.SeatCode, replace.Kind),
                        replace.Kind,
                        decisionNumberForRecord,
                        brief: true);
                    Notify($"Seat {replace.SeatCode} is now driven as {KindToken(replace.Kind)} (host replacement).");
                    if (replace.SeatCode == strikePausedSeat) pausedSeatReplaced = true;
                    break;
                case HostCommand.Edit edit:
                    ApplyEditCommand(edit);
                    break;
            }
        }
        return pausedSeatReplaced;
    }

    /// <summary>
    /// Applies one queued host edit, or refuses it. The loop
    /// drains commands in several places, but an edit may only take
    /// effect inside the host pause: drained anywhere else (a boundary
    /// the pause already left, a strike-pause), it is refused as
    /// stale, because the position it was written against is gone.
    /// One edit per pause: a second request in the same pause is
    /// refused, and a refusal never spends the pause's edit — only an
    /// applied edit does.
    /// </summary>
    private void ApplyEditCommand(HostCommand.Edit command)
    {
        if (!_inHostPause)
        {
            command.Completion.TrySetResult(new HostEditOutcome.Refused(
                "The table is no longer host-paused; the edit was not applied."));
            return;
        }
        if (_editUsedThisPause)
        {
            command.Completion.TrySetResult(new HostEditOutcome.Refused(
                "This pause's edit has already been used; the edit was not applied. " +
                "Resume the game and pause again for another edit."));
            return;
        }
        if (_game.PendingDecision() is null)
        {
            command.Completion.TrySetResult(new HostEditOutcome.Refused(
                "The game is over; its final position cannot be edited."));
            return;
        }
        HostEditResult result;
        try
        {
            result = _game.ApplyHostEdit(command.Edits);
        }
        catch (HostEditRefusedException ex)
        {
            command.Completion.TrySetResult(new HostEditOutcome.Refused(ex.Reason));
            return;
        }
        _editUsedThisPause = true;
        RecordEdit(result);
        command.Completion.TrySetResult(new HostEditOutcome.Applied(result));
    }

    /// <summary>
    /// Records an applied host edit as the first-class record
    /// citizen it is: the record's header flag is patched, one HOST
    /// EDIT line per changed territory and one HOST EDIT NOTE line per
    /// note land in the game record, the transcript carries the same
    /// entries, and every seat is told out of band (its next prompt's
    /// summary stays the engine's own lines — the edit is announced,
    /// never folded into the game's event log). The pending decision,
    /// if any stood, is not disturbed here: an edit can only land
    /// between decisions, and the next decision is presented fresh from
    /// the edited position when the host resumes.
    /// </summary>
    private void RecordEdit(HostEditResult result)
    {
        _hostEdited = true;
        _record?.MarkHostEdited();
        foreach (var change in result.Changes)
            WriteHostIntervention(
                $"HOST EDIT {change.Territory} {GameTranscriptWriter.HostEditParts(change)}",
                GameTranscriptWriter.HostEditEntry(change));
        foreach (string note in result.Notes)
            WriteHostIntervention(
                $"HOST EDIT NOTE {note}",
                GameTranscriptWriter.HostEditNoteEntry(note));
        AnnounceEdit(result);
        Notify("Host edited the position; the edit stands in the record as HOST EDIT lines, " +
               "and the game carries the host-edited flag from here on.");
    }

    /// <summary>
    /// Tells every seat its table was edited: the changes and
    /// the notes, in the record's own words, as a block that expects
    /// no reply — the briefing and game-over precedent. A seat that
    /// cannot be told shows itself at its next prompt, as everywhere.
    /// </summary>
    private void AnnounceEdit(HostEditResult result)
    {
        var lines = new List<string>
        {
            "HOST EDIT",
            "The host edited the position:",
        };
        lines.AddRange(result.Changes.Select(change =>
            $"{change.Territory} {GameTranscriptWriter.HostEditParts(change)}"));
        lines.AddRange(result.Notes);
        lines.Add("Play continues from the edited position.");
        string text = string.Join("\n", lines);
        foreach (var (code, channel) in ChannelPairsSnapshot())
        {
            Emit(GameTranscriptWriter.HostEditNoticeEntry(code, text));
            try { channel.Send(text); }
            catch (Exception ex)
            {
                Emit(GameTranscriptWriter.ChannelFailedEntry(code, "host-edit", null, ex.Message));
                /* a dead channel surfaces at its first prompt, through the pause */
            }
        }
    }

    /// <summary>
    /// Answers any edit request still queued when the game closes:
    /// refused, never left hanging. The boundary drains normally
    /// apply or refuse every queued edit before the game can finish,
    /// so this is the net for a request that raced the closing.
    /// </summary>
    private void RefuseQueuedEdits()
    {
        foreach (var command in TakeHostCommands())
            if (command is HostCommand.Edit edit)
                edit.Completion.TrySetResult(new HostEditOutcome.Refused(
                    "The game ended before the edit could be applied."));
    }

    /// <summary>
    /// Drops a pause-path host action made stale by a replacement that
    /// resolved the same pause through the command queue: a substitute
    /// aimed at the old seat's decision must never fire inside a later
    /// pause.
    /// </summary>
    private void DropStaleHostAction()
    {
        lock (_gate)
        {
            _hostAction = null;
            _hostSignal.Reset();
        }
    }

    /// <summary>Drops queued kind-replacements for a seat a pause-path replace has already handled.</summary>
    private void DropQueuedReplacementsFor(string seatCode)
    {
        lock (_commandGate)
            _commandQueue.RemoveAll(c => c is HostCommand.ReplaceKind replace && replace.SeatCode == seatCode);
    }

    private IAgentChannel BuildReplacementChannel(string seatCode, SeatKind kind) => kind switch
    {
        SeatKind.Scripted => new DelegateAgentChannel($"scripted seat {seatCode}", _scriptedPolicy.Respond),
        SeatKind.Socket => _seatConnector is not null
            ? new PendingJoinChannel(seatCode, _seatConnector)
            : throw new InvalidOperationException(
                "This session has no socket connector; a seat cannot be reopened over a socket."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// A seat replacement's effects, shared by every path that performs
    /// one (pause-path channel swaps, standing kind replacements, a
    /// queued replacement found at a strike-pause): the channel is
    /// swapped, the seat's current kind updated, and the intervention
    /// recorded in the record and the transcript. The replacement is
    /// briefed like any seat joining the game — except on the legacy
    /// pause path for a seat other than the paused one, which has never
    /// briefed and keeps its established shape.
    /// </summary>
    private void PerformReplacement(string seat, IAgentChannel channel, SeatKind kind, int decisionNumber, bool brief)
    {
        SwapChannel(seat, channel);
        SetCurrentKind(seat, kind);
        WriteHostIntervention(
            $"HOST REPLACE {seat} {KindToken(kind)} D-{decisionNumber}",
            GameTranscriptWriter.HostReplaceEntry(seat));
        if (!brief) return;
        string briefing = _prompts.Briefing(seat, _gameConfig);
        Emit(GameTranscriptWriter.BriefingEntry(seat, briefing));
        try { channel.Send(briefing); }
        catch (Exception ex)
        {
            Emit(GameTranscriptWriter.ChannelFailedEntry(seat, "briefing", null, ex.Message));
            /* a dead replacement shows itself at its first prompt */
        }
    }

    // ------------------------------------------------------------------
    // Channels, kinds, and the pending-decision state
    // ------------------------------------------------------------------

    private IAgentChannel ChannelOf(string seat)
    {
        lock (_channelsGate) return _channels[seat];
    }

    private bool HasChannel(string seat)
    {
        lock (_channelsGate) return _channels.ContainsKey(seat);
    }

    private void SwapChannel(string seat, IAgentChannel channel)
    {
        lock (_channelsGate) _channels[seat] = channel;
    }

    private IReadOnlyList<IAgentChannel> ChannelSnapshot()
    {
        lock (_channelsGate) return _channels.Values.ToList();
    }

    private SeatKind CurrentKindOf(string seat)
    {
        lock (_stateGate) return _currentKinds[seat];
    }

    private void SetCurrentKind(string seat, SeatKind kind)
    {
        lock (_stateGate) _currentKinds[seat] = kind;
    }

    /// <summary>
    /// The decision the table is waiting on, tracked for telemetry and
    /// for the poke. Written by the loop thread, read by host threads;
    /// every access takes _stateGate.
    /// </summary>
    private sealed class PendingState
    {
        public required string Seat { get; init; }
        public required int DecisionNumber { get; init; }
        public required DecisionKind Kind { get; init; }
        public required DateTimeOffset Since { get; init; }
        public int FailedReplies { get; set; }
        public string PromptText { get; set; } = "";
        public IAgentChannel? Channel { get; set; }
    }

    private void BeginPending(string seat, int decisionNumber, DecisionKind kind)
    {
        lock (_stateGate)
            _pending = new PendingState
            {
                Seat = seat,
                DecisionNumber = decisionNumber,
                Kind = kind,
                Since = DateTimeOffset.UtcNow,
            };
    }

    private void NotePendingPrompt(IAgentChannel channel, string prompt)
    {
        lock (_stateGate)
        {
            if (_pending is null) return;
            _pending.Channel = channel;
            _pending.PromptText = prompt;
        }
    }

    private void NoteReply(string seat)
    {
        lock (_stateGate) _lastReplyAt[seat] = DateTimeOffset.UtcNow;
    }

    private void NoteFailedReply()
    {
        lock (_stateGate) { if (_pending is not null) _pending.FailedReplies++; }
    }

    private void EndPending(int decisionNumber)
    {
        lock (_stateGate)
        {
            if (_pending is not null && _pending.DecisionNumber == decisionNumber) _pending = null;
        }
    }

    // ------------------------------------------------------------------
    // Emitting: one path for everything the transcript records
    // ------------------------------------------------------------------

    /// <summary>
    /// Records one transcript entry and publishes the identical text to
    /// the spectator feed. Every exchange and host event in the session
    /// flows through here, so the file and the live feed are the same
    /// words in the same order by construction, never by upkeep. The
    /// two sinks are independent: a transcript that failed to start (or
    /// failed mid-game) never silences the feed, and the feed never
    /// throws back into the game.
    /// </summary>
    private void Emit(string entryText)
    {
        // During a replay the entry already stands in the transcript
        // and the feed follows the live game only; the replayed span
        // is re-derived, never re-written.
        if (_replaying) return;
        _transcript?.WriteEntryText(entryText);
        _spectators?.Publish(entryText);
    }

    /// <summary>
    /// Records one host intervention in both audit artifacts at the
    /// moment it takes effect: a HOST line in the game record
    /// and, when the intervention has one, its mirrored entry in
    /// the transcript (published to the feed with everything else).
    /// </summary>
    private void WriteHostIntervention(string recordLine, string? transcriptEntry)
    {
        if (_replaying) return; // a replayed intervention already stands in both artifacts
        if (_record is not null)
        {
            try { _record.AppendHostLine(_game, recordLine); }
            catch (ObjectDisposedException)
            {
                // The game ended as the intervention landed (a poke
                // racing the final decision); the record is closed and
                // the intervention is moot. The transcript entry below
                // still stands if the transcript is open.
            }
        }
        if (transcriptEntry is not null) Emit(transcriptEntry);
    }

    /// <summary>
    /// Records any stale replies a socket channel set aside during the
    /// exchange just finished: each discarded line stands in
    /// the transcript under the decision whose prompt flushed it out.
    /// </summary>
    private void EmitStaleReplies(string seat, int decisionNumber, IAgentChannel channel)
    {
        if (channel is SocketAgentChannel socket)
            foreach (string stale in socket.TakeStaleReplies())
                Emit(GameTranscriptWriter.StaleReplyEntry(seat, decisionNumber, stale));
    }

    /// <summary>What follows every applied decision: the record grows, and watchers get the new table.</summary>
    private void AfterDecisionApplied()
    {
        if (_replaying) return; // the replayed span already stands in the record
        _record!.AppendNew(_game);
        PublishBoard();
    }

    /// <summary>
    /// Sends watchers a fresh board block, rendered from the seat whose
    /// decision is next (its hand is the block's YOUR HAND; watchers see
    /// every seat's hand across the feed's prompts anyway).
    /// When the game just ended there is no next decision, and the
    /// game-over entries that follow are the watchers' closing word.
    /// </summary>
    private void PublishBoard()
    {
        if (_spectators is null || _replaying) return;
        var next = _game.PendingDecision();
        if (next is null) return;
        _spectators.PublishBoard(_prompts.BoardBlock(_game.Snapshot(), next, next.Seat));
    }

    // ------------------------------------------------------------------
    // Bookends
    // ------------------------------------------------------------------

    private void SendBriefings()
    {
        foreach (var (code, channel) in ChannelPairsSnapshot())
        {
            string briefing = _prompts.Briefing(code, _gameConfig);
            Emit(GameTranscriptWriter.BriefingEntry(code, briefing));
            try { channel.Send(briefing); }
            catch (Exception ex)
            {
                Emit(GameTranscriptWriter.ChannelFailedEntry(code, "briefing", null, ex.Message));
                /* a dead channel surfaces at its first prompt, through the pause */
            }
        }
    }

    private void SendGameOver(string winner)
    {
        string text = $"GAME OVER\nWinner: {winner}";
        foreach (var (code, channel) in ChannelPairsSnapshot())
        {
            Emit(GameTranscriptWriter.GameOverEntry(code, text));
            try { channel.Send(text); }
            catch (Exception ex)
            {
                Emit(GameTranscriptWriter.ChannelFailedEntry(code, "game-over", null, ex.Message));
                /* the game is over either way */
            }
        }
    }

    private IReadOnlyList<KeyValuePair<string, IAgentChannel>> ChannelPairsSnapshot()
    {
        lock (_channelsGate) return _channels.ToList();
    }

    private SessionResult Finish(SessionStatus status, string? winner, string? endReason)
    {
        RefuseQueuedEdits();
        _status = status;
        var result = new SessionResult(status, winner, _decisionsResolved, _record!.Path, endReason);
        _record.Finish(result);
        Emit(GameTranscriptWriter.ResultEntry(result));
        Notify(status == SessionStatus.Completed
            ? $"Game over: {winner} wins. Record: {result.RecordPath}"
            : $"Game ended by the host: {endReason} Record: {result.RecordPath}");
        return result;
    }

    private void Notify(string message) => _notifyHost?.Invoke(message);
}
