namespace AgentRisk.Engine;

/// <summary>
/// The guarded host state edit: the one audited entry
/// point through which a host may touch a live position. The set of changes
/// is closed: a territory's owner (to a seat of this game, plus the Rebels
/// in Duo) and a territory's army count (never below one). Hands, the deck,
/// the dice stream, the sets-traded count and the turn order are not
/// reachable from here, and callers cannot smuggle extra state in: this
/// method validates the whole edit against the closed set before it applies
/// any part of it, so a refused edit changes nothing.
///
/// An edit never eliminates anyone by itself. A seat whose last territory is
/// given away simply holds no territories; what happens to it next is
/// whatever normal play resolves, and the edit's notes say plainly what the
/// position is. Where the edit breaks an in-flight structure (a battle whose
/// ground moved, a forced match placement on land the seat no longer holds,
/// a placement for a seat with no land), the structure is repaired here,
/// deterministically, and every repair is reported in the result's notes:
/// the same edit applied to the same position always yields the same game,
/// which is what lets suspend/resume replay reproduce an edited game
/// exactly (the choice stream carries the edit; replay applies it here).
/// </summary>
public sealed partial class RiskGame
{
    /// <summary>
    /// Validates and applies one host edit atomically. Throws
    /// <see cref="HostEditRefusedException"/> with a plain-language reason
    /// when the edit steps outside the closed set; on success returns the
    /// changes made and the notes the record carries.
    /// </summary>
    public HostEditResult ApplyHostEdit(IReadOnlyList<TerritoryEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
            throw new HostEditRefusedException("The edit names no territories; nothing was changed.");
        if (_pending is PendingState.GameOver)
            throw new HostEditRefusedException("The game is over; its final position cannot be edited.");

        // --- Validation: the whole closed set, before anything moves. ---
        var seen = new HashSet<string>();
        foreach (var edit in edits)
        {
            if (!seen.Add(edit.Territory))
                throw new HostEditRefusedException($"Territory {edit.Territory} is named twice in one edit.");
            if (!_territories.ContainsKey(edit.Territory))
                throw new HostEditRefusedException($"{edit.Territory} is not a territory on this board.");
            if (edit.NewOwner is null && edit.NewArmies is null)
                throw new HostEditRefusedException($"{edit.Territory}: the edit says nothing to change.");
            if (edit.NewOwner is { } owner && !IsLegalEditOwner(owner))
                throw new HostEditRefusedException(EditOwnerRefusal(owner));
            if (edit.NewArmies is { } armies && armies < RulesConstants.MinArmiesOnTerritory)
                throw new HostEditRefusedException(
                    $"{edit.Territory}: a territory must keep at least {RulesConstants.MinArmiesOnTerritory} army.");
        }

        // --- Diff: entries that restate the position are not changes. ---
        var changes = new List<TerritoryChange>();
        foreach (var edit in edits)
        {
            var state = _territories[edit.Territory];
            string? newOwner = edit.NewOwner ?? state.Owner;
            // An unclaimed territory stands at zero armies; handing it to
            // a seat without naming armies lands it with the floor of one
            // (a claim's landing), never as an owned territory at zero.
            // The change is recorded with the armies it landed, so the
            // record shows the whole of what the edit did.
            int newArmies = edit.NewArmies
                            ?? (state.Owner is null && newOwner is not null
                                ? RulesConstants.MinArmiesOnTerritory
                                : state.Armies);
            if (newOwner != state.Owner || newArmies != state.Armies)
                changes.Add(new TerritoryChange(edit.Territory, state.Owner, newOwner, state.Armies, newArmies));
        }
        if (changes.Count == 0)
            throw new HostEditRefusedException("The edit changes nothing; the position already stands as written.");

        var countsBefore = _territoryCounts.ToDictionary(pair => pair.Key, pair => pair.Value);

        // --- Apply: ownership and armies, nothing else. ---
        foreach (var change in changes)
        {
            if (change.OwnerChanged) SetOwner(change.Territory, change.NewOwner);
            if (change.ArmiesChanged) _territories[change.Territory].Armies = change.NewArmies;
        }

        var notes = new List<string>();
        RepairAfterEdit(notes);
        AddPositionNotes(countsBefore, notes);
        return new HostEditResult(changes, notes);
    }

    /// <summary>The seats an edit may hand a territory to: every seat of this game, Rebels included in Duo.</summary>
    private bool IsLegalEditOwner(string owner) => _players.ContainsKey(owner);

    private string EditOwnerRefusal(string owner) =>
        owner == RulesConstants.RebelCode
            ? "The Rebels are not part of a Standard game."
            : $"{owner} is not a seat in this game.";

