using AgentRisk.Engine;

namespace AgentRisk.Host;

/// <summary>
/// The per-game seat transcript: one text file written beside
/// the game record, holding every seat-facing exchange in the order it
/// happened — each briefing, each decision prompt exactly as handed to
/// the channel, each reply exactly as received, and the host actions
/// and channel failures that interrupted the flow. The game record
/// proves what the game did; the transcript proves what each seat was
/// shown and what it answered, which is what a host auditing an agent
/// (or a client panel replaying one) actually needs.
///
/// Format: a header mirroring the record's, then entries. An entry is a
/// marker line followed by its payload lines, running to the next
/// marker line (payloads are host-built text and single-line replies;
/// neither can begin with a marker prefix). Markers:
///
///   >>> SEAT BRIEFING            block sent, no reply expected
///   >>> SEAT D-n PROMPT attempt=a strike=s
///   &lt;&lt;&lt; SEAT D-n REPLY              the seat's reply line, verbatim
///   &lt;&lt;&lt; SEAT D-n NO-REPLY           the channel closed without a reply
///   >>> SEAT GAME OVER           the game-over block
///   >>> SEAT HOST EDIT           a host edit notice, sent to every seat
///   ### ...                      an event, not an exchange: PAUSED,
///                                HOST SUBSTITUTE / REPLACE / END,
///                                HOST POKE, HOST PAUSE / RESUME,
///                                HOST EDIT and HOST EDIT NOTE,
///                                SEAT CHANNEL FAILED, and a seat's
///                                STALE REPLY DISCARDED (a duplicate
///                                answer set aside as stale)
///
/// A suspended game closes its transcript with a
/// `### HOST SUSPEND decision=D-<n>` event and a RESULT section whose
/// status is Suspended. A resumed game continues THIS file: the join
/// point is a `### HOST RESUME decision=D-<n>` event (payload: the
/// resume context), each live seat is briefed again under a
/// `>>> SEAT RESUME BRIEFING` marker, and entries continue as usual,
/// closing with a fresh RESULT section when the game ends. A file may
/// therefore carry several spans and several RESULT sections; the
/// bare `### HOST RESUME` (a host pause released) is a
/// different entry from the resume join, which always names its
/// decision.
///
/// Framing markers (HostConstants.EndMarker / ReplyMarker) are added
/// by the socket transport on the wire and are NOT recorded: the
/// transcript holds the block text exactly as the session handed it to
/// the channel, so a socket seat and an in-process seat produce
/// identical transcripts for identical games.
///
/// The entry texts are built by this class's static *Entry methods and
/// shared verbatim with the spectator feed (<see cref="SpectatorFeed"/>): the feed carries these same entries live, plus one
/// feed-only entry, <c>>>> BOARD</c> (payload: the current board
/// block), sent on connect and after every applied decision so a
/// watcher can re-render the table without parsing event lines. BOARD
/// entries are never written to this file — the file's prompts already
/// carry the full board at every step.
///
/// The transcript is a secondary artifact: a write failure is reported
/// once through the error callback and disables further entries, and
/// never interrupts the game (the record remains the primary audit).
/// All lines end in "\n" on every OS (the record writer's convention of
/// OS newlines is deliberately not followed; this file is parsed).
/// </summary>
public sealed class GameTranscriptWriter : IDisposable
{
    /// <summary>Suffix that replaces the record file's ".log" to name the transcript.</summary>
    public const string TranscriptSuffix = ".transcript.log";

    private const string RecordSuffix = ".log";

    private readonly StreamWriter _writer;
    private readonly Action<string>? _reportError;
    private readonly object _gate = new();
    private bool _failed;

    // Resume mode: the writer is opened over the suspended
    // game's existing transcript and writes nothing until CatchUp is
    // called at the replay boundary — the replayed exchanges already
    // stand in the file, and the replay must not duplicate them.
    private bool _catchingUp;

    private GameTranscriptWriter(string path, StreamWriter writer, Action<string>? reportError)
    {
        Path = path;
        _writer = writer;
        _reportError = reportError;
    }

    /// <summary>The transcript file's path.</summary>
    public string Path { get; }

