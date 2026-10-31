using System.Text.RegularExpressions;
using AgentRisk.Engine;

namespace AgentRisk.Host;

/// <summary>Raised when a game transcript does not parse as a clean, complete choice stream.</summary>
public sealed class TranscriptFormatException : Exception
{
    public TranscriptFormatException(string message) : base(message) { }
}

/// <summary>One attempt's outcome within a recorded decision, exactly as the seat produced it.</summary>
public abstract record RecordedAttempt
{
    /// <summary>The seat's reply line, verbatim (legal or not).</summary>
    public sealed record Reply(string Text) : RecordedAttempt;

    /// <summary>The seat's channel closed without answering the prompt.</summary>
    public sealed record NoReply : RecordedAttempt;

    /// <summary>The seat's channel failed mid-exchange; the payload is the transport error.</summary>
    public sealed record ChannelFailed(string Message) : RecordedAttempt;
}

/// <summary>How one recorded segment of a decision ended.</summary>
public abstract record RecordedResolution
{
    /// <summary>The segment's last reply was legal and the decision was applied.</summary>
    public sealed record Applied : RecordedResolution;

    /// <summary>The host substituted the numbered legal move in the strike-pause.</summary>
    public sealed record Substitute(int MoveNumber, string MoveText) : RecordedResolution;

    /// <summary>The host replaced the seat in the strike-pause; the decision was presented again.</summary>
    public sealed record Replaced : RecordedResolution;

    /// <summary>
    /// The host suspended the game from the strike-pause: the decision
    /// was left unanswered. Only ever the last segment of the open
    /// decision a suspended stream ends with.
    /// </summary>
    public sealed record Suspended : RecordedResolution;
}

/// <summary>
/// One presentation episode of a recorded decision: the attempts the
/// seat made (one per prompt) and how the episode ended. A decision
/// has more than one segment only when a strike-pause was resolved by
/// replacing the seat, after which the decision was presented again
/// with its strikes reset.
/// </summary>
public sealed record RecordedSegment(
    IReadOnlyList<RecordedAttempt> Attempts,
    RecordedResolution Resolution);

/// <summary>One decision of the recorded choice stream, in presentation order.</summary>
public sealed record RecordedDecision(
    int Number,
    string Seat,
    IReadOnlyList<RecordedSegment> Segments);

/// <summary>
/// One host edit in the recorded stream: the territory changes
/// the host applied while the table stood host-paused between
/// decisions, and the notes the edit recorded. The edit stands before
/// decision <see cref="BeforeDecision"/> — the next decision the game
/// presented — and a replay applies it at exactly that point, through
/// the engine's one audited edit entry point, so the rebuilt game
/// takes the same edited path the recorded game took.
/// </summary>
public sealed record RecordedEdit(
    int BeforeDecision,
    IReadOnlyList<TerritoryEdit> Edits,
    IReadOnlyList<string> Notes);

/// <summary>
/// A game transcript parsed into its choice stream: the
/// game's identity from the header, every applied decision with the
/// replies that produced it (illegal attempts included, in order, so
/// strikes re-land identically on replay), and — when the game was
/// suspended from a strike-pause — the decision left open.
///
/// The parse is strict on purpose: a resume is only as trustworthy as
/// the stream it replays, so anything the session could not have
/// written (a gap in the decision numbers, a pause with no recorded
/// resolution, a stream that stops mid-decision without a clean
/// suspension) fails loudly, naming the decision it reached, rather
/// than resuming a game that is not quite the recorded one.
/// </summary>
public sealed partial class ParsedTranscript
{
    internal ParsedTranscript(
        GameMode mode, long seed, TerritorySetup territorySetup,
        IReadOnlyList<SeatAssignment> seats,
        IReadOnlyList<RecordedDecision> decisions,
        IReadOnlyList<RecordedEdit> edits,
        RecordedDecision? openDecision,
        int suspendedAtDecision,
        string closingStatus,
        int decisionsAtSuspend)
    {
        Mode = mode;
        Seed = seed;
        TerritorySetup = territorySetup;
        Seats = seats;
        Decisions = decisions;
        Edits = edits;
        OpenDecision = openDecision;
        SuspendedAtDecision = suspendedAtDecision;
        ClosingStatus = closingStatus;
        DecisionsAtSuspend = decisionsAtSuspend;
    }

