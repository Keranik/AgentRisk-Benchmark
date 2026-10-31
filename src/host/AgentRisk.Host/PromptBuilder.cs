using System.Text;
using AgentRisk.Board;
using AgentRisk.Engine;
using BoardModel = AgentRisk.Board.Board;

namespace AgentRisk.Host;

/// <summary>
/// Builds everything a seat is shown, in the text protocol: the one-time
/// briefing (static rules reference), the board block, the
/// decision prompt, and the strike-3 move list.
///
/// The protocol's boundary is enforced here, in one place: a prompt carries
/// raw state, the seat's own card faces, public counts, the reinforcement
/// breakdown the table ritual entitles the seat to hear, and the legal
/// choices. Nothing derived is ever computed for a seat:
/// no continent-control flags, no threats, no suggestions.
///
/// Newline discipline: every line this builder emits ends in "\n", on
/// every OS. Seat-facing text is a protocol artifact; seats parse it,
/// some with anchored line patterns, so its bytes must not depend on
/// the host. <see cref="StringBuilder.AppendLine(string)"/> emits
/// <see cref="Environment.NewLine"/> ("\r\n" on Windows) and broke the
/// host's own scripted seat on Windows in the field. All assembly here goes through
/// <see cref="AddLine"/> instead.
/// </summary>
public sealed class PromptBuilder
{
    private readonly BoardModel _board;

    public PromptBuilder(BoardModel board) => _board = board ?? throw new ArgumentNullException(nameof(board));

    /// <summary>Appends one line terminated by "\n" — never the OS newline (see the class remarks).</summary>
    private static void AddLine(StringBuilder text, string line) => text.Append(line).Append('\n');

    // ------------------------------------------------------------------
    // Briefing: the printed board, given once
    // ------------------------------------------------------------------

    /// <summary>
    /// The static rules reference sent to a seat before its first decision:
    /// identity, the seats, starting armies, the continent table, every
    /// territory with its borders, and the card/trade reference. All of it is printed-on-the-box knowledge; none of it
    /// describes the current game or any seat's position.
    /// </summary>
    public string Briefing(string seatCode, GameConfig config)
    {
        var text = new StringBuilder();
        AddLine(text, $"BRIEFING | You are {seatCode} in a game of Classic Risk.");
        AddLine(text, config.Mode == GameMode.Duo
            ? $"Mode: Duo. Seats: {string.Join(" ", config.PlayerCodes)}, plus the {RulesConstants.RebelCode} faction."
            : $"Mode: Standard. Seats: {string.Join(" ", config.PlayerCodes)}.");
        int startingArmies = RulesConstants.StartingArmiesByPlayerCount[config.PlayerCodes.Count];
        AddLine(text, config.Mode == GameMode.Duo
            ? $"Starting armies: {startingArmies} per player; the {RulesConstants.RebelCode} faction starts with {(int)(startingArmies * RulesConstants.RebelStartingArmyMultiplier)}."
            : $"Starting armies: {startingArmies} per player.");
        AddLine(text, config.TerritorySetup == TerritorySetup.RandomDeal
            ? "Setup: random deal. The territories are shuffled and dealt to the seats before setup placement; setup placement then proceeds in turn order."
            : "Setup: manual claims. Territories are claimed in turn order before setup placement.");
        AddLine(text, config.Mode == GameMode.Duo
            ? $"Victory: eliminate the other player. The game ends the moment one of the two players holds no territory; the {RulesConstants.RebelCode} faction does not have to be defeated."
            : "Victory: hold every territory on the board.");
        AddLine(text, "Continents (bonus for holding every territory of the continent at the start of your turn):");
        foreach (var continent in _board.Continents)
        {
            AddLine(text, $"[{continent.Code}] {continent.Name}, bonus {continent.Bonus}: {string.Join(" ", continent.Members)}");
        }
        AddLine(text, "Territories and their borders:");
        foreach (var territory in _board.Territories)
        {
            AddLine(text, $"{territory.Code} {territory.Name} ({territory.ContinentCode}) borders: {string.Join(" ", territory.Neighbors)}");
        }
        AddLine(text, "Cards: a set is 3 of a kind, one of each type (INF infantry, CAV cavalry, ART artillery), or any 3 cards including a wild (WLD).");
        AddLine(text, $"Trade-in values by set number traded in the game: {TradeScheduleText()}.");
        AddLine(text, "If a traded card pictures a territory you own, you gain 2 extra armies that must be placed on that territory.");
        text.Append("Your card faces are private to you; every player's card count is public.");
        return text.ToString();
    }

