using System.Collections.Generic;

namespace AgentRisk.Client.Protocol;

/// <summary>One block from the host, classified. The socket framing is already stripped.</summary>
public abstract record PromptMessage
{
    /// <summary>The greeting that opens a connection: which seats can still be claimed.</summary>
    public sealed record Greeting(IReadOnlyList<string> OpenSeats, string RawText) : PromptMessage;

    /// <summary>The host accepted the JOIN; this connection holds the named seat.</summary>
    public sealed record Joined(string SeatCode) : PromptMessage;

    /// <summary>The host refused the JOIN (or the handshake); the connection is closing.</summary>
    public sealed record Refused(string RawText) : PromptMessage;

    /// <summary>The one-time printed-board reference, sent before the first decision.</summary>
    public sealed record Briefing(string SeatCode, string ModeLine, BoardCatalog Catalog, string RawText) : PromptMessage;

    /// <summary>A full decision prompt: header, summary, board, and the ask.</summary>
    public sealed record Decision(DecisionPrompt Prompt) : PromptMessage;

    /// <summary>The strike-3 prompt: only the numbered legal moves.</summary>
    public sealed record StrikeThree(IReadOnlyList<string> Moves) : PromptMessage;

    /// <summary>The game is over; the block names the winner.</summary>
    public sealed record GameOver(string Winner, string RawText) : PromptMessage;

    /// <summary>Anything else the host sent, kept verbatim so the table stays usable if formats drift.</summary>
    public sealed record Unknown(string RawText, bool ExpectsReply) : PromptMessage;
}

/// <summary>A full decision prompt, parsed into the parts the table draws.</summary>
public sealed class DecisionPrompt
{
    public required int Number { get; init; }
    public required string Seat { get; init; }
    public required int Round { get; init; }

    /// <summary>Phase token from the prompt header (SETUP, REIN, ATK, FRT).</summary>
    public required string Phase { get; init; }

    /// <summary>Event lines since this seat's last decision, its own actions included.</summary>
    public required IReadOnlyList<string> SummaryLines { get; init; }

    /// <summary>The board as this prompt states it.</summary>
    public required BoardState Board { get; init; }

    /// <summary>The decision being asked, in the shape its ask declared.</summary>
    public required DecisionAsk Ask { get; init; }

    /// <summary>Why the previous reply was rejected, when this prompt is a re-prompt; otherwise null.</summary>
    public string? LastError { get; init; }

    /// <summary>The ask's own lines, verbatim — the fallback surface whenever parsing cannot classify the ask.</summary>
    public required IReadOnlyList<string> AskLines { get; init; }
}

/// <summary>One tradeable set as the trade ask enumerates it: its number, its card ids, its shape in words.</summary>
public sealed record TradeSetOption(int Number, IReadOnlyList<string> Cards, string Description);

/// <summary>
/// The decision a prompt asks for, classified by the ask's own wording.
/// Each variant carries exactly the parameters the prompt
/// printed; the legal choices themselves are read off the board state and
/// the printed board's adjacency, the way a human reads them.
/// </summary>
public abstract record DecisionAsk
{
    /// <summary>Claim one unclaimed territory. ForRebels: the choice names land for the Rebels faction.</summary>
    public sealed record Claim(bool ForRebels) : DecisionAsk;

    /// <summary>Setup placement: one army on one of the seat's own territories.</summary>
    public sealed record SetupPlace(int ArmiesLeftIncludingThis) : DecisionAsk;

    /// <summary>Trade a set, or decline when the ask offers it.</summary>
    public sealed record Trade(IReadOnlyList<TradeSetOption> Sets, bool CanDecline, bool Forced) : DecisionAsk;

    /// <summary>Place reinforcements, this many still to place.</summary>
    public sealed record Place(int Remaining) : DecisionAsk;

    /// <summary>
    /// The territory-match bonus: a fixed placement on a territory the
    /// traded set pictures. One candidate when one pictured territory is
    /// the trader's; several when more are, and the trader chooses which
    /// one takes the bonus (it is paid once a turn, never once per card).
    /// </summary>
    public sealed record MatchPlace(IReadOnlyList<string> Candidates, int Count) : DecisionAsk
    {
        /// <summary>A bonus with exactly one legal home.</summary>
        public MatchPlace(string territory, int count) : this(new[] { territory }, count) { }

        /// <summary>The first candidate: the only one when the card leaves no choice.</summary>
        public string Territory => Candidates[0];

        /// <summary>True when the trader picks among several pictured territories.</summary>
        public bool IsChoice => Candidates.Count > 1;
    }

    /// <summary>Declare an attack or end the phase. HasAttacks is false when the ask says no attack is legal.</summary>
    public sealed record AttackDeclare(bool HasAttacks) : DecisionAsk;

    /// <summary>Roll the next round of the named battle, or stop it when the ask offers STOP.</summary>
    public sealed record AttackDice(string From, string To, int MaxDice, bool CanStop) : DecisionAsk;

    /// <summary>Choose the defending dice against the named attack.</summary>
    public sealed record DefendDice(string Attacker, string From, string To, int MaxDice) : DecisionAsk;

    /// <summary>Move armies into the captured territory, between Min and Max.</summary>
    public sealed record CaptureMove(string From, string To, int Min, int Max) : DecisionAsk;

    /// <summary>Fortify once through the seat's own land, or skip.</summary>
    public sealed record Fortify : DecisionAsk;

    /// <summary>The ask matched no known wording; the raw lines stay available for the fallback surface.</summary>
    public sealed record Unknown : DecisionAsk;
}
