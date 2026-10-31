namespace AgentRisk.Client.Protocol;

/// <summary>
/// The wire tokens of the host's line protocol: the framing
/// markers the socket transport appends to every block, the handshake verb,
/// and the prompt block's own sentinel words. These strings are the contract
/// with the host; they live in one place so a protocol revision touches exactly
/// one file on the client side.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Last line of a host block that expects no reply.</summary>
    public const string EndMarker = "<<END>>";

    /// <summary>Last line of a host block that expects exactly one reply line.</summary>
    public const string ReplyMarker = "<<REPLY>>";

    /// <summary>First word of the one-line handshake that claims a seat.</summary>
    public const string JoinVerb = "JOIN";

    /// <summary>First word of the host's greeting block.</summary>
    public const string GreetingPrefix = "AGENT RISK |";

    /// <summary>First word of the host's answer to a successful JOIN.</summary>
    public const string JoinedPrefix = "JOINED";

    /// <summary>First word of the host's answer to a refused JOIN; the connection is then closed.</summary>
    public const string ErrorPrefix = "ERROR |";

    /// <summary>Owner token the host writes for a territory nobody has claimed yet.</summary>
    public const string UnclaimedOwnerToken = "NONE";

    /// <summary>Token on the YOUR HAND / REIN OWED lines when there is nothing to show.</summary>
    public const string NoneToken = "none";

    /// <summary>Section header above the round summary in a decision prompt.</summary>
    public const string SummaryHeader = "--- SUMMARY ---";

    /// <summary>Section header above the board block in a decision prompt.</summary>
    public const string BoardHeader = "--- BOARD ---";

    /// <summary>Section header above the decision ask in a decision prompt.</summary>
    public const string DecisionHeader = "--- DECISION ---";

    /// <summary>First line of the one-time briefing block.</summary>
    public const string BriefingPrefix = "BRIEFING |";

    /// <summary>First line of a decision prompt.</summary>
    public const string DecisionPrefix = "DECISION D-";

    /// <summary>First line of the strike-3 prompt: nothing but the numbered legal moves.</summary>
    public const string StrikeThreePrefix = "Choose one, reply with its number:";

    /// <summary>First line of the block that ends the game.</summary>
    public const string GameOverPrefix = "GAME OVER";

    /// <summary>
    /// Lead-in of the line a re-prompt carries when the previous reply was
    /// rejected; the remainder of the line is the rejection reason.
    /// </summary>
    public const string IllegalReplyPrefix = "Your previous reply was not legal: ";
}
