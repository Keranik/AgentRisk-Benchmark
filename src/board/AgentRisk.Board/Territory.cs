namespace AgentRisk.Board;

/// <summary>
/// One territory on the board. Immutable; <see cref="Neighbors"/> holds the
/// codes of the bordering territories (the sea routes printed on the classic
/// board count as borders).
/// </summary>
/// <param name="Code">Three-letter territory code, e.g. "ONT".</param>
/// <param name="Name">Full name as printed on the board, e.g. "Ontario".</param>
/// <param name="ContinentCode">Code of the continent this territory belongs to.</param>
/// <param name="Neighbors">Codes of bordering territories, sorted by code.</param>
public sealed record Territory(
    string Code,
    string Name,
    string ContinentCode,
    IReadOnlyList<string> Neighbors)
{
    /// <summary>Number of bordering territories (this territory's degree in the board graph).</summary>
    public int Degree => Neighbors.Count;
}
