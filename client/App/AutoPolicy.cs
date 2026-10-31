using System.Linq;
using AgentRisk.Client.Protocol;

namespace AgentRisk.Client.App;

/// <summary>
/// A deliberately plain legal-move policy used only by the client's
/// autopilot mode (the headless live-table smoke): it answers every
/// decision with the first legal choice the parsed prompt offers — claim
/// the first open territory, place on the first of its own, end attack
/// phases, skip fortifies. It exists so the whole client path (connect,
/// parse, present, reply) can run a real game end to end without a hand
/// on the mouse. It is a test driver, not a player, and it is never
/// consulted outside autopilot.
/// </summary>
public static class AutoPolicy
{
    /// <summary>Answers a full decision prompt; null when the ask is unrecognized.</summary>
    public static string? Answer(DecisionPrompt prompt, BoardCatalog catalog)
    {
        var board = prompt.Board;
        string seat = prompt.Seat;

        string? FirstOwn() => catalog.Territories
            .Select(t => t.Code)
            .FirstOrDefault(code => board.Territories.TryGetValue(code, out var t) && t.Owner == seat);
        string? FirstUnclaimed() => catalog.Territories
            .Select(t => t.Code)
            .FirstOrDefault(code => board.Territories.TryGetValue(code, out var t) && t.Owner is null);

        return prompt.Ask switch
        {
            DecisionAsk.Claim => FirstUnclaimed() is { } claim ? ReplyComposer.Claim(claim) : null,
            DecisionAsk.SetupPlace => FirstOwn() is { } own ? ReplyComposer.SetupPlace(own) : null,
            DecisionAsk.Trade trade => trade.Sets.Count > 0
                ? ReplyComposer.TradeSet(trade.Sets[0].Number)
                : trade.CanDecline ? ReplyComposer.TradeNone() : null,
            DecisionAsk.Place place => FirstOwn() is { } own ? ReplyComposer.Place(own, place.Remaining) : null,
            DecisionAsk.MatchPlace match => ReplyComposer.Place(match.Territory, match.Count),
            DecisionAsk.AttackDeclare => ReplyComposer.EndAttack(),
            DecisionAsk.AttackDice dice => dice.CanStop ? ReplyComposer.Stop() : ReplyComposer.Dice(dice.MaxDice),
            DecisionAsk.DefendDice dice => ReplyComposer.Dice(dice.MaxDice),
            DecisionAsk.CaptureMove move => ReplyComposer.MoveIn(move.Min),
            DecisionAsk.Fortify => ReplyComposer.FortifySkip(),
            _ => null,
        };
    }

    /// <summary>Answers a strike-3 prompt with its first listed move.</summary>
    public static string AnswerStrikeThree() => ReplyComposer.NumberedMove(1);
}
