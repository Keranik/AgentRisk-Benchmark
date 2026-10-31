using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentRisk.Client.Protocol;

/// <summary>How the human answers a decision at the table: which control path the decision panel drives.</summary>
public enum DecisionControlPath
{
    /// <summary>Click a territory on the map (the panel's list stays available).</summary>
    TerritoryClick,
    /// <summary>Click a source territory, then a target territory.</summary>
    RouteClick,
    /// <summary>The enumerated set list in the panel.</summary>
    SetList,
    /// <summary>Numbered buttons or a stepper in the panel (dice counts, move counts).</summary>
    Stepper,
    /// <summary>The strike-3 numbered move list.</summary>
    NumberedList,
    /// <summary>The raw reply line: the fallback for an ask the table cannot classify.</summary>
    RawReply,
}

/// <summary>
/// The guidance banner's words: for every decision the table can
/// ask, one short imperative for the human whose decision it is, and one
/// plain phrase for what another seat is being waited on for. The lines
/// teach actions only — what can be done and how to do it with the
/// controls in front of the player, never what would be wise (the table gives no hints
/// to humans, exactly as it gives none to agents).
/// </summary>
public static class Guidance
{
    /// <summary>
    /// The engine decision kinds (AgentRisk.Engine.DecisionKind) the
    /// guidance covers, by name. The protocol layer stays free of the
    /// engine assembly, so the names are pinned here and cross-checked
    /// against the real enum by the app's self-check and the test suite:
    /// an engine kind with no guidance fails both.
    /// </summary>
    public static readonly IReadOnlyList<string> CoveredDecisionKinds = new[]
    {
        "Claim", "ClaimForRebels", "SetupPlace", "Trade", "Place",
        "AttackDeclare", "AttackDice", "DefendDice", "CaptureMove", "Fortify",
    };

    /// <summary>The instruction when the strike-3 list is what the human faces.</summary>
    public const string StrikeThreeInstruction = "Two replies did not parse. Pick one of the listed moves.";

    /// <summary>What a seat facing the strike-3 list is being waited on for.</summary>
    public const string StrikeThreeWaitingPhrase = "pick one of the listed moves";

    /// <summary>The one imperative for the human whose decision this is.</summary>
    public static string InstructionFor(DecisionAsk ask) => InstructionFor(ask, code => code);

    /// <summary>The one imperative, with territory codes spelled as board names where a line names one.</summary>
    public static string InstructionFor(DecisionAsk ask, Func<string, string> territoryName) => ask switch
    {
        DecisionAsk.Claim { ForRebels: true } => "Click a territory, then press the Claim button to give it to the Rebels.",
        DecisionAsk.Claim => "Click a territory, then press the Claim button.",
        DecisionAsk.SetupPlace => "Click one of your territories, then press the Place button.",
        DecisionAsk.Trade { Forced: true } => "FORCED: you must trade a set now. Click a set to trade it.",
        DecisionAsk.Trade => "Click a set to trade it, or click Keep my cards.",
        DecisionAsk.Place place => $"Click a territory, set how many, then press the Place button. {place.Remaining} left to place.",
        DecisionAsk.MatchPlace { IsChoice: true } match =>
            $"Your traded cards picture {JoinAnd(match.Candidates.Select(territoryName).ToList())}: the {match.Count}-army " +
            "card bonus goes on one of them, once this turn. Click the one you choose.",
        DecisionAsk.MatchPlace match =>
            $"The card bonus goes on {territoryName(match.Territory)}. Click it to place the {match.Count} troops.",
        DecisionAsk.AttackDeclare { HasAttacks: true } =>
            "Click one of your territories, then the territory to attack, then choose Attack: round by round or Attack: until it resolves. Or click End attack phase.",
        DecisionAsk.AttackDeclare => "No attack is open to you. Click End attack phase.",
        DecisionAsk.AttackDice => "Choose how many dice to use.",
        DecisionAsk.DefendDice => "Choose how many dice to defend with.",
        DecisionAsk.CaptureMove => "Choose how many troops move in.",
        DecisionAsk.Fortify => "Click one of your territories, then one it connects to through your own land, set how many, then press Move. Or click Skip the fortify.",
        _ => "Read the question below and answer with one line.",
    };

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>What a seat deciding this is being waited on for, mid-sentence after "to".</summary>
    public static string WaitingPhraseFor(DecisionAsk ask) => ask switch
    {
        DecisionAsk.Claim { ForRebels: true } => "claim a territory for the Rebels",
        DecisionAsk.Claim => "claim a territory",
        DecisionAsk.SetupPlace => "place a troop",
        DecisionAsk.Trade { Forced: true } => "trade a set (forced)",
        DecisionAsk.Trade => "choose whether to trade a set",
        DecisionAsk.Place => "place reinforcements",
        DecisionAsk.MatchPlace => "place the territory-match bonus",
        DecisionAsk.AttackDeclare => "declare an attack or end the attack phase",
        DecisionAsk.AttackDice => "choose how many dice to use",
        DecisionAsk.DefendDice => "choose how many dice to defend with",
        DecisionAsk.CaptureMove => "choose how many troops move in",
        DecisionAsk.Fortify => "fortify or skip",
        _ => "answer the table's question",
    };

    /// <summary>The control path the decision panel drives for this ask.</summary>
    public static DecisionControlPath ControlPathFor(DecisionAsk ask) => ask switch
    {
        DecisionAsk.Claim => DecisionControlPath.TerritoryClick,
        DecisionAsk.SetupPlace => DecisionControlPath.TerritoryClick,
        DecisionAsk.Trade => DecisionControlPath.SetList,
        DecisionAsk.Place => DecisionControlPath.TerritoryClick,
        DecisionAsk.MatchPlace => DecisionControlPath.TerritoryClick,
        DecisionAsk.AttackDeclare => DecisionControlPath.RouteClick,
        DecisionAsk.AttackDice => DecisionControlPath.Stepper,
        DecisionAsk.DefendDice => DecisionControlPath.Stepper,
        DecisionAsk.CaptureMove => DecisionControlPath.Stepper,
        DecisionAsk.Fortify => DecisionControlPath.RouteClick,
        _ => DecisionControlPath.RawReply,
    };
}
