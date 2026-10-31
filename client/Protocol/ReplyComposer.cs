namespace AgentRisk.Client.Protocol;

/// <summary>
/// Composes the one-line replies a seat sends, in exactly the grammar the
/// prompt offered. Each method is one verb of that grammar;
/// the table's controls gather the parameters, and nothing here or in the
/// controls ever invents a reply the prompt did not offer.
/// </summary>
public static class ReplyComposer
{
    /// <summary>Claim a territory — for oneself, or for the Rebels on their claim turn; the line is the same.</summary>
    public static string Claim(string territoryCode) => $"CLAIM {territoryCode}";

    /// <summary>Setup placement: one army on the named own territory.</summary>
    public static string SetupPlace(string territoryCode) => $"PLC {territoryCode}";

    /// <summary>Place a number of reinforcements on one own territory.</summary>
    public static string Place(string territoryCode, int armies) => $"PLC {territoryCode} {armies}";

    /// <summary>Trade the set the ask enumerated under this number.</summary>
    public static string TradeSet(int setNumber) => $"TRD {setNumber}";

    /// <summary>Decline an optional trade (only ever sent when the ask offered it).</summary>
    public static string TradeNone() => "TRD NONE";

    /// <summary>Declare an attack; mode is the prompt's own word, RND or ALL.</summary>
    public static string Attack(string from, string to, string mode) => $"ATK {from} {to} {mode}";

    /// <summary>End the attack phase.</summary>
    public static string EndAttack() => "ENDATK";

    /// <summary>Roll this many dice — the attacker's next round, or the defender's answer.</summary>
    public static string Dice(int count) => $"DICE {count}";

    /// <summary>Stop the current round-by-round attack (only when the ask offered it).</summary>
    public static string Stop() => "STOP";

    /// <summary>Move this many armies into the captured territory.</summary>
    public static string MoveIn(int armies) => $"MOV {armies}";

    /// <summary>Fortify this many armies along one route through the seat's own land.</summary>
    public static string Fortify(string from, string to, int armies) => $"FRT {from} {to} {armies}";

    /// <summary>Skip the fortify.</summary>
    public static string FortifySkip() => "FRT SKIP";

    /// <summary>Answer a strike-3 prompt with the number of the chosen listed move.</summary>
    public static string NumberedMove(int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