    public GameMode Mode { get; }
    public long Seed { get; }
    public TerritorySetup TerritorySetup { get; }

    /// <summary>The seats with the kinds the game was originally configured with.</summary>
    public IReadOnlyList<SeatAssignment> Seats { get; }

    /// <summary>The game's identity as originally configured (original kinds included).</summary>
    public GameConfig OriginalConfig => new(Mode, Seats, Seed, TerritorySetup);

    /// <summary>Every decision the recorded stream applied, in order.</summary>
    public IReadOnlyList<RecordedDecision> Decisions { get; }

    /// <summary>
    /// Every host edit the recorded stream carries, in the
    /// order the host applied them; each names the decision it stands
    /// before. Empty in a game that was never edited.
    /// </summary>
    public IReadOnlyList<RecordedEdit> Edits { get; }

    /// <summary>
    /// The decision left unanswered when the game was suspended from a
    /// strike-pause, or null when play stopped between decisions.
    /// </summary>
    public RecordedDecision? OpenDecision { get; }

    /// <summary>The decision the last recorded suspension names: the decision play resumes at.</summary>
    public int SuspendedAtDecision { get; }

    /// <summary>The status of the transcript's last RESULT section ("Suspended", "Completed", ...).</summary>
    public string ClosingStatus { get; }

    /// <summary>The resolved-decision count the last RESULT section recorded.</summary>
    public int DecisionsAtSuspend { get; }

    /// <summary>Parses transcript text into its choice stream. Throws on any malformation.</summary>
    public static ParsedTranscript Parse(string transcriptText)
    {
        ArgumentNullException.ThrowIfNull(transcriptText);
        return new Parser(transcriptText).Run();
    }

    // ------------------------------------------------------------------
    // The parser: a tokenizer over entries and RESULT sections, then a
    // state machine over the entry stream. The invariant the machine
    // leans on: after a REPLY entry, the NEXT entry decides what the
    // reply did — a re-prompt of the same decision means it was
    // rejected, a PAUSED means the failure policy concluded, and
    // anything else means it applied and the decision closed.
    // ------------------------------------------------------------------

    private abstract record Item;
    private sealed record EntryItem(string Marker, string Payload) : Item;
    private sealed record ResultItem(string Status, int Decisions) : Item;

    private sealed class SegmentBuilder
    {
        public int Prompts;
        public readonly List<RecordedAttempt> Attempts = new();
    }

    private sealed class DecisionBuilder
    {
        public required int Number;
        public required string Seat;
        public readonly List<RecordedSegment> Segments = new();
    }

    private sealed partial class Parser
    {
        private readonly List<Item> _items;
        private readonly HashSet<string> _seatCodes;

        private GameMode _mode;
        private long _seed;
        private TerritorySetup _setup;
        private IReadOnlyList<SeatAssignment> _seats = Array.Empty<SeatAssignment>();

        private readonly List<RecordedDecision> _decisions = new();
        private readonly List<RecordedEdit> _edits = new();
        private List<TerritoryEdit>? _editGroup;
        private List<string>? _editGroupNotes;
        private DecisionBuilder? _open;
        private SegmentBuilder? _segment;
        private bool _segmentPaused;
        private bool _lastReplyPending;
        private bool _expectingPause;
        private DecisionBuilder? _suspendedOpen;
        private int _suspendedAtDecision;
        private bool _suspendSeenInSpan;
        private bool _afterSuspendedResult;
        private bool _gameCompleted;
        private string? _closingStatus;
        private int _decisionsAtSuspend;

        public Parser(string text) => _items = Tokenize(text, out _seatCodes, out _mode, out _seed, out _setup, out _seats);

        private int ExpectedDecision => (_decisions.Count == 0 ? 0 : _decisions[^1].Number) + 1;

        private string Where => _open is not null
            ? $"at decision D-{_open.Number}"
            : $"after decision D-{ExpectedDecision - 1}";

        private TranscriptFormatException Fail(string message) =>
            new($"Malformed transcript {Where}: {message}");

