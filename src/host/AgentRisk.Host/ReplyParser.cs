using System.Globalization;
using AgentRisk.Engine;

namespace AgentRisk.Host;

/// <summary>
/// Thrown when a reply line does not match the grammar the prompt offered.
/// The reason is written for the deciding agent: it is shown back verbatim
/// on the re-prompt.
/// </summary>
public sealed class ReplyFormatException : Exception
{
    public ReplyFormatException(string reason) : base(reason) => Reason = reason;

    /// <summary>Human-readable reason, safe to show the deciding agent.</summary>
    public string Reason { get; }
}

/// <summary>
/// Parses a seat's one-line reply into an engine <see cref="Choice"/>, using
/// exactly the grammar the prompt offered. Parsing is strict
/// and board-free by design: the structure, the counts, and the ranges a
/// decision itself fixes are checked here so the error message can be
/// precise, and everything else (ownership, adjacency, hand contents) is
/// the engine's call — its <see cref="IllegalChoiceException"/> is the
/// second, equally strike-worthy, rejection path in the failure policy.
/// The engine never guesses intent from prose, and neither does this parser.
/// </summary>
public static class ReplyParser
{
    /// <summary>Parses a full-grammar reply for the pending decision.</summary>
    /// <exception cref="ReplyFormatException">The line does not match the offered grammar.</exception>
    public static Choice Parse(PendingDecision decision, string replyLine)
    {
        var tokens = Tokenize(replyLine);
        return decision switch
        {
            ClaimDecision => new ClaimChoice(SingleCode(tokens, "CLAIM")),
            ClaimForRebelsDecision => new ClaimForRebelsChoice(SingleCode(tokens, "CLAIM")),
            SetupPlaceDecision => new SetupPlaceChoice(SingleCode(tokens, "PLC")),
            TradeDecision trade => ParseTrade(trade, tokens),
            PlaceDecision place => ParsePlace(place, tokens),
            AttackDeclareDecision => ParseAttackDeclare(tokens),
            AttackDiceDecision dice => ParseAttackDice(dice, tokens),
            DefendDiceDecision dice => new DefendDiceChoice(DiceCount(tokens, dice.MaxDice)),
            CaptureMoveDecision move => new CaptureMoveChoice(MoveCount(tokens, move)),
            FortifyDecision => ParseFortify(tokens),
            _ => throw new ReplyFormatException($"No reply grammar exists for a {decision.Kind} decision."),
        };
    }