    /// <summary>The transcript path that belongs to a game record path.</summary>
    public static string PathForRecord(string recordPath) =>
        recordPath.EndsWith(RecordSuffix, StringComparison.OrdinalIgnoreCase)
            ? recordPath[..^RecordSuffix.Length] + TranscriptSuffix
            : recordPath + TranscriptSuffix;

    /// <summary>
    /// Creates the transcript beside the game record at
    /// <paramref name="recordPath"/> and writes its header. Throws if the
    /// file cannot be opened; the caller (the session) treats that as a
    /// lost transcript, never a lost game.
    /// </summary>
    public static GameTranscriptWriter Create(
        string recordPath, GameConfig config, Func<string, SeatKind> seatKindOf,
        Action<string>? reportError = null)
    {
        string path = PathForRecord(recordPath);
        var writer = new StreamWriter(path, append: false) { AutoFlush = true, NewLine = "\n" };

        var transcript = new GameTranscriptWriter(path, writer, reportError);
        writer.WriteLine("AGENT RISK GAME TRANSCRIPT v1");
        writer.WriteLine($"mode: {config.Mode}");
        writer.WriteLine($"seed: {config.Seed}");
        writer.WriteLine($"setup: {config.TerritorySetup}");
        writer.WriteLine($"seats: {string.Join(" ", config.Seats.Select(s => $"{s.Code} ({seatKindOf(s.Code).ToString().ToLowerInvariant()})"))}");
        writer.WriteLine($"started-utc: {DateTime.UtcNow:O}");
        writer.WriteLine("--- TRANSCRIPT (seat-facing exchanges, chronological) ---");
        return transcript;
    }

    /// <summary>
    /// Reopens the transcript beside <paramref name="recordPath"/> for
    /// the resume: no header is written (the original stands),
    /// and entries are held back until <see cref="CatchUp"/> marks the
    /// replay boundary. A write failure is reported through
    /// <paramref name="reportError"/> like any transcript failure; the
    /// file itself must exist — <see cref="ResumePlan.FromFiles"/> has
    /// already read and validated it before this is called.
    /// </summary>
    public static GameTranscriptWriter OpenForResume(string recordPath, Action<string>? reportError = null)
    {
        string path = PathForRecord(recordPath);
        var writer = new StreamWriter(path, append: true) { AutoFlush = true, NewLine = "\n" };
        return new GameTranscriptWriter(path, writer, reportError) { _catchingUp = true };
    }

    /// <summary>
    /// Ends resume catch-up: entries flow to the file again. The
    /// session calls this at the replay boundary, after verifying the
    /// replay reproduced the recorded game exactly; the first entry
    /// written afterwards is the HOST RESUME join.
    /// </summary>
    public void CatchUp()
    {
        lock (_gate) _catchingUp = false;
    }

    // ------------------------------------------------------------------
    // Entry texts: built once here, written to the file by the instance
    // methods below and published verbatim to the spectator feed by the
    // session, so the file and the feed can never drift apart.
    // ------------------------------------------------------------------

    /// <summary>The entry text for a briefing block sent to a seat.</summary>
    public static string BriefingEntry(string seat, string text) =>
        Entry($">>> {seat} BRIEFING", text);

    /// <summary>The entry text for one decision prompt, as handed to the channel.</summary>
    public static string PromptEntry(string seat, int decisionNumber, int attempt, string promptText) =>
        Entry($">>> {seat} D-{decisionNumber} PROMPT attempt={attempt} strike={attempt - 1}", promptText);

    /// <summary>The entry text for a seat's reply line, exactly as received.</summary>
    public static string ReplyEntry(string seat, int decisionNumber, string reply) =>
        Entry($"<<< {seat} D-{decisionNumber} REPLY", reply);

    /// <summary>The entry text for a channel that closed with no reply to the pending prompt.</summary>
    public static string NoReplyEntry(string seat, int decisionNumber) =>
        Entry($"<<< {seat} D-{decisionNumber} NO-REPLY", "channel closed without a reply");

    /// <summary>The entry text for the game-over block sent to a seat.</summary>
    public static string GameOverEntry(string seat, string text) =>
        Entry($">>> {seat} GAME OVER", text);

