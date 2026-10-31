using AgentRisk.Engine;

namespace AgentRisk.Host;

/// <summary>
/// Raised when a game cannot be resumed: it is finished, it was not
/// cleanly suspended, its record and transcript disagree, or its files
/// are missing. The refusal is the feature — a resume that cannot be
/// exact does not happen.
/// </summary>
public sealed class ResumeException : Exception
{
    public ResumeException(string message) : base(message) { }
}

/// <summary>
/// Raised when a replay under way departs from the recorded game: the
/// re-execution asked a question the recorded stream cannot answer,
/// or the rebuilt game does not match the record at the suspend
/// boundary. The suspended files are untouched (nothing is appended
/// before the boundary is verified), so the game can be resumed again
/// once the cause — usually a code change that altered play — is
/// understood.
/// </summary>
public sealed class ResumeDivergedException : Exception
{
    public ResumeDivergedException(string message) : base(message) { }
}

/// <summary>
/// Everything a resume needs, loaded and cross-checked before any
/// session is built: the transcript's parsed choice stream,
/// the paths of the two files the resumed game will continue, and the
/// suspended record's event and dice counts, against which the replay
/// is verified at the boundary.
///
/// A plan can only be built for a game that was cleanly suspended:
/// finished games (a winner, or a host ending) are refused, as are a
/// record and transcript that disagree about the game's identity or
/// about how much of it was played. The record header and the
/// transcript header both carry the game's identity (mode, seed,
/// territory setup, seats with their original kinds); the resume
/// configuration the host supplies may change how seats are driven
/// from the resume on, never what game this is.
/// </summary>
public sealed class ResumePlan
{
    private ResumePlan(
        ParsedTranscript stream, string recordPath, string transcriptPath,
        int recordEventCount, int recordDiceCount, bool wasHostEdited)
    {
        Stream = stream;
        RecordPath = recordPath;
        TranscriptPath = transcriptPath;
        RecordEventCount = recordEventCount;
        RecordDiceCount = recordDiceCount;
        WasHostEdited = wasHostEdited;
    }

    /// <summary>The recorded choice stream, parsed and validated.</summary>
    public ParsedTranscript Stream { get; }

    /// <summary>The game record the resumed session will continue (append).</summary>
    public string RecordPath { get; }

    /// <summary>The transcript the resumed session will continue (append).</summary>
    public string TranscriptPath { get; }

    /// <summary>Engine event lines standing in the suspended record (all spans).</summary>
    public int RecordEventCount { get; }

    /// <summary>DICE lines standing in the suspended record (all spans).</summary>
    public int RecordDiceCount { get; }

    /// <summary>
    /// The suspended game was edited by its host at least once (Tier
    /// 3): the record header's flag says so, or its HOST EDIT lines
    /// stand in the log. A resumed session carries the flag from the
    /// start, before any replayed edit re-derives it.
    /// </summary>
    public bool WasHostEdited { get; }

    /// <summary>The game's identity as originally configured, original seat kinds included.</summary>
    public GameConfig OriginalConfig => Stream.OriginalConfig;

    /// <summary>Decisions the recorded stream applied: the count the replay must resolve.</summary>
    public int AppliedDecisionCount => Stream.Decisions.Count;

    /// <summary>The decision left open by a pause-suspension, when there is one.</summary>
    public RecordedDecision? OpenDecision => Stream.OpenDecision;

    /// <summary>The decision play resumes at (the decision the suspension named).</summary>
    public int ResumeDecisionNumber => Stream.SuspendedAtDecision;

    /// <summary>
    /// Loads and cross-checks a suspended game's record and transcript.
    /// </summary>
    /// <exception cref="ResumeException">
    /// The files are missing, the game is finished or was not cleanly
    /// suspended, or the two files disagree.
    /// </exception>
    /// <exception cref="TranscriptFormatException">The transcript does not parse as a clean choice stream.</exception>
    public static ResumePlan FromFiles(string recordPath, string transcriptPath)
    {
        ArgumentNullException.ThrowIfNull(recordPath);
        ArgumentNullException.ThrowIfNull(transcriptPath);
        if (!File.Exists(recordPath))
            throw new ResumeException($"the game record '{recordPath}' does not exist.");
        if (!File.Exists(transcriptPath))
            throw new ResumeException(
                $"the transcript '{transcriptPath}' does not exist. " +
                "A resume replays the transcript's recorded choices; without it there is nothing exact to resume from.");

        var record = RecordSummary.Read(recordPath);
        var stream = ParsedTranscript.Parse(File.ReadAllText(transcriptPath));

        if (stream.ClosingStatus == "Completed")
            throw new ResumeException(
                "the game is already finished. A finished game is a result, not a session to continue.");
        if (stream.ClosingStatus == "EndedByHost")
            throw new ResumeException(
                "the game was ended by the host. A host-ended game is closed, not suspended.");
        if (record.LastStatus != "Suspended")
            throw new ResumeException(
                $"the record's last result is '{record.LastStatus ?? "missing"}', not Suspended. " +
                "Only a cleanly suspended game can be resumed.");
        if (!record.Matches(stream))
            throw new ResumeException(
                "the record and the transcript disagree about the game " +
                "(mode, seed, setup, seats, or the decisions played). One of the files is not this game's.");
        if (record.LastDecisions != stream.DecisionsAtSuspend)
            throw new ResumeException(
                $"the record counts {record.LastDecisions} decisions played but the transcript's " +
                $"stream applied {stream.DecisionsAtSuspend}. The files do not describe the same stopping point.");

        return new ResumePlan(stream, recordPath, transcriptPath, record.EventCount, record.DiceCount,
            record.HostEdited || stream.Edits.Count > 0);
    }