        public ParsedTranscript Run()
        {
            foreach (var item in _items)
            {
                switch (item)
                {
                    case EntryItem entry: OnEntry(entry); break;
                    case ResultItem result: OnResult(result); break;
                }
            }
            FlushEditGroup();

            if (_closingStatus is null)
                throw Fail(_open is not null
                    ? $"the stream ends mid-decision at D-{_open.Number} with no result section; " +
                      "it was not cleanly suspended and cannot be resumed."
                    : "the stream ends with no result section; it was not cleanly suspended and cannot be resumed.");
            if (_open is not null)
                throw Fail($"decision D-{_open.Number} is still open at the end of the stream.");
            return new ParsedTranscript(
                _mode, _seed, _setup, _seats, _decisions, _edits,
                _suspendedOpen is null
                    ? null
                    : new RecordedDecision(_suspendedOpen.Number, _suspendedOpen.Seat, _suspendedOpen.Segments),
                _suspendedAtDecision, _closingStatus, _decisionsAtSuspend);
        }

        // ---------------------------- entries ----------------------------

        private void OnEntry(EntryItem entry)
        {
            string marker = entry.Marker;
            // A host edit's entries stand together: the first entry that
            // is not part of the group closes it, fixing the decision
            // the edit stands before as the next one the game presents.
            if (!HostEditRegex().IsMatch(marker) && marker != "### HOST EDIT NOTE")
                FlushEditGroup();
            if (_closingStatus is "Completed" or "EndedByHost")
                throw Fail("entries follow a finished game's result section.");
            if (_afterSuspendedResult)
            {
                if (!ResumeJoinRegex().IsMatch(marker))
                    throw Fail("only a HOST RESUME entry may follow a suspended result section.");
                OnResumeJoin(marker);
                return;
            }
            if (_gameCompleted && !GameOverRegex().IsMatch(marker))
                throw Fail("entries follow the game-over blocks.");

            Match m;
            if ((m = PromptRegex().Match(marker)).Success) OnPrompt(m);
            else if ((m = ReplyRegex().Match(marker)).Success) OnReply(m, entry.Payload);
            else if ((m = NoReplyRegex().Match(marker)).Success) OnFailedAttempt(m, new RecordedAttempt.NoReply());
            else if ((m = ChannelFailedRegex().Match(marker)).Success) OnChannelFailed(m, entry.Payload);
            else if ((m = PausedRegex().Match(marker)).Success) OnPaused(m);
            else if ((m = SubstituteRegex().Match(marker)).Success) OnSubstitute(m, entry.Payload);
            else if ((m = HostEditRegex().Match(marker)).Success) OnHostEdit(m, entry.Payload);
            else if (marker == "### HOST EDIT NOTE") OnHostEditNote(entry.Payload);
            else if ((m = HostEditNoticeRegex().Match(marker)).Success) OnBriefing(m.Groups[1].Value);
            else if (ReplaceRegex().IsMatch(marker)) OnReplace(marker);
            else if (SuspendRegex().IsMatch(marker)) OnSuspend(marker);
            else if (ResumeJoinRegex().IsMatch(marker))
                throw Fail("a HOST RESUME entry appears without a preceding suspended result section.");
            else if (marker is "### HOST PAUSE" or "### HOST RESUME") OnBoundaryHostEntry(marker);
            else if (PokeRegex().IsMatch(marker) || StaleRegex().IsMatch(marker))
            {
                // Events that never touch the choice stream: a poke
                // re-sends a prompt (its answer is the decision's own
                // reply entry), and a stale reply was discarded by the
                // transport precisely because it answered nothing.
            }
            else if ((m = BriefingRegex().Match(marker)).Success) OnBriefing(m.Groups[1].Value);
            else if ((m = GameOverRegex().Match(marker)).Success) OnGameOver(m.Groups[1].Value);
            else if (marker == "### HOST END") OnHostEnd();
            else throw Fail($"unrecognized entry marker '{marker}'.");
        }