    /// <summary>
    /// The entry text for a fresh board block. Feed-only (see the class
    /// remarks): the payload is a board block, rendered for the
    /// seat whose decision is next.
    /// </summary>
    public static string BoardEntry(string boardBlockText) =>
        Entry(">>> BOARD", boardBlockText);

    /// <summary>The entry text for a channel failure; the payload is the transport error.</summary>
    public static string ChannelFailedEntry(string seat, string context, int? decisionNumber, string message) =>
        Entry(
            $"### {seat}{(decisionNumber is null ? "" : $" D-{decisionNumber}")} CHANNEL FAILED context={context}",
            message);

    /// <summary>The entry text for a pause; the payload is the reason shown to the host.</summary>
    public static string PausedEntry(string seat, int decisionNumber, string reason) =>
        Entry($"### PAUSED seat={seat} decision=D-{decisionNumber}", reason);

    /// <summary>The entry text for a host substitute; the payload is the move the host played.</summary>
    public static string HostSubstituteEntry(string seat, int decisionNumber, int moveNumber, string moveText) =>
        Entry($"### HOST SUBSTITUTE seat={seat} decision=D-{decisionNumber} move={moveNumber}", moveText);

    /// <summary>The entry text for a host seat replacement.</summary>
    public static string HostReplaceEntry(string seat) =>
        Entry($"### HOST REPLACE seat={seat}", payload: null);

    /// <summary>The entry text for the host ending the game from a pause.</summary>
    public static string HostEndEntry() =>
        Entry("### HOST END", payload: null);

    /// <summary>
    /// The entry text for a host poke: the seat's pending prompt was
    /// re-sent, unchanged. The poke gets an event entry, not a second
    /// PROMPT entry — the prompt it re-sends already stands in the
    /// transcript under its decision, and one PROMPT entry per question
    /// asked is what keeps the file auditable. The payload is the fact
    /// of the re-send; the prompt text is the decision's own entry.
    /// </summary>
    public static string HostPokeEntry(string seat, int decisionNumber) =>
        Entry($"### HOST POKE seat={seat} decision=D-{decisionNumber}", "the pending prompt was re-sent, unchanged");

    /// <summary>The entry text for the host pausing the table between decisions.</summary>
    public static string HostPauseEntry() =>
        Entry("### HOST PAUSE", payload: null);

    /// <summary>The entry text for the host resuming a host-paused table.</summary>
    public static string HostResumeEntry() =>
        Entry("### HOST RESUME", payload: null);

    /// <summary>
    /// The entry text for the host suspending the game. When
    /// <paramref name="decisionOpen"/> is set, the named decision was
    /// in a strike-pause and is left unanswered — the one shape in
    /// which a transcript's choice stream ends mid-decision; the
    /// decision is presented again on resume. Otherwise the game
    /// stopped between decisions and the named decision is the next
    /// one play will present.
    /// </summary>
    public static string HostSuspendEntry(int decisionNumber, bool decisionOpen) =>
        Entry(
            $"### HOST SUSPEND decision=D-{decisionNumber}",
            decisionOpen
                ? "the game was suspended by the host with this decision unanswered; it will be presented again on resume"
                : "the game was suspended by the host between decisions; this decision will be presented on resume");

    /// <summary>
    /// The entry text for the join point of a resumed game:
    /// the replay reached the suspend boundary and the table went
    /// live in this same transcript. The marker always names the
    /// decision play resumes at, which is what distinguishes it from
    /// the bare <see cref="HostResumeEntry"/> (a host pause
    /// released). The payload is the resume context: how many
    /// decisions were replayed, and how each seat is driven now.
    /// </summary>
    public static string HostResumeEntry(int decisionNumber, string context) =>
        Entry($"### HOST RESUME decision=D-{decisionNumber}", context);

    /// <summary>
    /// The entry text for the briefing a live seat receives when a
    /// suspended game resumes: the game's ordinary briefing, marked as
    /// a resumption so the audit shows the seat was re-seated, not
    /// seated for the first time.
    /// </summary>
    public static string ResumeBriefingEntry(string seat, string text) =>
        Entry($">>> {seat} RESUME BRIEFING", text);

