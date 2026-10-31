using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentRisk.Client.Protocol;

/// <summary>
/// Parses the host's blocks into the client's models. Every pattern here
/// mirrors the text the host's prompt builder writes;
/// anything unrecognized degrades to <see cref="PromptMessage.Unknown"/>
/// or <see cref="DecisionAsk.Unknown"/> with the raw text preserved, so a
/// format drift narrows the table's conveniences instead of breaking the
/// seat: the raw ask and a hand-typed reply remain available.
/// </summary>
public static class PromptParser
{
    private static readonly Regex DecisionHeaderPattern = new(
        @"^DECISION D-(\d+) \| You are ([A-Z]{3}) \| Round (\d+) \| Phase ([A-Z]+)$");
    private static readonly Regex BoardHeaderPattern = new(
        @"^BOARD round=(\d+) turn=([A-Z]{3}) phase=([A-Z]+) next_trade=(\d+)$");
    private static readonly Regex ContinentHeaderPattern = new(@"^\[([A-Z]{2}) bonus=(\d+)\]$");
    // The owner token is a seat code (RED, REB, ...) or the host's NONE
    // sentinel for unclaimed land — four letters, so it cannot share the
    // territory code's fixed three-letter shape.
    private static readonly Regex TerritoryLinePattern = new(@"^([A-Z]{3}) (\S+) (\d+)$");
    private static readonly Regex PlayerSegmentPattern = new(@"^([A-Z]{3}) terr=(\d+) cards=(\d+)$");
    private static readonly Regex BriefingHeaderPattern = new(@"^BRIEFING \| You are ([A-Z]{3}) in a game of Classic Risk\.$");
    private static readonly Regex WinnerPattern = new(@"^Winner: ([A-Z]{3})$", RegexOptions.Multiline);
    private static readonly Regex OpenSeatsPattern = new(@"Seats open: ([A-Z ]+)\.");

    /// <summary>Classifies one host block (framing marker already stripped by the transport).</summary>
    public static PromptMessage ParseBlock(string blockText, bool expectsReply)
    {
        string text = blockText.TrimEnd('\n');
        string firstLine = text.Split('\n', 2)[0].TrimEnd('\r');

        if (firstLine.StartsWith(ProtocolConstants.GreetingPrefix, StringComparison.Ordinal))
        {
            var seats = OpenSeatsPattern.Match(text);
            var open = seats.Success
                ? seats.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            return new PromptMessage.Greeting(open, text);
        }

        if (firstLine.StartsWith(ProtocolConstants.JoinedPrefix, StringComparison.Ordinal))
            return new PromptMessage.Joined(firstLine[ProtocolConstants.JoinedPrefix.Length..].Trim());

        if (firstLine.StartsWith(ProtocolConstants.ErrorPrefix, StringComparison.Ordinal))
            return new PromptMessage.Refused(text);

        if (firstLine.StartsWith(ProtocolConstants.BriefingPrefix, StringComparison.Ordinal))
            return ParseBriefing(text);

        if (firstLine.StartsWith(ProtocolConstants.GameOverPrefix, StringComparison.Ordinal))
        {
            var winner = WinnerPattern.Match(text);
            return new PromptMessage.GameOver(winner.Success ? winner.Groups[1].Value : "", text);
        }

        if (firstLine.StartsWith(ProtocolConstants.StrikeThreePrefix, StringComparison.Ordinal))
            return new PromptMessage.StrikeThree(ParseNumberedMoves(text));

        if (firstLine.StartsWith(ProtocolConstants.DecisionPrefix, StringComparison.Ordinal))
            return new PromptMessage.Decision(ParseDecision(text));

        return new PromptMessage.Unknown(text, expectsReply);
    }

    private static PromptMessage ParseBriefing(string text)
    {
        var lines = SplitLines(text);
        var header = BriefingHeaderPattern.Match(lines[0]);
        string seat = header.Success ? header.Groups[1].Value : "";
        string modeLine = lines.FirstOrDefault(l => l.StartsWith("Mode: ", StringComparison.Ordinal)) ?? "";
        return new PromptMessage.Briefing(seat, modeLine, BoardCatalog.FromBriefing(lines), text);
    }

    private static IReadOnlyList<string> ParseNumberedMoves(string text)
    {
        var movePattern = new Regex(@"^\d+\) (.+)$");
        var moves = new List<string>();
        foreach (string line in SplitLines(text).Skip(1))
        {
            var match = movePattern.Match(line);
            if (match.Success) moves.Add(match.Groups[1].Value);
        }
        return moves;
    }

    // ------------------------------------------------------------------
    // Decision prompts
    // ------------------------------------------------------------------

