namespace AgentRisk.Board;

/// <summary>
/// One continent: its two-letter code, its printed name, the reinforcement bonus
/// for holding all of it, and the codes of its member territories
/// in board order.
/// </summary>
/// <param name="Code">Two-letter continent code, e.g. "NA".</param>
/// <param name="Name">Full name, e.g. "North America".</param>
/// <param name="Bonus">Armies awarded each turn for holding the whole continent.</param>
/// <param name="Members">Codes of the continent's territories.</param>
public sealed record Continent(
    string Code,
    string Name,
    int Bonus,
    IReadOnlyList<string> Members);