        private void OnPrompt(Match m)
        {
            string seat = m.Groups[1].Value;
            int number = int.Parse(m.Groups[2].Value);
            int attempt = int.Parse(m.Groups[3].Value);
            int strike = int.Parse(m.Groups[4].Value);
            RequireSeat(seat);
            if (strike != attempt - 1)
                throw Fail($"prompt for D-{number} has strike={strike} at attempt={attempt}.");

            if (_open is null)
            {
                if (number != ExpectedDecision)
                    throw Fail($"a prompt for D-{number} arrived where D-{ExpectedDecision} was expected.");
                _open = new DecisionBuilder { Number = number, Seat = seat };
                _segment = new SegmentBuilder();
                if (attempt != 1)
                    throw Fail($"decision D-{number} opens at attempt={attempt}.");
                _segment.Prompts = 1;
                return;
            }
            if (_open.Number != number)
            {
                // The previous decision must have closed by its last reply.
                SettleApplied($"a prompt for D-{number}");
                if (number != ExpectedDecision)
                    throw Fail($"a prompt for D-{number} arrived where D-{ExpectedDecision} was expected.");
                _open = new DecisionBuilder { Number = number, Seat = seat };
                _segment = new SegmentBuilder();
                if (attempt != 1)
                    throw Fail($"decision D-{number} opens at attempt={attempt}.");
                _segment.Prompts = 1;
                return;
            }
            // The same decision again: a re-prompt, or a fresh presentation.
            if (seat != _open.Seat)
                throw Fail($"decision D-{number} changed seat from {_open.Seat} to {seat}.");
            if (_segmentPaused)
                throw Fail($"a prompt for D-{number} arrived while the decision sat paused, with no recorded resolution.");
            if (_segment is null)
            {
                // The previous segment ended in a replacement; this is
                // the decision presented again, strikes reset.
                if (attempt != 1)
                    throw Fail($"decision D-{number} is presented again at attempt={attempt}, not 1.");
                _segment = new SegmentBuilder { Prompts = 1 };
                return;
            }
            // A re-prompt inside the segment: the reply before it (if
            // any) was rejected.
            _lastReplyPending = false;
            if (attempt != _segment.Attempts.Count + 1 || attempt > HostConstants.MaxAttemptsPerDecision)
                throw Fail($"prompt attempt={attempt} is out of sequence in decision D-{number}.");
            _segment.Prompts++;
        }

        private void OnReply(Match m, string payload)
        {
            var segment = RequireOpenSegment(m, out int number);
            if (segment.Attempts.Count >= segment.Prompts)
                throw Fail($"a second reply answers one prompt of decision D-{number}.");
            if (payload.Contains('\n'))
                throw Fail($"the reply recorded for decision D-{number} spans several lines; a reply is one line.");
            segment.Attempts.Add(new RecordedAttempt.Reply(payload));
            _lastReplyPending = true;
        }

        private void OnFailedAttempt(Match m, RecordedAttempt attempt)
        {
            var segment = RequireOpenSegment(m, out int number);
            if (segment.Attempts.Count >= segment.Prompts)
                throw Fail($"two outcomes answer one prompt of decision D-{number}.");
            segment.Attempts.Add(attempt);
            _lastReplyPending = false;
            _expectingPause = true; // a dead exchange always lands in the strike-pause
        }

        private void OnChannelFailed(Match m, string payload)
        {
            string seat = m.Groups[1].Value;
            RequireSeat(seat);
            if (m.Groups[3].Value != "prompt") return; // briefing / game-over failures carry no choice
            var segment = RequireOpenSegment(m, out _);
            if (segment.Attempts.Count >= segment.Prompts)
                throw Fail("two outcomes answer one prompt.");
            segment.Attempts.Add(new RecordedAttempt.ChannelFailed(payload));
            _lastReplyPending = false;
            _expectingPause = true;
        }

        private void OnPaused(Match m)
        {
            int number = int.Parse(m.Groups[2].Value);
            if (_open is null || _open.Number != number || _segment is null)
                throw Fail($"a pause is recorded for D-{number}, which is not the open decision.");
            if (_segmentPaused)
                throw Fail($"decision D-{number} is paused twice.");
            bool thirdStrike = _segment.Attempts.Count == HostConstants.MaxAttemptsPerDecision && _lastReplyPending;
            if (!_expectingPause && !thirdStrike)
                throw Fail($"decision D-{number} paused before the failure policy could produce a pause " +
                           $"({_segment.Attempts.Count} attempt(s) recorded).");
            _lastReplyPending = false;
            _expectingPause = false;
            _segmentPaused = true;
        }

        private void OnSubstitute(Match m, string payload)
        {
            int number = int.Parse(m.Groups[2].Value);
            int move = int.Parse(m.Groups[3].Value);
            if (_open is null || _open.Number != number || _segment is null || !_segmentPaused)
                throw Fail($"a host substitute is recorded for D-{number}, which is not the paused decision.");
            if (payload.Contains('\n'))
                throw Fail($"the substitute move recorded for decision D-{number} spans several lines.");
            CloseSegment(new RecordedResolution.Substitute(move, payload));
            CloseDecision();
        }