    /// <summary>Parses a full decision prompt into header, summary, board, and ask.</summary>
    public static DecisionPrompt ParseDecision(string text)
    {
        var lines = SplitLines(text);
        var header = DecisionHeaderPattern.Match(lines[0]);
        if (!header.Success)
            throw new FormatException($"Decision prompt header not recognized: '{lines[0]}'");

        int summaryAt = lines.IndexOf(ProtocolConstants.SummaryHeader);
        int boardAt = lines.IndexOf(ProtocolConstants.BoardHeader);
        int decisionAt = lines.IndexOf(ProtocolConstants.DecisionHeader);
        if (summaryAt < 0 || boardAt < 0 || decisionAt < 0)
            throw new FormatException("Decision prompt is missing one of its three sections.");

        var summary = lines.Skip(summaryAt + 1).Take(boardAt - summaryAt - 1).ToList();
        if (summary.Count == 1 && summary[0] == "(nothing since your last decision)")
            summary.Clear();

        var board = ParseBoardBlock(lines.Skip(boardAt + 1).Take(decisionAt - boardAt - 1).ToList());

        var askLines = lines.Skip(decisionAt + 1).ToList();
        string? lastError = null;
        if (askLines.Count > 0 && askLines[0].StartsWith(ProtocolConstants.IllegalReplyPrefix, StringComparison.Ordinal))
        {
            lastError = askLines[0][ProtocolConstants.IllegalReplyPrefix.Length..];
            askLines.RemoveAt(0);
        }

        return new DecisionPrompt
        {
            Number = int.Parse(header.Groups[1].Value),
            Seat = header.Groups[2].Value,
            Round = int.Parse(header.Groups[3].Value),
            Phase = header.Groups[4].Value,
            SummaryLines = summary,
            Board = board,
            Ask = ParseAsk(askLines),
            LastError = lastError,
            AskLines = askLines,
        };
    }

    /// <summary>Parses a board block; its first line is the BOARD header line itself.</summary>
    public static BoardState ParseBoardBlock(IReadOnlyList<string> lines)
    {
        // lines[0] is the BOARD header line; the caller sliced the section
        // starting just after "--- BOARD ---", so the header is included here.
        var header = BoardHeaderPattern.Match(lines[0]);
        if (!header.Success)
            throw new FormatException($"Board header not recognized: '{lines[0]}'");

        var territories = new Dictionary<string, TerritoryState>(StringComparer.Ordinal);
        var groups = new List<(string ContinentCode, int Bonus, IReadOnlyList<string> Members)>();
        var players = new List<PlayerLine>();
        IReadOnlyList<string> hand = Array.Empty<string>();
        int? reinOwed = null;

        string? currentContinent = null;
        int currentBonus = 0;
        var currentMembers = new List<string>();

        void CloseGroup()
        {
            if (currentContinent is not null)
                groups.Add((currentContinent, currentBonus, currentMembers.ToList()));
        }

        foreach (string line in lines.Skip(1))
        {
            var continent = ContinentHeaderPattern.Match(line);
            if (continent.Success)
            {
                CloseGroup();
                currentContinent = continent.Groups[1].Value;
                currentBonus = int.Parse(continent.Groups[2].Value);
                currentMembers = new List<string>();
                continue;
            }

            var territory = TerritoryLinePattern.Match(line);
            if (territory.Success)
            {
                string ownerToken = territory.Groups[2].Value;
                territories[territory.Groups[1].Value] = new TerritoryState(
                    territory.Groups[1].Value,
                    ownerToken == ProtocolConstants.UnclaimedOwnerToken ? null : ownerToken,
                    int.Parse(territory.Groups[3].Value));
                currentMembers.Add(territory.Groups[1].Value);
                continue;
            }

            if (line.StartsWith("PLAYERS: ", StringComparison.Ordinal))
            {
                CloseGroup();
                currentContinent = null;
                foreach (string segment in line["PLAYERS: ".Length..].Split(" | "))
                {
                    var player = PlayerSegmentPattern.Match(segment.Trim());
                    if (player.Success)
                        players.Add(new PlayerLine(
                            player.Groups[1].Value,
                            int.Parse(player.Groups[2].Value),
                            int.Parse(player.Groups[3].Value)));
                }
                continue;
            }

            if (line.StartsWith("YOUR HAND: ", StringComparison.Ordinal))
            {
                string rest = line["YOUR HAND: ".Length..];
                hand = rest == ProtocolConstants.NoneToken
                    ? Array.Empty<string>()
                    : rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                continue;
            }

            if (line.StartsWith("REIN OWED: ", StringComparison.Ordinal))
            {
                string rest = line["REIN OWED: ".Length..];
                reinOwed = rest == ProtocolConstants.NoneToken ? null : int.Parse(rest);
            }
        }
        CloseGroup();

        return new BoardState
        {
            Round = int.Parse(header.Groups[1].Value),
            TurnSeat = header.Groups[2].Value,
            Phase = header.Groups[3].Value,
            NextTradeValue = int.Parse(header.Groups[4].Value),
            Territories = territories,
            ContinentGroups = groups,
            Players = players,
            Hand = hand,
            ReinOwed = reinOwed,
        };
    }

    // ------------------------------------------------------------------
    // The decision ask (the wordings the host's prompt builder writes)
    // ------------------------------------------------------------------

