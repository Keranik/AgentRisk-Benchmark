using System.Collections.Generic;
using System.Linq;

namespace AgentRisk.Client.Protocol;

/// <summary>One territory's live state from the board block: owner seat code (null while unclaimed) and armies.</summary>
public sealed record TerritoryState(string Code, string? Owner, int Armies);

/// <summary>One seat's public line: territory count and card count. Faces are never public.</summary>
public sealed record PlayerLine(string Code, int Territories, int Cards);

/// <summary>
/// The board exactly as one prompt states it: the header counters,
/// every territory's owner and armies, every seat's public counts, the
/// prompted seat's own hand, and the reinforcements still owed. Raw state
/// only — nothing in this model is derived beyond what the block prints.
/// </summary>
public sealed class BoardState
{
    public required int Round { get; init; }

    /// <summary>Seat whose turn it is (during setup, the seat the table is waiting on).</summary>
    public required string TurnSeat { get; init; }

    /// <summary>Phase token from the board block: SETUP, REIN, ATK, FRT, or REB.</summary>
    public required string Phase { get; init; }

    /// <summary>Value of the next set traded in the game.</summary>
    public required int NextTradeValue { get; init; }

    /// <summary>All territories, keyed by code.</summary>
    public required IReadOnlyDictionary<string, TerritoryState> Territories { get; init; }

    /// <summary>Continent code to its territory codes, in the block's printed order.</summary>
    public required IReadOnlyList<(string ContinentCode, int Bonus, IReadOnlyList<string> Members)> ContinentGroups { get; init; }

    /// <summary>Every seat's public counts, in the block's printed order.</summary>
    public required IReadOnlyList<PlayerLine> Players { get; init; }

    /// <summary>The prompted seat's own card faces (ids like ONT-INF, or WLD). Empty when the hand is empty.</summary>
    public required IReadOnlyList<string> Hand { get; init; }

    /// <summary>Armies still to place during REIN, or null when the block says none.</summary>
    public required int? ReinOwed { get; init; }

    /// <summary>All territories a seat owns, in board order.</summary>
    public IEnumerable<TerritoryState> OwnedBy(string seat) =>
        Territories.Values.Where(t => t.Owner == seat);

    /// <summary>All territories nobody has claimed yet.</summary>
    public IEnumerable<TerritoryState> Unclaimed() =>
        Territories.Values.Where(t => t.Owner is null);
}