        private void OnReplace(string marker)
        {
            string seat = ReplaceRegex().Match(marker).Groups[1].Value;
            RequireSeat(seat);
            if (_open is not null && _segmentPaused && seat == _open.Seat)
            {
                // The pause resolves by replacement: the segment closes
                // and the decision will be presented again.
                CloseSegment(new RecordedResolution.Replaced());
                _segment = null;
                _segmentPaused = false;
                return;
            }
            if (_lastReplyPending) SettleApplied("a host replacement");
            // Otherwise a boundary replacement, or another seat replaced
            // while this decision's pause stands: the stream is unmoved.
        }

        private void OnBriefing(string seat)
        {
            RequireSeat(seat);
            if (_open is null) return; // game start, or a span's resumption
            if (_lastReplyPending) { SettleApplied("a briefing"); return; }
            if (_segment is null && !_segmentPaused) return; // a replacement's briefing, mid-decision
            throw Fail($"a briefing for {seat} arrived in the middle of decision D-{_open.Number}.");
        }

        private void OnGameOver(string seat)
        {
            RequireSeat(seat);
            if (_lastReplyPending) SettleApplied("the game-over blocks");
            if (_open is not null)
                throw Fail($"the game ended with decision D-{_open.Number} unresolved.");
            _gameCompleted = true;
        }

        private void OnHostEnd()
        {
            if (_lastReplyPending) SettleApplied("the host ending the game");
            if (_open is not null && !_segmentPaused)
                throw Fail($"the host ended the game in the middle of decision D-{_open.Number}.");
            // Ending from a strike-pause leaves the decision unanswered
            // forever; the factory refuses finished games regardless, so
            // the open decision is simply dropped here.
            _open = null;
            _segment = null;
            _segmentPaused = false;
        }

        private void OnBoundaryHostEntry(string marker)
        {
            // HOST PAUSE / HOST RESUME take effect between
            // decisions only.
            if (_lastReplyPending) { SettleApplied(marker); return; }
            if (_open is not null)
                throw Fail($"{marker} is recorded in the middle of decision D-{_open.Number}.");
        }

        private void OnHostEdit(Match m, string payload)
        {
            string territory = m.Groups[1].Value;
            if (_lastReplyPending) SettleApplied("a host edit");
            if (_open is not null || _segmentPaused)
                throw Fail("a HOST EDIT entry is recorded in the middle of a decision; " +
                           "an edit stands between decisions, inside a host pause.");
            string? newOwner = null;
            int? newArmies = null;
            var tokens = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
                throw Fail($"the HOST EDIT entry for {territory} has a malformed change text: '{payload}'.");
            for (int i = 0; i < tokens.Length; i += 2)
            {
                string field = tokens[i];
                string[] change = tokens[i + 1].Split("->");
                if (change.Length != 2)
                    throw Fail($"the HOST EDIT entry for {territory} has a malformed {field} change: '{tokens[i + 1]}'.");
                switch (field)
                {
                    case "owner":
                        if (newOwner is not null)
                            throw Fail($"the HOST EDIT entry for {territory} changes the owner twice.");
                        newOwner = change[1] == HostConstants.UnclaimedOwnerToken ? null : change[1];
                        break;
                    case "armies":
                        if (newArmies is not null || !int.TryParse(change[1], out int armies))
                            throw Fail($"the HOST EDIT entry for {territory} has a malformed armies change: '{tokens[i + 1]}'.");
                        newArmies = armies;
                        break;
                    default:
                        throw Fail($"the HOST EDIT entry for {territory} names a field outside the edit set: '{field}'.");
                }
            }
            if (newOwner is null && newArmies is null)
                throw Fail($"the HOST EDIT entry for {territory} changes nothing a host edit may change.");
            (_editGroup ??= new List<TerritoryEdit>()).Add(new TerritoryEdit(territory, newOwner, newArmies));
            _editGroupNotes ??= new List<string>();
        }

        private void OnHostEditNote(string payload)
        {
            if (_editGroup is null)
                throw Fail("a HOST EDIT NOTE entry stands with no HOST EDIT before it.");
            if (payload.Length == 0)
                throw Fail("a HOST EDIT NOTE entry carries no note.");
            _editGroupNotes!.Add(payload);
        }