    private static readonly Regex SetupPlacePattern = new(@"(\d+) of your setup armies remain");
    private static readonly Regex MatchPlacePattern = new(@"^Place the territory-match bonus: exactly (\d+) armies on ([A-Z]{3}),");
    private static readonly Regex MatchChoicePattern = new(@"^Place the territory-match bonus: exactly (\d+) armies on ONE of ((?:[A-Z]{3}, )+)the territories pictured");
    private static readonly Regex PlacePattern = new(@"^Place reinforcements: (\d+) remaining\.");
    private static readonly Regex AttackDicePattern = new(@"^Attack ([A-Z]{3}) > ([A-Z]{3}): roll the next round with 1 to (\d+) dice(, or stop this attack\.|\.)$");
    private static readonly Regex DefendDicePattern = new(@"^([A-Z]{3}) attacks ([A-Z]{3}) from ([A-Z]{3})\. Defend [A-Z]{3} with 1 to (\d+) dice\.$");
    private static readonly Regex CaptureMovePattern = new(@"^You captured ([A-Z]{3})\. Move between (\d+) and (\d+) armies from ([A-Z]{3}) into [A-Z]{3}\.$");
    private static readonly Regex SetLinePattern = new(@"^  (\d+)\) (.+) \((.+)\)$");

    /// <summary>Classifies the ask section by its own wording.</summary>
    public static DecisionAsk ParseAsk(IReadOnlyList<string> askLines)
    {
        string first = askLines.Count > 0 ? askLines[0] : "";

        if (first == "Claim one unclaimed territory for yourself.")
            return new DecisionAsk.Claim(ForRebels: false);
        if (first.StartsWith("It is the REB faction's claim turn.", StringComparison.Ordinal))
            return new DecisionAsk.Claim(ForRebels: true);
        if (first.StartsWith("Setup placement: place 1 army on one of your territories.", StringComparison.Ordinal))
        {
            var left = SetupPlacePattern.Match(first);
            return new DecisionAsk.SetupPlace(left.Success ? int.Parse(left.Groups[1].Value) : 1);
        }

        // The bonus with a choice: the set pictured several of the
        // trader's territories, and one of them takes it.
        var matchChoice = MatchChoicePattern.Match(first);
        if (matchChoice.Success)
            return new DecisionAsk.MatchPlace(
                matchChoice.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                int.Parse(matchChoice.Groups[1].Value));

        var matchPlace = MatchPlacePattern.Match(first);
        if (matchPlace.Success)
            return new DecisionAsk.MatchPlace(matchPlace.Groups[2].Value, int.Parse(matchPlace.Groups[1].Value));

        var place = PlacePattern.Match(first);
        if (place.Success)
            return new DecisionAsk.Place(int.Parse(place.Groups[1].Value));

        if (askLines.Any(l => l == "Sets you can trade:"))
            return ParseTradeAsk(askLines);

        if (first == "Declare an attack, or end the attack phase.")
            return new DecisionAsk.AttackDeclare(HasAttacks: true);
        if (first == "You have no legal attacks.")
            return new DecisionAsk.AttackDeclare(HasAttacks: false);

        var attackDice = AttackDicePattern.Match(first);
        if (attackDice.Success)
            return new DecisionAsk.AttackDice(
                attackDice.Groups[1].Value, attackDice.Groups[2].Value,
                int.Parse(attackDice.Groups[3].Value),
                attackDice.Groups[4].Value.StartsWith(","));

        var defendDice = DefendDicePattern.Match(first);
        if (defendDice.Success)
            return new DecisionAsk.DefendDice(
                defendDice.Groups[1].Value, defendDice.Groups[3].Value, defendDice.Groups[2].Value,
                int.Parse(defendDice.Groups[4].Value));

        var capture = CaptureMovePattern.Match(first);
        if (capture.Success)
            return new DecisionAsk.CaptureMove(
                capture.Groups[4].Value, capture.Groups[1].Value,
                int.Parse(capture.Groups[2].Value), int.Parse(capture.Groups[3].Value));

        if (first.StartsWith("Fortify: move armies from one of your territories", StringComparison.Ordinal))
            return new DecisionAsk.Fortify();

        return new DecisionAsk.Unknown();
    }

    private static DecisionAsk ParseTradeAsk(IReadOnlyList<string> askLines)
    {
        var sets = new List<TradeSetOption>();
        bool forced = false;
        bool canDecline = false;

        foreach (string line in askLines)
        {
            if (line.Contains("A trade is FORCED")) forced = true;

            var set = SetLinePattern.Match(line);
            if (set.Success)
            {
                var cards = set.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                sets.Add(new TradeSetOption(int.Parse(set.Groups[1].Value), cards, set.Groups[3].Value));
                continue;
            }

            if (line.StartsWith("Reply: TRD", StringComparison.Ordinal))
                canDecline = line.Contains("TRD NONE");
        }

        return new DecisionAsk.Trade(sets, canDecline, forced);
    }

    private static List<string> SplitLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
}