    /// <summary>
    /// Parses a strike-3 reply: nothing but the number of one of
    /// the enumerated moves. The chosen move's own text is then parsed by
    /// <see cref="Parse"/>, so a numbered reply can never smuggle in a move
    /// that was not on the list.
    /// </summary>
    /// <exception cref="ReplyFormatException">The line is not a listed move number.</exception>
    public static Choice ParseNumberedMove(PendingDecision decision, string replyLine, IReadOnlyList<string> moves)
    {
        string trimmed = (replyLine ?? string.Empty).Trim();
        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number < 1 || number > moves.Count)
        {
            throw new ReplyFormatException(
                $"Reply with the number of your chosen move (1 to {moves.Count}).");
        }
        return Parse(decision, moves[number - 1]);
    }

    // ------------------------------------------------------------------
    // Per-decision grammars. Each mirrors the Reply lines of its prompt.
    // ------------------------------------------------------------------

    private static Choice ParseTrade(TradeDecision decision, string[] tokens)
    {
        ExpectVerb(tokens, "TRD");
        if (tokens.Length == 2 && tokens[1] == "NONE") return new TradeNoneChoice();
        if (tokens.Length != 2) throw Bad("TRD", "TRD <n> or TRD NONE");
        int index = Number(tokens[1], "set number");
        if (index < 1 || index > decision.Sets.Count)
            throw new ReplyFormatException($"Set number must be between 1 and {decision.Sets.Count}; you sent {index}.");
        return new TradeChoice(decision.Sets[index - 1]);
    }

    private static Choice ParsePlace(PlaceDecision decision, string[] tokens)
    {
        ExpectVerb(tokens, "PLC");
        if (tokens.Length != 3) throw Bad("PLC", "PLC <CODE> <n>");
        string code = tokens[1];
        int count = Number(tokens[2], "army count");
        if (decision.IsMatchPlacement)
        {
            // The match bonus is a forced count onto a pictured
            // territory; with several pictured, the seat chooses which.
            var candidates = decision.MatchCandidates!;
            if (!candidates.Contains(code) || count != decision.FixedCount!.Value)
                throw new ReplyFormatException(
                    candidates.Count == 1
                        ? $"The match bonus is exactly {decision.FixedCount} armies on {candidates[0]}: reply PLC {candidates[0]} {decision.FixedCount}."
                        : $"The match bonus is exactly {decision.FixedCount} armies on one of {string.Join(", ", candidates)}: reply PLC <CODE> {decision.FixedCount}.");
        }
        else if (count < 1 || count > decision.ArmiesLeft)
        {
            throw new ReplyFormatException($"Place between 1 and {decision.ArmiesLeft} armies; you sent {count}.");
        }
        return new PlaceChoice(code, count);
    }

    private static Choice ParseAttackDeclare(string[] tokens)
    {
        if (tokens.Length == 1 && tokens[0] == "ENDATK") return new EndAttackChoice();
        ExpectVerb(tokens, "ATK");
        if (tokens.Length != 4) throw Bad("ATK", "ATK <FROM> <TO> RND, ATK <FROM> <TO> ALL, or ENDATK");
        return tokens[3] switch
        {
            "RND" => new AttackChoice(tokens[1], tokens[2], AttackMode.Round),
            "ALL" => new AttackChoice(tokens[1], tokens[2], AttackMode.All),
            _ => throw new ReplyFormatException($"Attack mode must be RND or ALL; you sent '{tokens[3]}'."),
        };
    }

    private static Choice ParseAttackDice(AttackDiceDecision decision, string[] tokens)
    {
        if (tokens.Length == 1 && tokens[0] == "STOP")
        {
            if (!decision.CanStop)
                throw new ReplyFormatException("An attack must roll at least one round before it can be stopped.");
            return new AttackStopChoice();
        }
        return new AttackDiceChoice(DiceCount(tokens, decision.MaxDice));
    }

    private static Choice ParseFortify(string[] tokens)
    {
        ExpectVerb(tokens, "FRT");
        if (tokens.Length == 2 && tokens[1] == "SKIP") return new FortifySkipChoice();
        if (tokens.Length != 4) throw Bad("FRT", "FRT <FROM> <TO> <n> or FRT SKIP");
        int count = Number(tokens[3], "army count");
        if (count < 1) throw new ReplyFormatException($"A fortify moves at least 1 army; you sent {count}.");
        return new FortifyChoice(tokens[1], tokens[2], count);
    }

    // ------------------------------------------------------------------
    // Token helpers
    // ------------------------------------------------------------------

    private static string[] Tokenize(string? replyLine)
    {
        // A valid response is exactly one line; the channel layer
        // already deals in single lines, so what remains is whitespace
        // splitting. Token content is strict; spacing between tokens is not.
        var tokens = (replyLine ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw new ReplyFormatException("Empty reply: no line was received.");
        return tokens;
    }

    private static void ExpectVerb(string[] tokens, string verb)
    {
        if (tokens[0] != verb)
            throw new ReplyFormatException($"This decision is answered with {verb}; you sent '{tokens[0]}'.");
    }

    private static string SingleCode(string[] tokens, string verb)
    {
        ExpectVerb(tokens, verb);
        if (tokens.Length != 2) throw Bad(verb, $"{verb} <CODE>");
        return tokens[1];
    }

    private static int DiceCount(string[] tokens, int maxDice)
    {
        ExpectVerb(tokens, "DICE");
        if (tokens.Length != 2) throw Bad("DICE", "DICE <n>");
        int count = Number(tokens[1], "dice count");
        if (count < 1 || count > maxDice)
            throw new ReplyFormatException($"Roll between 1 and {maxDice} dice; you sent {count}.");
        return count;
    }

    private static int MoveCount(string[] tokens, CaptureMoveDecision decision)
    {
        ExpectVerb(tokens, "MOV");
        if (tokens.Length != 2) throw Bad("MOV", "MOV <n>");
        int count = Number(tokens[1], "army count");
        if (count < decision.Min || count > decision.Max)
            throw new ReplyFormatException($"Move between {decision.Min} and {decision.Max} armies; you sent {count}.");
        return count;
    }

    private static int Number(string token, string what)
    {
        // Numbers are decimal with no separators: digits only.
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            throw new ReplyFormatException($"The {what} must be a plain decimal number; you sent '{token}'.");
        return value;
    }

    private static ReplyFormatException Bad(string verb, string grammar) =>
        new($"Reply with exactly one line: {grammar}.");
}