        /// <summary>
        /// Closes the standing host edit group, if any: the edit is
        /// recorded against the next decision the game will present,
        /// which is where the replay applies it.
        /// </summary>
        private void FlushEditGroup()
        {
            if (_editGroup is null) return;
            _edits.Add(new RecordedEdit(
                ExpectedDecision, _editGroup.ToList(), (_editGroupNotes ?? new List<string>()).ToList()));
            _editGroup = null;
            _editGroupNotes = null;
        }

        private void OnSuspend(string marker)
        {
            int number = int.Parse(SuspendRegex().Match(marker).Groups[1].Value);
            if (_open is not null && _segmentPaused)
            {
                // Suspended from the strike-pause: the decision is left
                // open, its segment closed by the suspension itself.
                if (number != _open.Number)
                    throw Fail($"the suspension names D-{number} but the paused decision is D-{_open.Number}.");
                CloseSegment(new RecordedResolution.Suspended());
                _suspendedOpen = _open;
                _open = null;
                _segmentPaused = false;
            }
            else
            {
                if (_lastReplyPending) SettleApplied("the suspension");
                if (_open is not null)
                    throw Fail($"the suspension is recorded in the middle of decision D-{_open.Number}, " +
                               "which is neither resolved nor paused.");
                if (number != ExpectedDecision)
                    throw Fail($"the suspension names D-{number} but the next decision would be D-{ExpectedDecision}.");
            }
            _suspendedAtDecision = number;
            _suspendSeenInSpan = true;
        }

        private void OnResumeJoin(string marker)
        {
            int number = int.Parse(ResumeJoinRegex().Match(marker).Groups[1].Value);
            _afterSuspendedResult = false;
            if (_suspendedOpen is not null)
            {
                if (number != _suspendedOpen.Number)
                    throw Fail($"the resume names D-{number} but the suspended decision is D-{_suspendedOpen.Number}.");
                _open = _suspendedOpen; // its last segment closed with the suspension; play re-presents it
                _suspendedOpen = null;
                _segment = null;
            }
            else if (number != ExpectedDecision)
                throw Fail($"the resume names D-{number} but the next decision would be D-{ExpectedDecision}.");
            _suspendSeenInSpan = false;
        }

        // ---------------------------- results ----------------------------

        private void OnResult(ResultItem result)
        {
            FlushEditGroup();
            if (_closingStatus is "Completed" or "EndedByHost")
                throw Fail("a second result section follows a finished game's result.");
            if (_afterSuspendedResult)
                throw Fail("a result section follows a suspended result with no HOST RESUME entry between.");
            if (_lastReplyPending) SettleApplied("a result section");
            if (_open is not null)
                throw Fail($"a result section closes the stream with decision D-{_open.Number} unresolved.");
            switch (result.Status)
            {
                case "Suspended":
                    if (!_suspendSeenInSpan)
                        throw Fail("a suspended result section has no HOST SUSPEND entry before it.");
                    if (result.Decisions != _decisions.Count)
                        throw Fail($"the suspended result counts {result.Decisions} decisions but the stream applied {_decisions.Count}.");
                    _closingStatus = "Suspended";
                    _decisionsAtSuspend = result.Decisions;
                    _afterSuspendedResult = true;
                    break;
                case "Completed":
                case "EndedByHost":
                    _closingStatus = result.Status;
                    _decisionsAtSuspend = result.Decisions;
                    break;
                default:
                    throw Fail($"unrecognized result status '{result.Status}'.");
            }
        }

        // ---------------------------- helpers ----------------------------

        private SegmentBuilder RequireOpenSegment(Match m, out int number)
        {
            string seat = m.Groups[1].Value;
            number = int.Parse(m.Groups[2].Value);
            RequireSeat(seat);
            if (_open is null || _open.Number != number || _segment is null)
                throw Fail($"an outcome is recorded for D-{number}, which is not the open decision.");
            if (seat != _open.Seat)
                throw Fail($"decision D-{number} belongs to {_open.Seat}, not {seat}.");
            if (_segmentPaused)
                throw Fail($"an outcome is recorded for D-{number} while the decision sat paused.");
            if (_expectingPause)
                throw Fail($"an outcome follows a failed exchange in decision D-{number} with no pause between.");
            return _segment;
        }

