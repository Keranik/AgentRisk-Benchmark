namespace AgentRisk.Host;

/// <summary>
/// The replay half of a resume: feeds a recorded choice
/// stream back through a fresh <see cref="GameSession"/> so the game
/// is reconstructed exactly, decision by decision.
///
/// The driver flattens the parsed stream (<see cref="ParsedTranscript"/>)
/// into the shapes the session consumes:
///
/// - Per seat, a queue of recorded attempts. One replay channel per
///   seat pops its queue at every prompt: a recorded reply is returned
///   verbatim, a recorded no-reply closes the channel's answer, a
///   recorded channel failure throws the same transport error. The
///   session's ordinary machinery does the rest — re-prompts, strikes,
///   pauses — and because the engine is deterministic under the seed,
///   it walks exactly the path the original session walked.
/// - Per decision, a queue of the resolutions that ended its paused
///   segments (a substitute, a replacement, or — for the decision a
///   pause-suspension left open — the suspension itself). When the
///   replaying session enters a strike-pause, it asks the driver how
///   the original pause was resolved and does that.
/// - A queue of the recorded host edits, each standing
///   before a named decision. The session takes each edit as it
///   reaches that decision and applies it through the engine's edit
///   entry point, so the rebuilt position is the edited position.
///
/// Anything the stream does not account for — a prompt the stream has
/// no attempt for, a pause the stream never resolved — is divergence:
/// the replay is not reproducing the recorded game, and the driver
/// says so (as <see cref="ResumeDivergedException"/>) instead of
/// improvising. A resume that cannot be exact does not happen.
/// </summary>
internal sealed class ReplayDriver
{
    private readonly Dictionary<string, Queue<RecordedAttempt>> _attemptsBySeat = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Queue<RecordedResolution>> _resolutionsByDecision = new();
    private readonly Dictionary<string, ReplayChannel> _channels = new(StringComparer.Ordinal);
    private readonly Queue<RecordedEdit> _edits;

    public ReplayDriver(ParsedTranscript stream)
    {
        _edits = new Queue<RecordedEdit>(stream.Edits);
        foreach (string seat in stream.Seats.Select(s => s.Code))
            _attemptsBySeat[seat] = new Queue<RecordedAttempt>();

        foreach (var decision in stream.Decisions.Concat(
                     stream.OpenDecision is null ? Enumerable.Empty<RecordedDecision>() : new[] { stream.OpenDecision }))
        {
            var resolutions = new Queue<RecordedResolution>();
            foreach (var segment in decision.Segments)
            {
                foreach (var attempt in segment.Attempts)
                    _attemptsBySeat[decision.Seat].Enqueue(attempt);
                if (segment.Resolution is not RecordedResolution.Applied)
                    resolutions.Enqueue(segment.Resolution);
            }
            _resolutionsByDecision[decision.Number] = resolutions;
        }

        foreach (string seat in _attemptsBySeat.Keys)
            _channels[seat] = new ReplayChannel(seat, this);
    }

    /// <summary>One replay channel per seat, keyed by seat code.</summary>
    public IReadOnlyDictionary<string, ReplayChannel> Channels => _channels;

    /// <summary>
    /// True when every recorded attempt, every recorded pause
    /// resolution, and every recorded host edit has been consumed:
    /// the replay has rebuilt the whole recorded stream.
    /// </summary>
    public bool Exhausted =>
        _attemptsBySeat.Values.All(q => q.Count == 0)
        && _resolutionsByDecision.Values.All(q => q.Count == 0)
        && _edits.Count == 0;

    /// <summary>
    /// Takes the next recorded host edit when it stands
    /// before <paramref name="decisionNumber"/> — the decision the
    /// replay is about to present. Edits are consumed in recorded
    /// order, each at its own point in the choice stream; an edit the
    /// replay has already walked past is divergence, and says so.
    /// </summary>
    internal bool TryTakeEditBefore(int decisionNumber, out RecordedEdit edit)
    {
        edit = null!;
        if (_edits.Count == 0) return false;
        var head = _edits.Peek();
        if (head.BeforeDecision < decisionNumber)
            throw new ResumeDivergedException(
                $"Replay diverged: the recorded host edit before decision D-{head.BeforeDecision} " +
                $"was never applied, but the replay has reached D-{decisionNumber}. " +
                "The resume is abandoned; the suspended record and transcript are untouched.");
        if (head.BeforeDecision > decisionNumber) return false;
        edit = _edits.Dequeue();
        return true;
    }

    internal RecordedAttempt NextAttempt(string seat, string prompt)
    {
        _channels[seat].NotePrompt(prompt);
        if (_attemptsBySeat[seat].Count == 0)
            throw new ChannelDisconnectedException(
                $"The recorded stream has no answer for seat {seat}'s prompt; the replay has diverged from the recorded game.");
        return _attemptsBySeat[seat].Dequeue();
    }

    /// <summary>
    /// How the original session resolved the pause the replay has just
    /// re-entered for this decision, consumed in segment order. Throws
    /// <see cref="ResumeDivergedException"/> when the stream recorded
    /// no resolution — the replay paused where the original did not.
    /// </summary>
    internal RecordedResolution NextResolution(int decisionNumber)
    {
        if (!_resolutionsByDecision.TryGetValue(decisionNumber, out var queue) || queue.Count == 0)
            throw new ResumeDivergedException(
                $"Replay diverged at decision D-{decisionNumber}: the session paused, " +
                "but the recorded stream holds no resolution for that pause. The resume is abandoned; " +
                "the suspended record and transcript are untouched.");
        return queue.Dequeue();
    }
}

/// <summary>
/// A seat channel that answers from the recorded choice stream instead
/// of a player. Blocks that expect no reply (briefings, the game-over
/// notice) are accepted and ignored; a prompt pops the seat's next
/// recorded attempt. Every prompt it is handed is kept, so a test (or
/// a host auditing a resume) can compare the replay's prompts against
/// the original transcript's byte for byte.
/// </summary>
internal sealed class ReplayChannel : IAgentChannel
{
    private readonly string _seat;
    private readonly ReplayDriver _driver;
    private readonly List<string> _receivedPrompts = new();

    internal ReplayChannel(string seat, ReplayDriver driver)
    {
        _seat = seat;
        _driver = driver;
        DisplayName = $"replay seat {seat}";
    }

    public string DisplayName { get; }

    /// <summary>Every decision prompt handed to this channel during the replay, in order.</summary>
    public IReadOnlyList<string> ReceivedPrompts => _receivedPrompts;

    internal void NotePrompt(string prompt) => _receivedPrompts.Add(prompt);

    public void Send(string text)
    {
        // Briefings and game-over blocks during a replay are the
        // session re-deriving what the file already holds; there is no
        // player to show them to.
    }

    public string? Ask(string promptText)
    {
        return _driver.NextAttempt(_seat, promptText) switch
        {
            RecordedAttempt.Reply reply => reply.Text,
            RecordedAttempt.NoReply => null,
            RecordedAttempt.ChannelFailed failed => throw new ChannelDisconnectedException(failed.Message),
            _ => throw new InvalidOperationException("Unknown recorded attempt."),
        };
    }

    public void Resend(string promptText)
    {
        // A poke has no replay meaning: the recorded answer already
        // stands in the stream.
    }
}