    private static string TradeScheduleText()
    {
        var parts = RulesConstants.TradeValueSchedule
            .Select((value, i) => $"set#{i + 1} = {value}")
            .ToList();
        return string.Join(", ", parts) + $", then +{RulesConstants.TradeValueStepAfterBase} per set";
    }

    // ------------------------------------------------------------------
    // Board block
    // ------------------------------------------------------------------

    /// <summary>The phase token for a decision kind.</summary>
    public static string PhaseOf(DecisionKind kind) => kind switch
    {
        DecisionKind.Claim or DecisionKind.ClaimForRebels or DecisionKind.SetupPlace => "SETUP",
        DecisionKind.Trade or DecisionKind.Place => "REIN",
        DecisionKind.AttackDeclare or DecisionKind.AttackDice or DecisionKind.DefendDice
            or DecisionKind.CaptureMove => "ATK",
        DecisionKind.Fortify => "FRT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The full board block for one seat's prompt. Every territory always
    /// appears, in board order under its continent header; the seat's
    /// own hand is included and no other seat's faces ever are.
    /// </summary>
    public string BoardBlock(GameSnapshot snapshot, PendingDecision decision, string seatCode)
    {
        var text = new StringBuilder();
        // During setup there is no turn seat yet; the header names the seat
        // being asked, which is the seat the table is waiting on.
        string turn = snapshot.CurrentTurnSeat ?? decision.Seat;
        AddLine(text, $"BOARD round={snapshot.Round} turn={turn} phase={PhaseOf(decision.Kind)} next_trade={snapshot.NextTradeValue}");

        var byCode = snapshot.Territories.ToDictionary(t => t.Code, StringComparer.Ordinal);
        foreach (var continent in _board.Continents)
        {
            AddLine(text, $"[{continent.Code} bonus={continent.Bonus}]");
            foreach (string code in continent.Members)
            {
                var territory = byCode[code];
                AddLine(text, $"{code} {territory.Owner ?? HostConstants.UnclaimedOwnerToken} {territory.Armies}");
            }
        }

        var players = snapshot.Players.Select(p => $"{p.Code} terr={p.TerritoryCount} cards={p.Hand.Count}");
        AddLine(text, $"PLAYERS: {string.Join(" | ", players)}");

        var me = snapshot.Players.Single(p => p.Code == seatCode);
        AddLine(text, me.Hand.Count > 0
            ? $"YOUR HAND: {string.Join(" ", me.Hand)}"
            : $"YOUR HAND: {HostConstants.EmptyHandToken}");

        int owed = decision is PlaceDecision place ? place.ArmiesLeft : 0;
        text.Append(owed > 0 ? $"REIN OWED: {owed}" : "REIN OWED: none");
        return text.ToString();
    }

    // ------------------------------------------------------------------
    // Decision prompts
    // ------------------------------------------------------------------

    /// <summary>
    /// The full decision prompt: identity header, the round summary since
    /// the seat's last decision (its own actions included, game-log lines
    /// verbatim), the board block, and the decision with its reply grammar.
    /// On a re-prompt, <paramref name="lastError"/> carries the rejection
    /// reason from the previous attempt.
    /// </summary>
    public string FullPrompt(
        RiskGame game,
        GameSnapshot snapshot,
        PendingDecision decision,
        int decisionNumber,
        IReadOnlyList<string> summaryLines,
        string? lastError)
    {
        var text = new StringBuilder();
        AddLine(text, $"DECISION D-{decisionNumber} | You are {decision.Seat} | Round {snapshot.Round} | Phase {PhaseOf(decision.Kind)}");
        AddLine(text, "--- SUMMARY ---");
        if (summaryLines.Count == 0)
            AddLine(text, "(nothing since your last decision)");
        else
            foreach (string line in summaryLines) AddLine(text, line);
        AddLine(text, "--- BOARD ---");
        AddLine(text, BoardBlock(snapshot, decision, decision.Seat));
        AddLine(text, "--- DECISION ---");
        if (lastError is not null)
            AddLine(text, $"Your previous reply was not legal: {lastError}");
        text.Append(DecisionAsk(game, snapshot, decision));
        return text.ToString();
    }

    /// <summary>
    /// The strike-3 prompt: ONLY the numbered list of
    /// fully specified legal moves. No board, no summary, no framing.
    /// </summary>
    public string StrikeThreePrompt(IReadOnlyList<string> moves)
    {
        var text = new StringBuilder("Choose one, reply with its number:");
        for (int i = 0; i < moves.Count; i++)
            text.Append($"\n{i + 1}) {moves[i]}");
        return text.ToString();
    }

    /// <summary>The ask text for one decision, with the reply grammar it offers.</summary>
    private string DecisionAsk(RiskGame game, GameSnapshot snapshot, PendingDecision decision) => decision switch
    {
        ClaimDecision claim => JoinLines(
            "Claim one unclaimed territory for yourself.",
            "Reply with exactly one line:",
            "  CLAIM <CODE>",
            $"Example of a valid reply: CLAIM {claim.Unclaimed[0]}"),
        ClaimForRebelsDecision rebels => JoinLines(
            $"It is the {RulesConstants.RebelCode} faction's claim turn. Choose the territory the {RulesConstants.RebelCode} faction receives.",
            "Reply with exactly one line:",
            "  CLAIM <CODE>",
            $"Example of a valid reply: CLAIM {rebels.Unclaimed[0]}"),
        SetupPlaceDecision place => JoinLines(
            $"Setup placement: place 1 army on one of your territories. {place.ArmiesLeft} of your setup armies remain, including this one.",
            "Reply with exactly one line:",
            "  PLC <CODE>",
            $"Example of a valid reply: PLC {place.OwnTerritories[0]}"),
        TradeDecision trade => TradeAsk(game, snapshot, trade),
        PlaceDecision place => place.IsMatchPlacement
            ? (place.MatchCandidates!.Count == 1
                ? JoinLines(
                    $"Place the territory-match bonus: exactly {place.FixedCount} armies on {place.MatchCandidates[0]}, the territory pictured on your traded card.",
                    $"Reply: PLC {place.MatchCandidates[0]} {place.FixedCount}")
                : JoinLines(
                    $"Place the territory-match bonus: exactly {place.FixedCount} armies on ONE of {string.Join(", ", place.MatchCandidates)}, the territories pictured on your traded cards. You choose which one.",
                    "Reply with exactly one line:",
                    string.Join("\n", place.MatchCandidates.Select(code => $"  PLC {code} {place.FixedCount}"))))
            : $"Place reinforcements: {place.ArmiesLeft} remaining. Reply: PLC <CODE> <n>",
        AttackDeclareDecision declare => AttackDeclareAsk(declare),
        AttackDiceDecision dice => JoinLines(
            $"Attack {dice.From} > {dice.To}: roll the next round with 1 to {dice.MaxDice} dice{(dice.CanStop ? ", or stop this attack." : ".")}",
            "Reply with exactly one line:",
            "  DICE <n>" + (dice.CanStop ? "\n  STOP" : "")),
        DefendDiceDecision dice => JoinLines(
            $"{dice.Attacker} attacks {dice.To} from {dice.From}. Defend {dice.To} with 1 to {dice.MaxDice} dice.",
            "Reply with exactly one line:",
            "  DICE <n>"),
        CaptureMoveDecision move => JoinLines(
            $"You captured {move.To}. Move between {move.Min} and {move.Max} armies from {move.From} into {move.To}.",
            "Reply with exactly one line:",
            "  MOV <n>"),
        FortifyDecision => JoinLines(
            "Fortify: move armies from one of your territories to another connected through your own territories (the source keeps at least 1), or skip.",
            "Reply with exactly one line:",
            "  FRT <FROM> <TO> <n>",
            "  FRT SKIP",
            "Example of a valid reply: FRT SKIP"),
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };

    private string TradeAsk(RiskGame game, GameSnapshot snapshot, TradeDecision trade)
    {
        var hand = snapshot.Players.Single(p => p.Code == trade.Seat).Hand;
        var lines = new List<string>();

        if (trade.Context == PlacementContext.TurnStart)
        {
            // The reinforcement breakdown is part of the prompt contract.
            // The EARN event line that carries it into the
            // summary is only emitted when the trade phase closes, so at
            // the trade decision itself the breakdown is stated here, from
            // the engine's own accounting.
            var earn = game.CurrentTurnBreakdown();
            if (earn is not null)
            {
                string continents = earn.Continents.Count == 0
                    ? ""
                    : " | " + string.Join(" | ", earn.Continents.Select(c => $"{c.Continent} +{c.Bonus}"));
                lines.Add($"Your reinforcements this turn, before any trade: {earn.Total} | terr {earn.TerritoryCount} -> {earn.TerritoryYield}{continents}");
            }
        }

        lines.Add($"You hold {hand.Count} cards: {string.Join(" ", hand)}");
        if (trade.Forced)
        {
            lines.Add(trade.Context == PlacementContext.TurnStart
                ? "A trade is FORCED: you hold 5 or more cards at the start of your turn."
                : "A trade is FORCED: you hold 6 or more cards after an inheritance, and must trade down to 4 or fewer.");
        }
        lines.Add("Sets you can trade:");
        for (int i = 0; i < trade.Sets.Count; i++)
            lines.Add($"  {i + 1}) {string.Join(" ", trade.Sets[i])} ({SetDescription(trade.Sets[i])})");
        lines.Add(trade.CanDecline ? "Reply: TRD <n>   or   TRD NONE" : "Reply: TRD <n>");
        return JoinLines(lines.ToArray());
    }

    /// <summary>The protocol's parenthetical for a set: its shape in words.</summary>
    private static string SetDescription(IReadOnlyList<string> set)
    {
        var types = set.Select(id => id == "WLD" ? "WLD" : id[(id.LastIndexOf('-') + 1)..]).ToList();
        if (types.Contains("WLD")) return "wild set";
        return types.Distinct().Count() == 1 ? $"three {types[0]}" : "one of each";
    }

    private static string AttackDeclareAsk(AttackDeclareDecision declare)
    {
        if (declare.Attacks.Count == 0)
        {
            return JoinLines(
                "You have no legal attacks.",
                "Reply with exactly one line:",
                "  ENDATK");
        }
        var first = declare.Attacks[0];
        return JoinLines(
            "Declare an attack, or end the attack phase.",
            "Reply with exactly one line:",
            "  ATK <FROM> <TO> RND",
            "  ATK <FROM> <TO> ALL",
            "  ENDATK",
            $"Example of a valid reply: ATK {first.From} {first.To} RND");
    }

    // ------------------------------------------------------------------
    // Legal moves: the enumeration shared by the strike-3 prompt and the
    // host's substitute control. Every entry is a complete reply line that
    // the normal grammar accepts.
    // ------------------------------------------------------------------

    /// <summary>
    /// Every distinct legal move for a decision, as fully specified reply
    /// lines. Decisions with a numeric parameter are enumerated at each
    /// legal value where the range is small (dice, capture move-ins); for
    /// placements and fortifies, whose counts run into the dozens, each
    /// distinct route is listed once at its maximum legal count, which is
    /// always a legal reply.
    /// </summary>
    public static IReadOnlyList<string> LegalMoves(PendingDecision decision)
    {
        var moves = new List<string>();
        switch (decision)
        {
            case ClaimDecision claim:
                moves.AddRange(claim.Unclaimed.Select(code => $"CLAIM {code}"));
                break;
            case ClaimForRebelsDecision rebels:
                moves.AddRange(rebels.Unclaimed.Select(code => $"CLAIM {code}"));
                break;
            case SetupPlaceDecision place:
                moves.AddRange(place.OwnTerritories.Select(code => $"PLC {code}"));
                break;
            case TradeDecision trade:
                for (int i = 0; i < trade.Sets.Count; i++) moves.Add($"TRD {i + 1}");
                if (trade.CanDecline) moves.Add("TRD NONE");
                break;
            case PlaceDecision place:
                if (place.IsMatchPlacement)
                    moves.AddRange(place.MatchCandidates!.Select(code => $"PLC {code} {place.FixedCount}"));
                else
                    moves.AddRange(place.OwnTerritories.Select(code => $"PLC {code} {place.ArmiesLeft}"));
                break;
            case AttackDeclareDecision declare:
                foreach (var attack in declare.Attacks)
                {
                    moves.Add($"ATK {attack.From} {attack.To} RND");
                    moves.Add($"ATK {attack.From} {attack.To} ALL");
                }
                moves.Add("ENDATK");
                break;
            case AttackDiceDecision dice:
                for (int d = 1; d <= dice.MaxDice; d++) moves.Add($"DICE {d}");
                if (dice.CanStop) moves.Add("STOP");
                break;
            case DefendDiceDecision dice:
                for (int d = 1; d <= dice.MaxDice; d++) moves.Add($"DICE {d}");
                break;
            case CaptureMoveDecision move:
                for (int n = move.Min; n <= move.Max; n++) moves.Add($"MOV {n}");
                break;
            case FortifyDecision fortify:
                foreach (var option in fortify.Moves)
                    moves.Add($"FRT {option.From} {option.To} {option.MaxCount}");
                moves.Add("FRT SKIP");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(decision));
        }
        return moves;
    }

    private static string JoinLines(params string[] lines) => string.Join("\n", lines);
}
