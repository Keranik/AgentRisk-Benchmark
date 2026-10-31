namespace AgentRisk.Engine;

/// <summary>Card faces. Wild cards picture no territory.</summary>
public enum CardType
{
    Infantry,
    Cavalry,
    Artillery,
    Wild,
}

/// <summary>
/// One card in the deck. Territory cards carry their territory code and the
/// type the canonical deck assigns to it; wilds have a null territory.
/// The string id is the protocol form: "ONT-INF" for a territory card, "WLD" for a wild.
/// </summary>
public sealed record Card(string? Territory, CardType Type)
{
    public string Id => Territory is null ? "WLD" : $"{Territory}-{TypeCode(Type)}";

    public static string TypeCode(CardType type) => type switch
    {
        CardType.Infantry => "INF",
        CardType.Cavalry => "CAV",
        CardType.Artillery => "ART",
        CardType.Wild => "WLD",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static CardType ParseType(string code) => code switch
    {
        "INF" => CardType.Infantry,
        "CAV" => CardType.Cavalry,
        "ART" => CardType.Artillery,
        "WLD" => CardType.Wild,
        _ => throw new ArgumentException($"Unknown card type code '{code}'.", nameof(code)),
    };

    /// <summary>Parses a protocol card id ("ONT-INF" or "WLD") back into a card.</summary>
    public static Card FromId(string id)
    {
        if (id == "WLD") return new Card(null, CardType.Wild);
        int cut = id.LastIndexOf('-');
        if (cut <= 0) throw new ArgumentException($"Malformed card id '{id}'.", nameof(id));
        return new Card(id[..cut], ParseType(id[(cut + 1)..]));
    }
}

/// <summary>Set detection and the trade-in value schedule.</summary>
public static class CardSets
{
    /// <summary>
    /// A set is exactly three cards: three of a kind, one of each type, or any
    /// trio that includes a wild (a wild stands for whatever completes the set).
    /// </summary>
    public static bool IsSet(IReadOnlyList<Card> trio)
    {
        if (trio.Count != RulesConstants.CardsPerSet) return false;
        var real = trio.Where(c => c.Type != CardType.Wild).Select(c => c.Type).ToList();
        if (real.Count <= 1) return true;              // wilds plus at most one real card: always completable
        int distinct = real.Distinct().Count();
        if (distinct == 1) return true;                // three of a kind (a wild may stand in for the third)
        if (distinct == 3) return true;                // one of each
        // Two distinct real types: a set only when the third card is a wild
        // standing in for the missing type; two-plus-one without a wild is not.
        return real.Count < RulesConstants.CardsPerSet;
    }

    /// <summary>Enumerates every distinct legal set in a hand, as card lists.</summary>
    public static List<Card[]> FindSets(IReadOnlyList<Card> hand)
    {
        var sets = new List<Card[]>();
        for (int i = 0; i < hand.Count; i++)
        for (int j = i + 1; j < hand.Count; j++)
        for (int k = j + 1; k < hand.Count; k++)
        {
            var trio = new[] { hand[i], hand[j], hand[k] };
            if (IsSet(trio)) sets.Add(trio);
        }
        return sets;
    }

    /// <summary>
    /// Trade-in value of the n-th set traded in the game (1-based):
    /// 4, 6, 8, 10, 12, 15, then +5 per set.
    /// </summary>
    public static int TradeValue(int setNumber)
    {
        if (setNumber < 1) throw new ArgumentOutOfRangeException(nameof(setNumber));
        var schedule = RulesConstants.TradeValueSchedule;
        if (setNumber <= schedule.Length) return schedule[setNumber - 1];
        return schedule[^1] + RulesConstants.TradeValueStepAfterBase * (setNumber - schedule.Length);
    }
}
