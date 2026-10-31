namespace AgentRisk.Board;

/// <summary>
/// The picture on a territory card, written INF / CAV / ART in the text
/// protocol. A set is three of a kind, one of each, or any set
/// involving a wild card; the wild cards themselves are counted on
/// <see cref="Board.WildCards"/>, not here.
/// </summary>
public enum CardType
{
    Infantry,
    Cavalry,
    Artillery,
}

/// <summary>Conversions between <see cref="CardType"/> and its three-letter code.</summary>
public static class CardTypeCode
{
    /// <summary>The protocol code for a card type: "INF", "CAV" or "ART".</summary>
    public static string ToCode(this CardType type) => type switch
    {
        CardType.Infantry => "INF",
        CardType.Cavalry => "CAV",
        CardType.Artillery => "ART",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown card type."),
    };

    /// <summary>Parses a protocol code into a <see cref="CardType"/>.</summary>
    /// <exception cref="FormatException">The code is not INF, CAV or ART.</exception>
    public static CardType Parse(string code) => code switch
    {
        "INF" => CardType.Infantry,
        "CAV" => CardType.Cavalry,
        "ART" => CardType.Artillery,
        _ => throw new FormatException($"Unknown card type code \"{code}\"; expected INF, CAV or ART."),
    };

    /// <summary>True when <paramref name="code"/> is one of INF, CAV, ART.</summary>
    public static bool IsValidCode(string? code) => code is "INF" or "CAV" or "ART";
}
