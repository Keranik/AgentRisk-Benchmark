using System;
using System.Text.RegularExpressions;

namespace AgentRisk.Client.Protocol;

/// <summary>What one entry on the spectator feed is.</summary>
public enum FeedEntryKind
{
    /// <summary>The feed's own current-board entry: payload is a board block.</summary>
    Board,
    /// <summary>A decision prompt the host sent a seat.</summary>
    Prompt,
    /// <summary>The reply a seat gave to a prompt.</summary>
    Reply,
    /// <summary>A prompt that closed with no reply (the seat's channel failed).</summary>
    NoReply,
    /// <summary>The briefing a seat received at the start of a game.</summary>
    Briefing,
    /// <summary>The briefing a seat received when a suspended game resumed.</summary>
    ResumeBriefing,
    /// <summary>The game-over block a seat received.</summary>
    GameOver,
    /// <summary>A host event (pause, poke, substitute, replace, suspend, resume, end).</summary>
    HostEvent,
    /// <summary>The game's closing result section.</summary>
    Result,
    /// <summary>Anything else: kept whole, never guessed at.</summary>
    Unknown,
}

/// <summary>
/// One entry of the spectator feed, parsed from its transcript-format text
/// (the same text the host writes beside the game record). The marker line
/// classifies the entry; the payload is kept whole so the seat panel can
/// show exactly what the host sent and what came back.
/// </summary>
public sealed class FeedEntry
{
    private static readonly Regex OutgoingPattern = new(
        @"^>>> (?:(?<seat>[A-Z]{3}) (?<what>BRIEFING|RESUME BRIEFING|GAME OVER)|(?<pseat>[A-Z]{3}) D-(?<num>\d+) PROMPT attempt=(?<attempt>\d+) strike=(?<strike>\d+)|BOARD)$",
        RegexOptions.Compiled);
    private static readonly Regex ReplyPattern = new(
        @"^<<< (?<seat>[A-Z]{3}) D-(?<num>\d+) (?<what>REPLY|NO-REPLY)$",
        RegexOptions.Compiled);

    public FeedEntryKind Kind { get; private init; }

    /// <summary>The seat the entry concerns, or null for table-wide entries.</summary>
    public string? Seat { get; private init; }

    /// <summary>The decision number of a prompt or reply entry, or null.</summary>
    public int? DecisionNumber { get; private init; }

    /// <summary>The 1-based attempt of a prompt entry, or null.</summary>
    public int? Attempt { get; private init; }

    /// <summary>The first line of the entry, exactly as written.</summary>
    public string Marker { get; private init; } = "";

    /// <summary>Everything after the marker line (may be empty).</summary>
    public string Payload { get; private init; } = "";

    /// <summary>The whole entry text, marker and payload.</summary>
    public string Text { get; private init; } = "";

    /// <summary>The parsed board, when this is a board entry that parses.</summary>
    public BoardState? Board { get; private init; }

    /// <summary>The prompt payload classified by the prompt parser, when this is a prompt entry.</summary>
    public PromptMessage? Block { get; private init; }

    /// <summary>The parsed decision, when this prompt entry carries a full decision prompt.</summary>
    public DecisionPrompt? Prompt => (Block as PromptMessage.Decision)?.Prompt;

    /// <summary>Parses one feed block's text into an entry.</summary>
    public static FeedEntry Parse(string blockText)
    {
        string text = blockText.TrimEnd('\r', '\n');
        int newline = text.IndexOf('\n');
        string marker = (newline < 0 ? text : text[..newline]).TrimEnd('\r');
        string payload = newline < 0 ? "" : text[(newline + 1)..];

        var outgoing = OutgoingPattern.Match(marker);
        if (outgoing.Success)
        {
            if (outgoing.Groups["seat"].Success)
            {
                return new FeedEntry
                {
                    Kind = outgoing.Groups["what"].Value switch
                    {
                        "BRIEFING" => FeedEntryKind.Briefing,
                        "RESUME BRIEFING" => FeedEntryKind.ResumeBriefing,
                        _ => FeedEntryKind.GameOver,
                    },
                    Seat = outgoing.Groups["seat"].Value,
                    Marker = marker,
                    Payload = payload,
                    Text = text,
                };
            }
            if (outgoing.Groups["pseat"].Success)
            {
                PromptMessage? block = null;
                try
                {
                    // Feed prompts always expect a reply (briefings travel as
                    // their own entry kind), so the decision parse applies.
                    block = PromptParser.ParseBlock(payload, expectsReply: true);
                }
                catch (Exception)
                {
                    // The raw payload still stands in the seat panel; a feed
                    // entry that does not parse is shown, never dropped.
                }
                return new FeedEntry
                {
                    Kind = FeedEntryKind.Prompt,
                    Seat = outgoing.Groups["pseat"].Value,
                    DecisionNumber = int.Parse(outgoing.Groups["num"].Value),
                    Attempt = int.Parse(outgoing.Groups["attempt"].Value),
                    Marker = marker,
                    Payload = payload,
                    Text = text,
                    Block = block,
                };
            }
            // >>> BOARD
            BoardState? board = null;
            try
            {
                board = PromptParser.ParseBoardBlock(
                    payload.Replace("\r\n", "\n").Split('\n'));
            }
            catch (Exception)
            {
                // Shown raw, as above.
            }
            return new FeedEntry
            {
                Kind = FeedEntryKind.Board,
                Marker = marker,
                Payload = payload,
                Text = text,
                Board = board,
            };
        }

        var reply = ReplyPattern.Match(marker);
        if (reply.Success)
            return new FeedEntry
            {
                Kind = reply.Groups["what"].Value == "REPLY" ? FeedEntryKind.Reply : FeedEntryKind.NoReply,
                Seat = reply.Groups["seat"].Value,
                DecisionNumber = int.Parse(reply.Groups["num"].Value),
                Marker = marker,
                Payload = payload,
                Text = text,
            };

        if (marker.StartsWith("### ", StringComparison.Ordinal))
            return new FeedEntry { Kind = FeedEntryKind.HostEvent, Marker = marker, Payload = payload, Text = text };

        if (marker == "--- RESULT ---")
            return new FeedEntry { Kind = FeedEntryKind.Result, Marker = marker, Payload = payload, Text = text };

        return new FeedEntry { Kind = FeedEntryKind.Unknown, Marker = marker, Payload = payload, Text = text };
    }
}
