namespace AgentRisk.Host;

/// <summary>
/// A local seat driven by a delegate: the delegate sees the exact prompt
/// text a remote agent would see and returns the reply line, or null to
/// stay silent (a seat that never answers is a real outcome the host's
/// failure policy exists for, so the delegate's type says so). This is
/// how scripted seats sit at the table (tests, smoke games, the host's
/// built-in scripted policy) without a socket: same prompt contract, no
/// transport. Sent blocks are recorded so the host (and tests) can
/// inspect precisely what a seat was shown.
/// </summary>
public sealed class DelegateAgentChannel : IAgentChannel
{
    private readonly Func<string, string?> _responder;
    private readonly List<string> _sentBlocks = new();
    private readonly List<string> _prompts = new();
    private readonly List<string> _resentPrompts = new();

    /// <param name="displayName">Name for host logs.</param>
    /// <param name="responder">Maps a prompt's text to the seat's one-line reply, or null for no reply.</param>
    public DelegateAgentChannel(string displayName, Func<string, string?> responder)
    {
        DisplayName = displayName;
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    public string DisplayName { get; }

    /// <summary>Every no-reply block sent to this seat, in order (briefing first).</summary>
    public IReadOnlyList<string> SentBlocks => _sentBlocks;

    /// <summary>Every decision prompt sent to this seat, in order.</summary>
    public IReadOnlyList<string> Prompts => _prompts;

    /// <summary>
    /// Prompts re-sent to this seat by a host poke, in order. A poke
    /// does not invoke the responder again: the seat's answer comes
    /// through the still-outstanding <see cref="Ask"/>, so the resend is
    /// recorded here and nothing else happens. (Over a socket the same
    /// poke puts the text on the wire a second time; the seat answers
    /// whichever copy it acts on.)
    /// </summary>
    public IReadOnlyList<string> ResentPrompts => _resentPrompts;

    public void Send(string text) => _sentBlocks.Add(text);

    public string? Ask(string promptText)
    {
        _prompts.Add(promptText);
        return _responder(promptText);
    }

    public void Resend(string promptText) => _resentPrompts.Add(promptText);
}
