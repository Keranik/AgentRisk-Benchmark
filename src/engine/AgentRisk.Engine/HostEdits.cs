namespace AgentRisk.Engine;

/// <summary>
/// One territory change inside a host edit: the only
/// two facts about a position a host may touch, who owns a territory and how
/// many armies stand on it. A null field means "leave it as it stands". Cards,
/// the deck, the dice stream and the turn order are outside the edit set by
/// construction: nothing in this type can name them.
/// </summary>
public sealed record TerritoryEdit(string Territory, string? NewOwner, int? NewArmies);

/// <summary>
/// One change the edit actually made, with the before and after values the
/// game record and the transcript carry. The old owner is null only for a
/// territory that stood unclaimed (setup), which the record renders with its
/// unclaimed token.
/// </summary>
public sealed record TerritoryChange(
    string Territory, string? OldOwner, string? NewOwner, int OldArmies, int NewArmies)
{
    /// <summary>True when the edit moved the territory to a different owner.</summary>
    public bool OwnerChanged => OldOwner != NewOwner;

    /// <summary>True when the edit changed the army count.</summary>
    public bool ArmiesChanged => OldArmies != NewArmies;
}

/// <summary>
/// The outcome of an applied host edit: the changes, plus plain-language
/// notes about what the edit did to the position around them (an attack
/// called off, a placement set aside, a seat left holding no territories).
/// The notes are part of the record: they are written as HOST EDIT NOTE
/// lines and carried in the transcript beside the changes.
/// </summary>
public sealed record HostEditResult(IReadOnlyList<TerritoryChange> Changes, IReadOnlyList<string> Notes);

/// <summary>
/// A host edit the engine refused. The refusal carries one plain-language
/// reason the host shows as it stands; a refused edit changed nothing,
/// because validation always runs to completion before any change is made.
/// </summary>
public sealed class HostEditRefusedException : Exception
{
    public HostEditRefusedException(string reason) : base(reason) => Reason = reason;

    /// <summary>The plain-language refusal, safe to show to the host verbatim.</summary>
    public string Reason { get; }
}