    /// <summary>
    /// The change text shared by a host edit's record line and its
    /// transcript entry: the fields that changed, old to new,
    /// in the record's grammar. An unclaimed territory's
    /// owner renders as the host's unclaimed token. The transcript
    /// parser reads exactly this text back into the edit the replay
    /// applies, so the two artifacts can never disagree about an edit.
    /// </summary>
    public static string HostEditParts(TerritoryChange change)
    {
        var parts = new List<string>();
        if (change.OwnerChanged)
            parts.Add($"owner {OwnerToken(change.OldOwner)}->{OwnerToken(change.NewOwner)}");
        if (change.ArmiesChanged)
            parts.Add($"armies {change.OldArmies}->{change.NewArmies}");
        return string.Join(" ", parts);
    }

    private static string OwnerToken(string? owner) => owner ?? HostConstants.UnclaimedOwnerToken;

    /// <summary>
    /// The entry text for one territory changed by a host edit (Tier
    /// 3). One entry per changed territory, in the edit's own order,
    /// followed by any HOST EDIT NOTE entries; the group stands between
    /// decisions, inside the host pause that allowed it. The change
    /// text is the record line's own (see <see cref="HostEditParts"/>).
    /// </summary>
    public static string HostEditEntry(TerritoryChange change) =>
        Entry($"### HOST EDIT territory={change.Territory}", HostEditParts(change));

    /// <summary>
    /// The entry text for one note of a host edit: a plain
    /// statement of what the edit did to the position around the
    /// changes (an attack called off, a seat left holding no
    /// territories), as the engine reported it. Notes follow their
    /// edit's change entries and belong to that edit's group.
    /// </summary>
    public static string HostEditNoteEntry(string note) =>
        Entry("### HOST EDIT NOTE", note);

    /// <summary>
    /// The entry text for the notice of a host edit sent to one seat:
    /// the seat is told the position was edited, what
    /// changed, and that play continues from the edited position —
    /// out of band, like a briefing, because the choice stream itself
    /// carries only the edit entries the replay applies.
    /// </summary>
    public static string HostEditNoticeEntry(string seat, string text) =>
        Entry($">>> {seat} HOST EDIT", text);

    /// <summary>
    /// The entry text for a stale reply set aside by the transport:
    /// a complete reply line that arrived for an earlier
    /// decision and was discarded when the seat's next prompt went
    /// out. The payload is the discarded line, verbatim — what the seat
    /// said is recorded; that it counted for nothing is the marker.
    /// </summary>
    public static string StaleReplyEntry(string seat, int decisionNumber, string replyLine) =>
        Entry($"### {seat} D-{decisionNumber} STALE REPLY DISCARDED", replyLine);

    /// <summary>The closing section's text, mirroring the game record's result.</summary>
    public static string ResultEntry(SessionResult result) =>
        "--- RESULT ---\n" +
        $"status: {result.Status}\n" +
        (result.Winner is not null ? $"winner: {result.Winner}\n" : "winner: none\n") +
        (result.EndReason is not null ? $"end-reason: {result.EndReason}\n" : "") +
        $"decisions: {result.Decisions}\n" +
        $"ended-utc: {DateTime.UtcNow:O}\n";

    private static string Entry(string marker, string? payload) =>
        payload is null
            ? marker + "\n"
            : marker + "\n" + payload + (payload.EndsWith('\n') ? "" : "\n");

    // ------------------------------------------------------------------
    // Writing: one lock, one guarded path. An entry is written whole or
    // the transcript is marked failed; the game never sees an exception.
    // ------------------------------------------------------------------

    /// <summary>Writes one entry text built by the static *Entry methods.</summary>
    internal void WriteEntryText(string entryText) => Write(entryText);

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }

    private void Write(string text)
    {
        lock (_gate)
        {
            if (_failed || _catchingUp) return;
            try
            {
                _writer.Write(text);
            }
            catch (Exception ex)
            {
                _failed = true;
                _reportError?.Invoke(
                    $"Transcript write failed ({ex.Message}); no further transcript entries will be written. " +
                    "The game and its record are unaffected.");
            }
        }
    }
}