        private void SettleApplied(string byWhat)
        {
            if (_open is null || _segment is null || !_lastReplyPending)
                throw Fail($"{byWhat} arrived while decision D-{_open?.Number} had no reply to settle.");
            CloseSegment(new RecordedResolution.Applied());
            CloseDecision();
        }

        private void CloseSegment(RecordedResolution resolution)
        {
            _open!.Segments.Add(new RecordedSegment(_segment!.Attempts.ToList(), resolution));
            _segment = new SegmentBuilder();
            _lastReplyPending = false;
            _expectingPause = false;
        }

        private void CloseDecision()
        {
            _decisions.Add(new RecordedDecision(_open!.Number, _open.Seat, _open.Segments.ToList()));
            _open = null;
            _segment = null;
            _segmentPaused = false;
        }

        private void RequireSeat(string seat)
        {
            if (!_seatCodes.Contains(seat))
                throw Fail($"an entry names seat {seat}, which is not a seat in the transcript header.");
        }

        // ---------------------------- tokenizer ----------------------------

        private static List<Item> Tokenize(
            string text,
            out HashSet<string> seatCodes,
            out GameMode mode,
            out long seed,
            out TerritorySetup setup,
            out IReadOnlyList<SeatAssignment> seats)
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count == 0 || lines[0] != "AGENT RISK GAME TRANSCRIPT v1")
                throw new TranscriptFormatException("Not an Agent Risk game transcript (bad first line).");