    /// <summary>
    /// Repairs the in-flight structures an edit can strand, in a fixed order,
    /// reporting each repair as a plain note. Pending decisions themselves
    /// are always recomputed from state, so anything whose inputs still stand
    /// needs no repair at all: it is simply presented again, fresh.
    /// </summary>
    private void RepairAfterEdit(List<string> notes)
    {
        // A forced match placement stands on its pictured territories;
        // candidates the seat no longer owns fall away, and the
        // placement lapses only when none remain. The queue keeps its
        // order: survivors are rotated through.
        if (_turn is { } turn)
        {
            int queued = turn.Placement.MatchQueue.Count;
            for (int i = 0; i < queued; i++)
            {
                var (candidates, armies) = turn.Placement.MatchQueue.Dequeue();
                var remaining = candidates.Where(code => _territories[code].Owner == turn.Player).ToList();
                if (remaining.Count > 0)
                {
                    turn.Placement.MatchQueue.Enqueue((remaining, armies));
                }
                else
                {
                    notes.Add($"The forced +{RulesConstants.TerritoryMatchBonus} placement on " +
                              $"{string.Join(", ", candidates)} lapsed: none of those territories " +
                              $"belongs to {turn.Player} any longer.");
                }
            }
        }

        // A battle stands on its two territories. If either changed hands,
        // or the capture it was heading for can no longer be made, the
        // attack is called off and play returns to the attacker's choices.
        BattleState? battle = _pending switch
        {
            PendingState.AttackDice dice => dice.Battle,
            PendingState.DefendDice defend => defend.Battle,
            PendingState.CaptureMove capture => capture.Battle,
            _ => null,
        };
        if (battle is not null && BattleBrokenByEdit(battle))
        {
            if (battle.HeldLogLine is { } held)
            {
                Emit(held);
                battle.HeldLogLine = null;
            }
            _pending = new PendingState.AttackDeclare(battle.Attacker);
            notes.Add($"The attack {battle.From}>{battle.To} was called off: the edit changed a territory it stood on.");
        }

        // A placement for a seat the edit left landless cannot be answered;
        // it is set aside and the turn moves on. A placement whose last
        // pending piece lapsed (a match above) simply completes.
        if (_pending is PendingState.Place place)
        {
            if (_territoryCounts[place.Player] == 0)
            {
                _turn!.Placement.FreeArmies = 0;
                _turn.Placement.MatchQueue.Clear();
                _pending = new PendingState.AttackDeclare(place.Player);
                notes.Add($"{place.Player}'s placement is set aside: {place.Player} holds no territories to place on.");
            }
            else if (_turn is { } current && !current.Placement.AnyPending)
            {
                _pending = NextPlacementOrAttack(place.Context);
            }
        }

        // Claims: if the edit claimed the last open territory, setup moves
        // on to placement exactly as a final claim does.
        if ((_pending is PendingState.Claim or PendingState.ClaimForRebels)
            && UnclaimedTerritories().Count == 0)
        {
            _rotationIndex = 0;
            _pending = NextSetupPlacePending();
            notes.Add("Every territory is claimed now; setup moves on to placement.");
        }

        // Setup placement for a seat that can no longer place is set
        // aside: the edit left it landless, or it stands at its full
        // starting allowance already (the edit handed it placed armies,
        // and placement counts armies on the board). The rotation
        // moves to the next seat that still has placing to do.
        if (_pending is PendingState.SetupPlace setupPlace
            && (_territoryCounts[setupPlace.Player] == 0
                || ArmiesPlacedBy(setupPlace.Player) >= StartingArmies(PlayerCount)))
        {
            string why = _territoryCounts[setupPlace.Player] == 0
                ? $"{setupPlace.Player} holds no territories"
                : $"{setupPlace.Player} already holds its full starting allowance";
            _rotationIndex = (_rotationIndex + 1) % _rotation.Count;
            _pending = NextSetupPlacePending();
            notes.Add($"{setupPlace.Player}'s setup placement is set aside: {why}.");
        }
    }

    /// <summary>True when the edit pulled the ground from under an in-progress battle.</summary>
    private bool BattleBrokenByEdit(BattleState battle)
    {
        if (_territories[battle.From].Owner != battle.Attacker
            || _territories[battle.To].Owner != battle.Defender)
            return true;
        if (_pending is PendingState.AttackDice && MaxAttackerDice(battle.From) < RulesConstants.MinDice)
            return true;
        if (_pending is PendingState.CaptureMove)
        {
            // The capture move-in must still be a move that can be made:
            // the target empty, and room for the dice that took it.
            int maxMove = _territories[battle.From].Armies - RulesConstants.MinArmiesLeftBehind;
            if (_territories[battle.To].Armies != 0 || maxMove < battle.LastAttackerDice)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The position, stated plainly: any seat the edit left holding no
    /// territories, and any eliminated seat the edit handed land back to
    /// (it holds the land; it still takes no turns, and only normal play
    /// can change that).
    /// </summary>
    private void AddPositionNotes(IReadOnlyDictionary<string, int> countsBefore, List<string> notes)
    {
        foreach (string seat in _territoryCounts.Keys.OrderBy(code => code, StringComparer.Ordinal))
        {
            int before = countsBefore.GetValueOrDefault(seat);
            int after = _territoryCounts[seat];
            if (after == before) continue;
            if (after == 0)
                notes.Add($"{seat} now holds no territories.");
            else if (before == 0 && _players.TryGetValue(seat, out var player) && !player.IsRebel && !player.Alive)
                notes.Add($"{seat} is eliminated and takes no turns; the edit leaves it holding {after} territories.");
        }
    }
}
