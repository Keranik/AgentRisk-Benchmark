namespace AgentRisk.Host;

/// <summary>Raised when a seat's channel can no longer carry text (socket closed, stream failed).</summary>
public sealed class ChannelDisconnectedException : Exception
{
    public ChannelDisconnectedException(string message) : base(message) { }
    public ChannelDisconnectedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// One seat's text transport. The prompt contract is identical over
/// every implementation; channels translate transport only.
///
/// Two shapes, because the protocol has exactly two: a block that
/// expects no reply (briefing, game-over notice) and a prompt that expects
/// exactly one reply line (a decision). Keeping the distinction in the
/// interface lets the socket transport frame the two shapes differently
/// while in-process seats ignore framing entirely. <see cref="Resend"/>
/// is the prompt shape repeated, for a host poke.
/// </summary>
public interface IAgentChannel
{
    /// <summary>Human-facing name for host logs (e.g. "socket 203.0.113.7" or "scripted").</summary>
    string DisplayName { get; }

    /// <summary>Sends a block that expects no reply.</summary>
    void Send(string text);

    /// <summary>
    /// Sends a prompt block and returns the seat's single reply line, or
    /// null when the seat closed the channel without answering.
    /// </summary>
    /// <exception cref="ChannelDisconnectedException">The transport failed mid-exchange.</exception>
    string? Ask(string promptText);

    /// <summary>
    /// Re-sends a prompt whose reply is still outstanding (a host
    /// poke): the identical text, with the same reply framing, so a
    /// seat that missed the first sending — or whose operator needs a
    /// nudge — sees the question again. No reply is read here; the
    /// outstanding <see cref="Ask"/> collects the seat's answer,
    /// whichever sending it answers. If the seat answers twice, the
    /// duplicate is a stale reply and the transport discards it when
    /// the next prompt goes out (the stale-reply rule).
    /// </summary>
    /// <exception cref="ChannelDisconnectedException">The transport failed mid-exchange.</exception>
    void Resend(string promptText);
}