    /// <summary>
    /// The record's side of the cross-check: its header identity, the
    /// event and dice lines standing in its log sections (HOST lines
    /// and RESULT sections excluded — the replay re-derives events and
    /// dice, not the host's own lines), and its last result.
    /// </summary>
    private sealed class RecordSummary
    {
        public required GameConfig Header { get; init; }
        public required int EventCount { get; init; }
        public required int DiceCount { get; init; }
        public required string? LastStatus { get; init; }
        public required int LastDecisions { get; init; }
        public required bool HostEdited { get; init; }

        public bool Matches(ParsedTranscript stream) =>
            Header.Mode == stream.Mode
            && Header.Seed == stream.Seed
            && Header.TerritorySetup == stream.TerritorySetup
            && Header.Seats.Count == stream.Seats.Count
            && Header.Seats.Zip(stream.Seats).All(pair => pair.First == pair.Second);

        public static RecordSummary Read(string path)
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != "AGENT RISK GAME RECORD v1")
                throw new ResumeException($"'{path}' is not an Agent Risk game record (bad first line).");

            GameMode mode = default;
            long seed = 0;
            TerritorySetup setup = default;
            bool hostEdited = false;
            var seats = new List<SeatAssignment>();
            int i = 1;
            bool logFound = false;
            for (; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("--- GAME LOG", StringComparison.Ordinal)) { logFound = true; i++; break; }
                if (line.StartsWith("mode: ", StringComparison.Ordinal))
                {
                    if (!Enum.TryParse(line["mode: ".Length..], out mode))
                        throw new ResumeException($"Record header has an unknown mode: '{line}'.");
                }
                else if (line.StartsWith("seed: ", StringComparison.Ordinal))
                {
                    if (!long.TryParse(line["seed: ".Length..], out seed))
                        throw new ResumeException($"Record header has an unreadable seed: '{line}'.");
                }
                else if (line.StartsWith("setup: ", StringComparison.Ordinal))
                {
                    if (!Enum.TryParse(line["setup: ".Length..], out setup))
                        throw new ResumeException($"Record header has an unknown setup: '{line}'.");
                }
                else if (line.StartsWith("host-edited:", StringComparison.Ordinal))
                    hostEdited = line["host-edited:".Length..].Trim() == "yes";
                else if (line.StartsWith("seats: ", StringComparison.Ordinal))
                    seats.AddRange(ParseSeats(line["seats: ".Length..]));
            }
            if (!logFound || seats.Count == 0)
                throw new ResumeException("Record header is incomplete (no seats, or no GAME LOG section).");

            int events = 0, dice = 0;
            string? lastStatus = null;
            int lastDecisions = -1;
            bool inResult = false;
            for (; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line == "--- RESULT ---") { inResult = true; continue; }
                if (inResult)
                {
                    if (line.StartsWith("status: ", StringComparison.Ordinal)) lastStatus = line["status: ".Length..];
                    else if (line.StartsWith("decisions: ", StringComparison.Ordinal))
                        _ = int.TryParse(line["decisions: ".Length..], out lastDecisions);
                    else if (line.StartsWith("winner: ", StringComparison.Ordinal)
                             || line.StartsWith("end-reason: ", StringComparison.Ordinal)
                             || line.StartsWith("ended-utc: ", StringComparison.Ordinal)) { }
                    else inResult = false; // the first non-field line ends the section; count it below
                    if (inResult) continue;
                }
                if (line.Length == 0) continue;
                if (line.StartsWith("DICE ", StringComparison.Ordinal)) dice++;
                else if (line.StartsWith("HOST ", StringComparison.Ordinal)) { /* the host's own lines */ }
                else events++;
            }
            return new RecordSummary
            {
                Header = new GameConfig(mode, seats, seed, setup),
                EventCount = events,
                DiceCount = dice,
                LastStatus = lastStatus,
                LastDecisions = lastDecisions,
                HostEdited = hostEdited,
            };
        }

        private static IReadOnlyList<SeatAssignment> ParseSeats(string text)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length % 2 != 0)
                throw new ResumeException($"Record header has a malformed seats line: '{text}'.");
            var seats = new List<SeatAssignment>();
            for (int i = 0; i < tokens.Length; i += 2)
            {
                string kindToken = tokens[i + 1];
                if (kindToken.Length < 3 || kindToken[0] != '(' || kindToken[^1] != ')')
                    throw new ResumeException($"Record header has a malformed seat kind: '{kindToken}'.");
                var kind = kindToken[1..^1] switch
                {
                    "socket" => SeatKind.Socket,
                    "scripted" => SeatKind.Scripted,
                    var other => throw new ResumeException($"Record header names an unknown seat kind '{other}'."),
                };
                seats.Add(new SeatAssignment(tokens[i], kind));
            }
            return seats;
        }
    }
}
