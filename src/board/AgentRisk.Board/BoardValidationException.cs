namespace AgentRisk.Board;

/// <summary>
/// Thrown when board data fails validation. The message lists every problem
/// <see cref="BoardValidator"/> found, one per line, so a bad data file can be
/// fixed in one pass instead of one error at a time.
/// </summary>
public sealed class BoardValidationException : Exception
{
    public BoardValidationException(string source, IReadOnlyList<string> problems)
        : base($"Board data from {source} failed validation:\n- {string.Join("\n- ", problems)}")
    {
        Problems = problems;
    }

    /// <summary>Every validation problem found, in the order the checks ran.</summary>
    public IReadOnlyList<string> Problems { get; }
}