            mode = default;
            seed = 0;
            setup = default;
            var seatList = new List<SeatAssignment>();
            bool sectionFound = false;
            int i = 1;
            for (; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.StartsWith("--- TRANSCRIPT", StringComparison.Ordinal)) { i++; sectionFound = true; break; }
                if (line.StartsWith("mode: ", StringComparison.Ordinal))
                    mode = ParseEnum<GameMode>(line["mode: ".Length..], "mode");
                else if (line.StartsWith("seed: ", StringComparison.Ordinal))
                {
                    if (!long.TryParse(line["seed: ".Length..], out seed))
                        throw new TranscriptFormatException($"Transcript header has an unreadable seed: '{line}'.");
                }
                else if (line.StartsWith("setup: ", StringComparison.Ordinal))
                    setup = ParseEnum<TerritorySetup>(line["setup: ".Length..], "setup");
                else if (line.StartsWith("seats: ", StringComparison.Ordinal))
                    seatList.AddRange(ParseSeats(line["seats: ".Length..]));
                else if (line.StartsWith("started-utc: ", StringComparison.Ordinal)) { /* informational */ }
                else throw new TranscriptFormatException($"Unexpected transcript header line: '{line}'.");
            }
            if (!sectionFound || seatList.Count == 0)
                throw new TranscriptFormatException("Transcript header is incomplete (no seats, or no TRANSCRIPT section).");
            seats = seatList;
            seatCodes = seatList.Select(s => s.Code).ToHashSet(StringComparer.Ordinal);

            var items = new List<Item>();
            List<string>? payload = null;
            string? marker = null;

            void FlushEntry()
            {
                if (marker is null) return;
                items.Add(new EntryItem(marker, payload is null ? "" : string.Join("\n", payload).TrimEnd('\n')));
                marker = null;
                payload = null;
            }

            for (; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line == "--- RESULT ---")
                {
                    FlushEntry();
                    items.Add(ParseResult(lines, ref i));
                    continue;
                }
                if (line.StartsWith(">>> ", StringComparison.Ordinal)
                    || line.StartsWith("<<< ", StringComparison.Ordinal)
                    || line.StartsWith("### ", StringComparison.Ordinal))
                {
                    FlushEntry();
                    marker = line;
                    payload = new List<string>();
                    continue;
                }
                if (marker is null)
                {
                    if (line.Length == 0 && i == lines.Count - 1) break; // the file's final newline
                    throw new TranscriptFormatException($"Unexpected line outside any entry: '{line}'.");
                }
                payload!.Add(line);
            }
            FlushEntry();
            return items;
        }

        private static ResultItem ParseResult(List<string> lines, ref int i)
        {
            // i stands on the "--- RESULT ---" line; the field lines
            // follow in the writer's fixed order. Consume exactly the
            // field lines; the caller's loop resumes at the next item.
            string? status = null;
            int decisions = -1;
            for (int j = i + 1; j < lines.Count; j++)
            {
                string line = lines[j];
                if (line.StartsWith("status: ", StringComparison.Ordinal)) status = line["status: ".Length..];
                else if (line.StartsWith("winner: ", StringComparison.Ordinal)) { }
                else if (line.StartsWith("end-reason: ", StringComparison.Ordinal)) { }
                else if (line.StartsWith("decisions: ", StringComparison.Ordinal))
                {
                    if (!int.TryParse(line["decisions: ".Length..], out decisions))
                        throw new TranscriptFormatException($"Result section has an unreadable decisions count: '{line}'.");
                }
                else if (line.StartsWith("ended-utc: ", StringComparison.Ordinal)) { }
                else { i = j - 1; break; }
                if (j == lines.Count - 1) i = j;
            }
            if (status is null || decisions < 0)
                throw new TranscriptFormatException("A result section is missing its status or decisions line.");
            return new ResultItem(status, decisions);
        }

        private static IReadOnlyList<SeatAssignment> ParseSeats(string text)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length % 2 != 0)
                throw new TranscriptFormatException($"Transcript header has a malformed seats line: '{text}'.");
            var seats = new List<SeatAssignment>();
            for (int i = 0; i < tokens.Length; i += 2)
            {
                string kindToken = tokens[i + 1];
                if (kindToken.Length < 3 || kindToken[0] != '(' || kindToken[^1] != ')')
                    throw new TranscriptFormatException($"Transcript header has a malformed seat kind: '{kindToken}'.");
                var kind = kindToken[1..^1] switch
                {
                    "socket" => SeatKind.Socket,
                    "scripted" => SeatKind.Scripted,
                    var other => throw new TranscriptFormatException($"Transcript header names an unknown seat kind '{other}'."),
                };
                seats.Add(new SeatAssignment(tokens[i], kind));
            }
            return seats;
        }

        private static T ParseEnum<T>(string value, string field) where T : struct, Enum
        {
            if (!Enum.TryParse<T>(value, out var parsed) || !Enum.IsDefined(parsed))
                throw new TranscriptFormatException($"Transcript header has an unknown {field}: '{value}'.");
            return parsed;
        }

        // ---------------------------- marker shapes ----------------------------

        [GeneratedRegex(@"^>>> (\S+) D-(\d+) PROMPT attempt=(\d+) strike=(\d+)$")]
        private static partial Regex PromptRegex();

        [GeneratedRegex(@"^<<< (\S+) D-(\d+) REPLY$")]
        private static partial Regex ReplyRegex();

        [GeneratedRegex(@"^<<< (\S+) D-(\d+) NO-REPLY$")]
        private static partial Regex NoReplyRegex();

        [GeneratedRegex(@"^### (\S+)(?: D-(\d+))? CHANNEL FAILED context=(\w+)$")]
        private static partial Regex ChannelFailedRegex();

        [GeneratedRegex(@"^### PAUSED seat=(\S+) decision=D-(\d+)$")]
        private static partial Regex PausedRegex();

        [GeneratedRegex(@"^### HOST SUBSTITUTE seat=(\S+) decision=D-(\d+) move=(\d+)$")]
        private static partial Regex SubstituteRegex();

        [GeneratedRegex(@"^### HOST EDIT territory=(\S+)$")]
        private static partial Regex HostEditRegex();

        [GeneratedRegex(@"^>>> (\S+) HOST EDIT$")]
        private static partial Regex HostEditNoticeRegex();

        [GeneratedRegex(@"^### HOST REPLACE seat=(\S+)$")]
        private static partial Regex ReplaceRegex();

        [GeneratedRegex(@"^### HOST POKE seat=(\S+) decision=D-(\d+)$")]
        private static partial Regex PokeRegex();

        [GeneratedRegex(@"^### (\S+) D-(\d+) STALE REPLY DISCARDED$")]
        private static partial Regex StaleRegex();

        [GeneratedRegex(@"^### HOST SUSPEND decision=D-(\d+)$")]
        private static partial Regex SuspendRegex();

        [GeneratedRegex(@"^### HOST RESUME decision=D-(\d+)$")]
        private static partial Regex ResumeJoinRegex();

        [GeneratedRegex(@"^>>> (\S+) (?:RESUME )?BRIEFING$")]
        private static partial Regex BriefingRegex();

        [GeneratedRegex(@"^>>> (\S+) GAME OVER$")]
        private static partial Regex GameOverRegex();
    }
}
