using System.Text;

namespace AgentRisk.Client.Protocol;

/// <summary>
/// The words a host hands to the players filling socket seats.
/// For an Agent seat: a complete instruction block with the address,
/// port, and seat filled in live from the setup form, ready to copy and
/// paste to the agent verbatim. For a Client seat: the one line the host
/// tells their player. Plain product voice: the block speaks to the
/// agent as a player being seated, and nothing else.
/// </summary>
public static class AgentInstructions
{
    /// <summary>
    /// The full instruction block for one Agent seat. The address is the
    /// one players connect to (the setup form's connect address), never
    /// the host's listen address.
    /// </summary>
    public static string ForAgentSeat(string address, int port, SeatPlan seat)
    {
        var text = new StringBuilder();
        text.Append($"You are being seated in a game of Classic Risk as the {seat.Color} player");
        if (!string.IsNullOrWhiteSpace(seat.Name))
            text.Append($" (the host calls this seat \"{seat.Name.Trim()}\")");
        text.Append(".\n\n");

        text.Append($"Connect to {address}, port {port}. When the host prompts you to join, send exactly this line:\n");
        text.Append($"JOIN {seat.Color}\n\n");

        text.Append("You will first receive one briefing: the board, the seats, and the full reply grammar. ");
        text.Append("After that, every decision arrives as its own prompt. Each prompt contains a summary of ");
        text.Append("everything that has happened since your last decision, including your own actions and all ");
        text.Append("dice results, the full board, and the one decision you are being asked, with the exact ");
        text.Append("reply it expects.\n\n");

        text.Append("You may form a plan and carry it out across successive prompts. Check it against each new ");
        text.Append("summary as it arrives: the board may have changed in ways the plan did not expect.\n\n");

        text.Append("Answering efficiently:\n");
        text.Append("- Reinforcements place in bulk: one PLC reply per territory places any number of troops ");
        text.Append("there. Do not place one army at a time.\n");
        text.Append("- ATK <FROM> <TO> ALL means full send: the attack continues until it resolves or the ");
        text.Append("target falls, with no further attack prompts to you. The defender is still asked for ");
        text.Append("dice, round by round.\n");
        text.Append("- ATK <FROM> <TO> RND asks you round by round instead.\n\n");

        text.Append("A full game is hundreds of decisions. Answer each prompt with exactly one line in the ");
        text.Append("grammar it shows. Read the whole prompt, and decide for yourself: never delegate your ");
        text.Append("decisions to a script or a first-option rule.\n");
        return text.ToString();
    }

    /// <summary>
    /// The one line a host tells the human filling a Client seat. The
    /// address is the one players connect to, as in <see cref="ForAgentSeat"/>.
    /// </summary>
    public static string ForClientSeat(string address, int port, SeatPlan seat) =>
        $"Tell your player to open the app, choose Join a game, enter {address} and port {port}, " +
        $"and pick seat {seat.Color}.";
}
