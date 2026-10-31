using System;
using System.Collections.Generic;

namespace AgentRisk.Client.Protocol;

/// <summary>One seat's latest exchange with the host, as the seat panel shows it.</summary>
public sealed class SeatExchange
{
    /// <summary>The marker line of the last prompt sent to this seat.</summary>
    public string? LastPromptMarker { get; internal set; }

    /// <summary>The full text of the last prompt sent to this seat.</summary>
    public string? LastPromptText { get; internal set; }

    /// <summary>The decision number of that prompt.</summary>
    public int? LastDecisionNumber { get; internal set; }

    /// <summary>The decision that prompt asks, when it was a full decision prompt the parser classified.</summary>
    public DecisionAsk? LastAsk { get; internal set; }

    /// <summary>True when that prompt was the strike-3 numbered list of legal moves.</summary>
    public bool LastPromptIsStrikeThree { get; internal set; }

    /// <summary>The 1-based attempt of that prompt (2 and 3 follow an illegal reply), when the feed said.</summary>
    public int? LastAttempt { get; internal set; }

    /// <summary>The reply the seat gave to that prompt (null while it is still thinking).</summary>
    public string? LastReplyText { get; internal set; }

    /// <summary>True once the seat's briefing has been seen on the feed.</summary>
    public bool BriefingSeen { get; internal set; }

    /// <summary>The game-over text the seat received, once the game ended for it.</summary>
    public string? GameOverText { get; internal set; }
}

/// <summary>
/// Tracks, per seat, the last prompt the host sent and the reply it gave —
/// the seat panel's whole content. Fed by the spectator feed in every mode;
/// a joined player whose watch connection failed feeds their own exchanges
/// in directly, so the local seat is always shown.
/// </summary>
public sealed class SeatExchangeTracker
{
    private readonly Dictionary<string, SeatExchange> _seats = new(StringComparer.Ordinal);

    public SeatExchange For(string seatCode) =>
        _seats.TryGetValue(seatCode, out var exchange)
            ? exchange
            : _seats[seatCode] = new SeatExchange();

    public bool TryGet(string seatCode, out SeatExchange exchange) =>
        _seats.TryGetValue(seatCode, out exchange!);

    /// <summary>Every seat the tracker has seen an exchange for.</summary>
    public IEnumerable<string> SeatCodes => _seats.Keys;

    /// <summary>Folds one feed entry into the per-seat record.</summary>
    public void Consume(FeedEntry entry)
    {
        switch (entry.Kind)
        {
            case FeedEntryKind.Prompt when entry.Seat is not null:
                var exchange = For(entry.Seat);
                exchange.LastPromptMarker = entry.Marker;
                exchange.LastPromptText = entry.Payload;
                exchange.LastDecisionNumber = entry.DecisionNumber;
                exchange.LastAsk = entry.Prompt?.Ask;
                exchange.LastPromptIsStrikeThree = entry.Block is PromptMessage.StrikeThree;
                exchange.LastAttempt = entry.Attempt;
                exchange.LastReplyText = null;
                break;
            case FeedEntryKind.Reply when entry.Seat is not null:
                For(entry.Seat).LastReplyText = entry.Payload.Trim();
                break;
            case FeedEntryKind.NoReply when entry.Seat is not null:
                For(entry.Seat).LastReplyText = "(no reply: the connection closed)";
                break;
            case FeedEntryKind.Briefing:
            case FeedEntryKind.ResumeBriefing:
                if (entry.Seat is not null) For(entry.Seat).BriefingSeen = true;
                break;
            case FeedEntryKind.GameOver when entry.Seat is not null:
                For(entry.Seat).GameOverText = entry.Payload;
                break;
        }
    }

    /// <summary>Records the local seat's own prompt, when no feed carries it.</summary>
    public void NoteLocalPrompt(string seatCode, int? decisionNumber, string promptText, DecisionAsk? ask = null)
    {
        var exchange = For(seatCode);
        exchange.LastPromptText = promptText;
        exchange.LastDecisionNumber = decisionNumber;
        exchange.LastAsk = ask;
        exchange.LastPromptIsStrikeThree = false;
        exchange.LastAttempt = null;
        exchange.LastReplyText = null;
    }

    /// <summary>Records the local seat's own reply, when no feed carries it.</summary>
    public void NoteLocalReply(string seatCode, string replyLine) =>
        For(seatCode).LastReplyText = replyLine;
}
