using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentRisk.Client.Protocol;

/// <summary>One record-of-play line in plain words, with the seat it is about (for its colour mark), if any.</summary>
public sealed record NarratedLine(string? Actor, string Text);

/// <summary>The names a narration needs, looked up from the table's own catalog.</summary>
public sealed record NarrationNames(
    Func<string, string> Territory,
    Func<string, string> Seat,
    Func<string, string> Continent,
    Func<string, string> Card);

/// <summary>
/// The record of play in plain words. Every engine event line
/// and every host event the feed carries is turned into one sentence a
/// reader can follow without knowing the protocol: <c>YEL ATK ONT&gt;QUE ALL</c>
/// becomes "Yellow attacks Quebec from Ontario, until it resolves."
///
/// The narration restates the line and adds nothing: no totals, no
/// judgments, no derived state. A line it does not recognise
/// comes back exactly as written, so a format change shows up as a raw
/// line rather than a wrong sentence. The protocol text stays one click
/// away on the table, for readers who want what the seats were sent.
/// </summary>
public static class EventNarration
{
    private const string Seat = "[A-Z]{3}";
    private const string Code = "[A-Z]{3}";

    private static readonly Regex Claim = new($@"^(?<seat>{Seat}) CLAIM (?<t>{Code})(?: BY (?<by>{Seat}))?$", RegexOptions.Compiled);
    private static readonly Regex Deal = new($@"^DEAL (?<t>{Code}) TO (?<seat>{Seat})$", RegexOptions.Compiled);
    private static readonly Regex Place = new($@"^(?<seat>{Seat}) PLC (?<list>(?:{Code}\+\d+ ?)+?)(?: \((?<tag>setup|match|random)\))?$", RegexOptions.Compiled);
    private static readonly Regex Earn = new($@"^(?<seat>{Seat}) EARN (?<total>\d+)(?<rest>(?: \| [^|]+)*)$", RegexOptions.Compiled);
    private static readonly Regex Trade = new($@"^(?<seat>{Seat}) TRD set#(?<n>\d+)(?: \[(?<cards>[^\]]*)\])? =(?<v>\d+)(?<match>(?: match\+2:[A-Z|]+)*)$", RegexOptions.Compiled);
    private static readonly Regex Attack = new($@"^(?<seat>{Seat}) ATK (?<f>{Code})>(?<t>{Code}) (?<m>RND|ALL)$", RegexOptions.Compiled);
    private static readonly Regex Round = new($@"^RND (?<a>\d)v(?<d>\d) A\[(?<af>[\d ]+)\] D\[(?<df>[\d ]+)\] A-(?<al>\d+) D-(?<dl>\d+)(?: \| CAP (?<ct>{Code}) MOV(?<mv>\d+))?$", RegexOptions.Compiled);
    private static readonly Regex Blitz = new($@"^ALL x(?<r>\d+) A-(?<al>\d+) D-(?<dl>\d+)(?: \| CAP (?<ct>{Code}) MOV(?<mv>\d+))?$", RegexOptions.Compiled);
    private static readonly Regex SeatVerb = new($@"^(?<seat>{Seat}) (?<verb>STOP|ENDATK|CRD)$", RegexOptions.Compiled);
    private static readonly Regex Fortify = new($@"^(?<seat>{Seat}) FRT (?:(?<f>{Code})>(?<t>{Code}) (?<n>\d+)|(?<skip>SKIP))$", RegexOptions.Compiled);
    private static readonly Regex TurnEnd = new($@"^TURN END (?<seat>{Seat})$", RegexOptions.Compiled);
    private static readonly Regex Elimination = new(@"^ELIMINATION: (?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex SeatToken = new(@"\b(?:RED|BLU|GRN|YEL|PNK|BLK|REB)\b", RegexOptions.Compiled);

    // Host events, as the history shows them: the transcript marker
    // without its "### ", then ": " and the payload's first line.
    private static readonly Regex HostPoke = new($@"^HOST POKE seat=(?<seat>{Seat}) decision=D-(?<d>\d+)(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex HostSubstitute = new($@"^HOST SUBSTITUTE seat=(?<seat>{Seat}) decision=D-(?<d>\d+) move=(?<m>\d+)(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex HostReplace = new($@"^HOST REPLACE seat=(?<seat>{Seat})(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex HostSuspend = new(@"^HOST SUSPEND decision=D-(?<d>\d+)(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex HostResumeSuspended = new(@"^HOST RESUME decision=D-(?<d>\d+)(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex HostEdit = new($@"^HOST EDIT territory=(?<t>{Code})(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex HostEditNote = new(@"^HOST EDIT NOTE(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex Paused = new($@"^PAUSED seat=(?<seat>{Seat}) decision=D-(?<d>\d+)(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex ChannelFailed = new($@"^(?<seat>{Seat})(?: D-(?<d>\d+))? CHANNEL FAILED context=\w+(?:: (?<p>.*))?$", RegexOptions.Compiled);
    private static readonly Regex StaleReply = new($@"^(?<seat>{Seat}) D-(?<d>\d+) STALE REPLY DISCARDED(?:: (?<p>.*))?$", RegexOptions.Compiled);
    // The closing lines: the seat's game-over block and the result section.
    private static readonly Regex Winner = new($@"^[Ww]inner: (?<seat>{Seat}|none)$", RegexOptions.Compiled);
    private static readonly Regex ResultKey = new(@"^(?<key>status|end-reason|decisions|ended-utc): (?<value>.+)$", RegexOptions.Compiled);
    private static readonly Regex EditChange = new($@"(?<field>owner|armies) (?<from>\w+)->(?<to>\w+)", RegexOptions.Compiled);

    /// <summary>Narrates one history line; an unrecognised line comes back unchanged, with no actor.</summary>
    public static NarratedLine Describe(string line, NarrationNames names)
    {
        string text = line.Trim();
        Match m;

        if ((m = Claim.Match(text)).Success)
        {
            string seat = m.Groups["seat"].Value;
            string territory = names.Territory(m.Groups["t"].Value);
            return m.Groups["by"].Success
                ? Say(seat, $"{names.Seat(m.Groups["by"].Value)} claims {territory} for {names.Seat(seat)}.")
                : Say(seat, $"{names.Seat(seat)} {Verb(seat, "claims")} {territory}.");
        }
        if ((m = Deal.Match(text)).Success)
            return Say(m.Groups["seat"].Value,
                $"{names.Territory(m.Groups["t"].Value)} is dealt to {names.Seat(m.Groups["seat"].Value)}.");
        if ((m = Place.Match(text)).Success)
            return Say(m.Groups["seat"].Value, DescribePlacement(m, names));
        if ((m = Earn.Match(text)).Success)
            return Say(m.Groups["seat"].Value, DescribeEarnings(m, names));
        if ((m = Trade.Match(text)).Success)
            return Say(m.Groups["seat"].Value, DescribeTrade(m, names));
        if ((m = Attack.Match(text)).Success)
        {
            string how = m.Groups["m"].Value == "ALL" ? "until it resolves" : "round by round";
            return Say(m.Groups["seat"].Value,
                $"{names.Seat(m.Groups["seat"].Value)} {Verb(m.Groups["seat"].Value, "attacks")} {names.Territory(m.Groups["t"].Value)} " +
                $"from {names.Territory(m.Groups["f"].Value)}, {how}.");
        }
        if ((m = Round.Match(text)).Success)
        {
            string sentence = $"Dice {m.Groups["af"].Value.Trim()} against {m.Groups["df"].Value.Trim()}: " +
                              $"the attacker loses {m.Groups["al"].Value}, the defender loses {m.Groups["dl"].Value}.";
            return Say(null, sentence + Capture(m, names));
        }
        if ((m = Blitz.Match(text)).Success)
        {
            int rounds = int.Parse(m.Groups["r"].Value);
            string sentence = $"Resolved in {rounds} {(rounds == 1 ? "round" : "rounds")}: " +
                              $"the attacker loses {m.Groups["al"].Value}, the defender loses {m.Groups["dl"].Value}.";
            return Say(null, sentence + Capture(m, names));
        }
        if ((m = SeatVerb.Match(text)).Success)
        {
            string code = m.Groups["seat"].Value;
            string seat = names.Seat(code);
            return Say(code, m.Groups["verb"].Value switch
            {
                "STOP" => $"{seat} {Verb(code, "stops")} the attack.",
                "ENDATK" => $"{seat} {Verb(code, "ends")} the attack phase.",
                _ => $"{seat} {Verb(code, "draws")} a card.",
            });
        }
        if ((m = Fortify.Match(text)).Success)
        {
            string code = m.Groups["seat"].Value;
            string seat = names.Seat(code);
            return Say(code, m.Groups["skip"].Success
                ? $"{seat} {Verb(code, "does not")} fortify."
                : $"{seat} {Verb(code, "moves")} {Armies(m.Groups["n"].Value)} from {names.Territory(m.Groups["f"].Value)} " +
                  $"to {names.Territory(m.Groups["t"].Value)}.");
        }
        if ((m = TurnEnd.Match(text)).Success)
            return Say(m.Groups["seat"].Value, $"End of {Possessive(names.Seat(m.Groups["seat"].Value))} turn.");
        if ((m = Elimination.Match(text)).Success)
            return Say(null, "Elimination: " + SeatNames(m.Groups["rest"].Value, names));

        return DescribeHostEvent(text, names) ?? DescribeResult(text, names) ?? new NarratedLine(null, line);
    }

    private static NarratedLine? DescribeResult(string text, NarrationNames names)
    {
        Match m;
        if (text == "GAME OVER") return Say(null, "Game over.");
        if ((m = Winner.Match(text)).Success)
            return m.Groups["seat"].Value == "none"
                ? Say(null, "No winner is recorded.")
                : Say(m.Groups["seat"].Value, $"Winner: {names.Seat(m.Groups["seat"].Value)}.");
        if ((m = ResultKey.Match(text)).Success)
        {
            string value = m.Groups["value"].Value.Trim();
            return Say(null, m.Groups["key"].Value switch
            {
                "status" => "Result: " + value switch
                {
                    "Completed" => "completed.",
                    "EndedByHost" => "ended by the host.",
                    "Suspended" => "suspended, to be resumed.",
                    _ => $"{value}.",
                },
                "end-reason" => $"Reason: {value.TrimEnd('.')}.",
                "decisions" => $"Decisions played: {value}.",
                _ => $"Ended at {value} (UTC).",
            });
        }
        return null;
    }

    private static NarratedLine? DescribeHostEvent(string text, NarrationNames names)
    {
        Match m;
        if (text == "HOST PAUSE") return Say(null, "The host paused the game.");
        if (text == "HOST RESUME") return Say(null, "The host resumed the game.");
        if (text == "HOST END") return Say(null, "The host ended the game.");
        if ((m = HostPoke.Match(text)).Success)
            return Say(m.Groups["seat"].Value,
                $"The host poked {names.Seat(m.Groups["seat"].Value)} at decision {m.Groups["d"].Value}: " +
                "the pending prompt was sent again.");
        if ((m = HostSubstitute.Match(text)).Success)
            return Say(m.Groups["seat"].Value,
                $"The host answered for {names.Seat(m.Groups["seat"].Value)} at decision {m.Groups["d"].Value} " +
                $"with listed move {m.Groups["m"].Value}{Tail(m)}");
        if ((m = HostReplace.Match(text)).Success)
            return Say(m.Groups["seat"].Value,
                $"The host changed who plays {names.Seat(m.Groups["seat"].Value)}{Tail(m)}");
        if ((m = HostSuspend.Match(text)).Success)
            return Say(null, $"The host suspended the game at decision {m.Groups["d"].Value}{Tail(m)}");
        if ((m = HostResumeSuspended.Match(text)).Success)
            return Say(null, $"The suspended game resumed at decision {m.Groups["d"].Value}{Tail(m)}");
        if ((m = HostEdit.Match(text)).Success)
        {
            var changes = EditChange.Matches(m.Groups["p"].Value)
                .Select(change => change.Groups["field"].Value == "owner"
                    ? $"owner {OwnerName(change.Groups["from"].Value, names)} to {OwnerName(change.Groups["to"].Value, names)}"
                    : $"armies {change.Groups["from"].Value} to {change.Groups["to"].Value}")
                .ToList();
            string what = changes.Count > 0 ? $": {string.Join(", ", changes)}." : ".";
            return Say(null, $"The host edited {names.Territory(m.Groups["t"].Value)}{what}");
        }
        if ((m = HostEditNote.Match(text)).Success)
            return Say(null, $"Edit note: {SeatNames(m.Groups["p"].Value, names)}");
        if ((m = Paused.Match(text)).Success)
            return Say(m.Groups["seat"].Value,
                $"The game paused: {names.Seat(m.Groups["seat"].Value)} could not answer decision " +
                $"{m.Groups["d"].Value}{Tail(m)}");
        if ((m = ChannelFailed.Match(text)).Success)
        {
            string at = m.Groups["d"].Success ? $" at decision {m.Groups["d"].Value}" : "";
            return Say(m.Groups["seat"].Value,
                $"{Possessive(names.Seat(m.Groups["seat"].Value))} connection failed{at}{Tail(m)}");
        }
        if ((m = StaleReply.Match(text)).Success)
            return Say(m.Groups["seat"].Value,
                $"A late reply from {names.Seat(m.Groups["seat"].Value)} for decision {m.Groups["d"].Value} " +
                $"was set aside{Tail(m)}");
        return null;
    }

    private static string DescribePlacement(Match m, NarrationNames names)
    {
        var parts = m.Groups["list"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Split('+'))
            .Select(pair => (Territory: names.Territory(pair[0]), Count: pair[1]))
            .ToList();
        var phrases = parts.Select((part, i) => i == 0
            ? $"{Armies(part.Count)} on {part.Territory}"
            : $"{part.Count} on {part.Territory}").ToList();
        string tag = m.Groups["tag"].Value switch
        {
            "setup" => " (setup)",
            "match" => " (the card's territory bonus)",
            "random" => " (placed at random)",
            _ => "",
        };
        string code = m.Groups["seat"].Value;
        return $"{names.Seat(code)} {Verb(code, "places")} {JoinAnd(phrases)}{tag}.";
    }

    private static string DescribeEarnings(Match m, NarrationNames names)
    {
        var sources = new List<string>();
        foreach (string part in m.Groups["rest"].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var territories = Regex.Match(part, @"^terr (?<n>\d+) -> (?<y>\d+)$");
            var trade = Regex.Match(part, @"^trade \+(?<v>\d+) \(set#(?<s>\d+)\)$");
            var continent = Regex.Match(part, @"^(?<c>[A-Z]{2}) \+(?<b>\d+)$");
            if (territories.Success)
            {
                string held = territories.Groups["n"].Value;
                sources.Add($"{territories.Groups["y"].Value} for holding {held} {(held == "1" ? "territory" : "territories")}");
            }
            else if (trade.Success)
                sources.Add($"{trade.Groups["v"].Value} for trading in set #{trade.Groups["s"].Value}");
            else if (continent.Success)
                sources.Add($"{continent.Groups["b"].Value} for holding {names.Continent(continent.Groups["c"].Value)}");
            else
                sources.Add(part);
        }
        string code = m.Groups["seat"].Value;
        string seat = $"{names.Seat(code)} {Verb(code, "receives")}";
        string total = Armies(m.Groups["total"].Value);
        return sources.Count == 0
            ? $"{seat} {total}."
            : $"{seat} {total}: {JoinAnd(sources)}.";
    }

    private static string DescribeTrade(Match m, NarrationNames names)
    {
        string code = m.Groups["seat"].Value;
        string seat = $"{names.Seat(code)} {Verb(code, "trades")}";
        // The cards follow the total, each named with its type in
        // brackets: "for 15 armies: Ontario (Infantry), Siam (Infantry)
        // and Kamchatka (Infantry)."
        string cards = m.Groups["cards"].Success
            ? $": {JoinAnd(m.Groups["cards"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(names.Card).ToList())}"
            : "";
        string sentence = $"{seat} in set #{m.Groups["n"].Value} for {Armies(m.Groups["v"].Value)}{cards}.";

        var matches = Regex.Matches(m.Groups["match"].Value, @"match\+2:(?<codes>[A-Z|]+)")
            .Select(match => match.Groups["codes"].Value.Split('|').Select(names.Territory).ToList())
            .ToList();
        if (matches.Count == 1 && matches[0].Count == 1)
            sentence += $" The card bonus of 2 goes on {matches[0][0]}.";
        else if (matches.Count == 1)
            sentence += $" The card bonus of 2 goes on one of {JoinOr(matches[0])}, the trader's choice.";
        else if (matches.Count > 1)
            sentence += $" Card bonuses of 2 go on {JoinAnd(matches.Select(list => JoinOr(list)).ToList())}.";
        return sentence;
    }

    private static string Capture(Match m, NarrationNames names) =>
        m.Groups["ct"].Success
            ? $" {names.Territory(m.Groups["ct"].Value)} is captured; {Armies(m.Groups["mv"].Value)} " +
              (m.Groups["mv"].Value == "1" ? "moves in." : "move in.")
            : "";

    private static NarratedLine Say(string? actor, string sentence) => new(actor, Capitalize(sentence));

    private static string Tail(Match m)
    {
        string payload = m.Groups["p"].Success ? m.Groups["p"].Value.Trim() : "";
        return payload.Length == 0 ? "." : $": {payload.TrimEnd('.')}.";
    }

    private static string OwnerName(string code, NarrationNames names) =>
        code == "NONE" ? "no one" : names.Seat(code);

    private static string SeatNames(string text, NarrationNames names) =>
        SeatToken.Replace(text, match => names.Seat(match.Value));

    /// <summary>The verb as it agrees with the seat: the Rebels are plural ("the Rebels place").</summary>
    private static string Verb(string seatCode, string singular) =>
        seatCode != "REB" ? singular
        : singular == "does not" ? "do not"
        : singular.TrimEnd('s');

    private static string Armies(string count) => count == "1" ? "1 army" : $"{count} armies";

    private static string Possessive(string name) => name.EndsWith('s') ? $"{name}'" : $"{name}'s";

    private static string Capitalize(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static string JoinOr(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1],
    };
}
