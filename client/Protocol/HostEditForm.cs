using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentRisk.Client.Protocol;

/// <summary>
/// One row of the host edit form: a territory as the position
/// stands when the form opens, with the name the table shows for it.
/// The owner is null only for a territory nobody has claimed yet
/// (setup); every owned territory names its seat.
/// </summary>
public sealed record HostEditPosition(string Territory, string TerritoryName, string? Owner, int Armies);

/// <summary>
/// One territory change the host edit form produces: the closed set
/// and nothing else — a new owner, a new army count, or both; a null
/// field leaves that part of the territory as it stands. Plain data:
/// the app turns these into the engine's edit entries at the boundary,
/// and the engine validates the set again on its own side regardless.
/// </summary>
public sealed record HostEditChange(string Territory, string? NewOwner, int? NewArmies);

/// <summary>
/// The host edit form's logic, kept in the protocol layer so
/// it is testable headlessly: given the position as it stands and what
/// the host has picked in the form, work out the changes, the plain
/// refusal when a pick steps outside the closed set, the live summary
/// line the host confirms against, and when the editor may open at
/// all. The form holds no state and touches no game; the panel in the
/// app renders it, and the session applies what it produces.
/// </summary>
public static class HostEditForm
{
    /// <summary>The summary line while the form holds no changes.</summary>
    public const string NoChangesSummary = "No changes yet.";

    /// <summary>
    /// Whether the edit surface may open: only at a table this app
    /// hosts, only while the host has the game paused, never once the
    /// game is over, and never twice in one pause (the pause's one
    /// edit already spent). Everything else about the table — whose
    /// turn it is, what decision stands — does not gate the editor.
    /// </summary>
    public static bool EditorAvailable(bool isHost, bool hostPaused, bool gameOver, bool editUsedThisPause) =>
        isHost && hostPaused && !gameOver && !editUsedThisPause;

    /// <summary>
    /// The changes the form's picks make, in board order: a row whose
    /// owner and armies both stand as they were contributes nothing.
    /// A row the form holds no pick for is untouched.
    /// </summary>
    public static List<HostEditChange> BuildChanges(
        IReadOnlyList<HostEditPosition> position,
        IReadOnlyDictionary<string, (string? Owner, int Armies)> chosen)
    {
        var changes = new List<HostEditChange>();
        foreach (var row in position)
        {
            if (!chosen.TryGetValue(row.Territory, out var pick)) continue;
            string? newOwner = pick.Owner != row.Owner ? pick.Owner : null;
            int? newArmies = pick.Armies != row.Armies ? pick.Armies : null;
            if (newOwner is not null || newArmies is not null)
                changes.Add(new HostEditChange(row.Territory, newOwner, newArmies));
        }
        return changes;
    }

    /// <summary>
    /// The first plain refusal among the form's picks, or null when
    /// every pick stands inside the closed set: armies never below
    /// one, and an owner only ever a seat the caller names as legal
    /// (the game's seats, the Rebels included in Duo). The wording is
    /// the modal's own; the session and the engine refuse again on
    /// their side, in theirs.
    /// </summary>
    public static string? Validate(
        IReadOnlyList<HostEditPosition> position,
        IReadOnlyDictionary<string, (string? Owner, int Armies)> chosen,
        IReadOnlyCollection<string> legalOwners)
    {
        foreach (var row in position)
        {
            if (!chosen.TryGetValue(row.Territory, out var pick)) continue;
            if (pick.Armies < 1)
                return $"{row.TerritoryName}: armies must be at least 1.";
            if (pick.Owner is not null && !legalOwners.Contains(pick.Owner))
                return $"{row.TerritoryName}: that owner is not a seat in this game.";
        }
        return null;
    }

    /// <summary>
    /// The live summary line the host confirms against: every pending
    /// change in plain words, each field named ("Quebec: owner Green to
    /// Red, armies 3 to 5", as the record of play words a HOST EDIT),
    /// then what the changes would leave standing — a seat the changes
    /// strip of its last territory is named as holding none, because
    /// the edit eliminates nobody and the host should see the position
    /// they are about to make. Seat and territory names come from the
    /// caller's display names; a null owner reads as "no one".
    /// </summary>
    public static string Summary(
        IReadOnlyList<HostEditPosition> position,
        IReadOnlyList<HostEditChange> changes,
        IReadOnlyList<string> seatOrder,
        Func<string?, string> seatName,
        Func<string, string> territoryName)
    {
        if (changes.Count == 0) return NoChangesSummary;
        var byCode = position.ToDictionary(p => p.Territory, StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var change in changes)
        {
            var row = byCode[change.Territory];
            var fields = new List<string>();
            if (change.NewOwner is not null)
                fields.Add($"owner {seatName(row.Owner)} to {seatName(change.NewOwner)}");
            if (change.NewArmies is not null)
                fields.Add($"armies {row.Armies} to {change.NewArmies.Value}");
            parts.Add($"{territoryName(change.Territory)}: {string.Join(", ", fields)}");
        }
        string text = string.Join(". ", parts) + ".";

        var before = TerritoryCounts(position, seatOrder);
        var after = new Dictionary<string, int>(before, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var row = byCode[change.Territory];
            if (change.NewOwner is null) continue;
            if (row.Owner is not null) after[row.Owner]--;
            after[change.NewOwner] = after.GetValueOrDefault(change.NewOwner) + 1;
        }
        foreach (string seat in seatOrder)
            if (before.GetValueOrDefault(seat) > 0 && after.GetValueOrDefault(seat) == 0)
                text += $" {seatName(seat)} would hold no territories.";
        return text;
    }

    private static Dictionary<string, int> TerritoryCounts(
        IReadOnlyList<HostEditPosition> position, IReadOnlyList<string> seatOrder)
    {
        var counts = seatOrder.ToDictionary(seat => seat, _ => 0, StringComparer.Ordinal);
        foreach (var row in position)
            if (row.Owner is not null)
                counts[row.Owner] = counts.GetValueOrDefault(row.Owner) + 1;
        return counts;
    }
}
