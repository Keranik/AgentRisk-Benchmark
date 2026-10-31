namespace AgentRisk.Engine;

/// <summary>
/// The state-machine transitions behind <see cref="RiskGame.ApplyDecision"/>:
/// setup, turn structure, trades and placements, combat, fortify, and the
/// Rebels' automatic turn. Split from RiskGame.cs so the public API surface
/// and the flow rules can each be read on their own.
/// </summary>
public sealed partial class RiskGame
{
    // ------------------------------------------------------------------
    // Setup: claims or the random deal, then one-at-a-time placement
    // ------------------------------------------------------------------

    /// <summary>
    /// The random deal (a session option): the whole board is
    /// shuffled through the game's seeded RNG and dealt round-robin in
    /// rotation order, the Rebels included as a recipient in Duo, exactly
    /// 1 army per territory, one DEAL line per territory. The shuffle
    /// draws on the same RNG stream as the deck, so the same seed and
    /// choice stream replay the same deal. There is no chooser in a
    /// deal, so no DEAL line ever carries the BY attribution that manual
    /// Duo claims carry. Setup placement afterwards is played
    /// by decision, exactly as after claims.
    /// </summary>
    private PendingState DealTerritories()
    {
        var codes = _board.Territories.Select(t => t.Code).ToList();
        Shuffle(codes);
        for (int i = 0; i < codes.Count; i++)
        {
            string seat = _rotation[i % _rotation.Count];
            SetOwner(codes[i], seat);
            _territories[codes[i]].Armies = 1; // every dealt territory starts with exactly 1 army
            Emit($"DEAL {codes[i]} TO {seat}");
        }
        // Dealing is done: first player in rotation starts placing.
        _rotationIndex = 0;
        return NextSetupPlacePending();
    }

    private PendingState NextClaimPending()
    {
        string seat = _rotation[_rotationIndex];
        return _players[seat].IsRebel
            ? new PendingState.ClaimForRebels(CurrentRebelChooser())
            : new PendingState.Claim(seat);
    }

    private string CurrentRebelChooser()
    {
        // Duo only: the two players alternate choosing the Rebels' claims,
        // starting with the first player.
        var players = _rotation.Where(c => !_players[c].IsRebel).ToList();
        return players[_rebelChooserIndex % players.Count];
    }

    private void ApplyClaim(string recipient, string territory, string? chooser = null)
    {
        if (!_territories.TryGetValue(territory, out var state))
            throw Illegal($"Unknown territory '{territory}'.");
        if (state.Owner is not null)
            throw Illegal($"{territory} is already claimed by {state.Owner}.");

        SetOwner(territory, recipient);
        state.Armies = 1; // every claim lands exactly 1 army
        // A Rebel claim names its chooser in the log:
        // `REB CLAIM ONT BY RED`. A self-claim has no chooser and no BY.
        Emit(chooser is null
            ? $"{recipient} CLAIM {territory}"
            : $"{recipient} CLAIM {territory} BY {chooser}");

        if (recipient == RulesConstants.RebelCode)
            _rebelChooserIndex++;

        if (UnclaimedTerritories().Count == 0)
        {
            // Claims are done: first player in rotation starts placing.
            _rotationIndex = 0;
            _pending = NextSetupPlacePending();
        }
        else
        {
            _rotationIndex = (_rotationIndex + 1) % _rotation.Count;
            _pending = NextClaimPending();
        }
    }

    private PendingState NextSetupPlacePending()
    {
        // Players place in rotation, skipping the Rebels (their placement is
        // random) and skipping anyone who has placed their full allowance.
        // A seat holding no territories is skipped too: unreachable in an
        // unedited game (claims and the deal always leave every seat
        // landed), it is the state a host edit can create.
        for (int tried = 0; tried < _rotation.Count; tried++)
        {
            string seat = _rotation[_rotationIndex];
            if (!_players[seat].IsRebel && _territoryCounts[seat] > 0
                && ArmiesPlacedBy(seat) < StartingArmies(PlayerCount))
                return new PendingState.SetupPlace(seat);
            _rotationIndex = (_rotationIndex + 1) % _rotation.Count;
        }
        // Everyone is placed: in Duo the Rebels' remaining armies land now.
        if (Mode == GameMode.Duo) PlaceRebelSetupArmies();
        _rotationIndex = 0;
        BeginTurn(_rotation[0]);
        return _pending; // BeginTurn replaced it
    }

    private void ApplySetupPlace(string player, string territory)
    {
        if (!_territories.TryGetValue(territory, out var state))
            throw Illegal($"Unknown territory '{territory}'.");
        if (state.Owner != player)
            throw Illegal($"Setup placement must be on your own territory; {territory} belongs to {state.Owner ?? "nobody"}.");
        if (ArmiesPlacedBy(player) >= StartingArmies(PlayerCount))
            throw Illegal($"{player} has already placed all starting armies.");

        state.Armies += 1;
        Emit($"{player} PLC {territory}+1 (setup)");
        _rotationIndex = (_rotationIndex + 1) % _rotation.Count;
        _pending = NextSetupPlacePending();
    }

    private void PlaceRebelSetupArmies()
    {
        // The Rebels' claims already put 1 army on each of their territories;
        // the rest of the 1.5x allowance lands at random.
        int total = (int)(StartingArmies(PlayerCount) * RulesConstants.RebelStartingArmyMultiplier);
        int remaining = total - ArmiesPlacedBy(RulesConstants.RebelCode);
        var placed = PlaceRandomly(RulesConstants.RebelCode, remaining);
        if (placed.Count > 0) Emit(RandomPlacementLine(RulesConstants.RebelCode, placed));
    }

    /// <summary>Distributes armies one at a time onto uniformly random owned territories; returns per-territory totals.</summary>
    private Dictionary<string, int> PlaceRandomly(string seat, int armies)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var owned = OwnedTerritories(seat);
        for (int i = 0; i < armies && owned.Count > 0; i++)
        {
            string target = owned[_random.NextInt(owned.Count)];
            _territories[target].Armies += 1;
            totals[target] = totals.GetValueOrDefault(target) + 1;
        }
        return totals;
    }

    private static string RandomPlacementLine(string seat, Dictionary<string, int> placed) =>
        $"{seat} PLC {string.Join(" ", placed.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}+{kv.Value}"))} (random)";

    // ------------------------------------------------------------------
    // Turn structure
    // ------------------------------------------------------------------

    private void BeginTurn(string seat)
    {
        if (_players[seat].IsRebel)
        {
            ResolveRebelTurn();
            AdvanceTurn();
            return;
        }

        int territoryCount = _territoryCounts[seat];
        _turn = new TurnState
        {
            Player = seat,
            TerritoryCountAtStart = territoryCount,
            TerritoryYield = Math.Max(RulesConstants.MinTerritoryIncome, territoryCount / RulesConstants.TerritoriesPerArmyDivisor),
            ContinentBonuses = _board.Continents
                .Where(c => c.Members.All(m => _territories[m].Owner == seat))
                .Select(c => (c.Code, c.Bonus))
                .ToList(),
            Conquered = false,
        };

        // A trade decision comes first whenever the player holds any set;
        // with no set there is nothing to decide.
        _pending = CardSets.FindSets(_players[seat].Hand).Count > 0
            ? new PendingState.Trade(seat, PlacementContext.TurnStart)
            : FinalizeEarn();
    }

    /// <summary>Closes the trade phase: totals the count, speaks the EARN line, and moves to placements.</summary>
    private PendingState FinalizeEarn()
    {
        var turn = _turn!;
        int total = turn.TerritoryYield
                    + turn.ContinentBonuses.Sum(c => c.Bonus)
                    + turn.Trades.Sum(t => t.Value);
        turn.Placement.FreeArmies += total;
        Emit(EarnLine(turn.Player, total, turn.TerritoryCountAtStart, turn.TerritoryYield, turn.ContinentBonuses, turn.Trades));
        return NextPlacementOrAttack(PlacementContext.TurnStart);
    }

    private static string EarnLine(string seat, int total, int territoryCount, int territoryYield,
        List<(string Continent, int Bonus)> continents, List<(int SetNumber, int Value)> trades)
    {
        var parts = new List<string> { $"{seat} EARN {total}", $"terr {territoryCount} -> {territoryYield}" };
        parts.AddRange(continents.Select(c => $"{c.Continent} +{c.Bonus}"));
        parts.AddRange(trades.Select(t => $"trade +{t.Value} (set#{t.SetNumber})"));
        return string.Join(" | ", parts);
    }

    private PendingState NextPlacementOrAttack(PlacementContext context)
    {
        var turn = _turn!;
        // A host edit can leave the turn's seat holding no territories at
        // all: its placement lapses here, so no route into a turn
        // can strand the game on a placement nobody can answer. Unreachable
        // in unedited play, where the turn's seat always holds land.
        if (_territoryCounts[turn.Player] == 0 && turn.Placement.AnyPending)
        {
            turn.Placement.FreeArmies = 0;
            turn.Placement.MatchQueue.Clear();
        }
        return turn.Placement.AnyPending
            ? new PendingState.Place(turn.Player, context)
            : new PendingState.AttackDeclare(turn.Player);
    }

    private void AdvanceTurn()
    {
        // Next seat in rotation that is still alive; landing at or before the
        // previous index means the rotation wrapped, starting a new round.
        int previous = _rotationIndex;
        do
        {
            _rotationIndex = (_rotationIndex + 1) % _rotation.Count;
        } while (!_players[_rotation[_rotationIndex]].Alive);
        if (_rotationIndex <= previous) _round++;
        BeginTurn(_rotation[_rotationIndex]);
    }

    private void ResolveRebelTurn()
    {
        // Rebels earn by the normal formula and place at random; that is their
        // whole turn. They never trade, attack, or fortify.
        const string reb = RulesConstants.RebelCode;
        int territoryCount = _territoryCounts[reb];
        int yield = Math.Max(RulesConstants.MinTerritoryIncome, territoryCount / RulesConstants.TerritoriesPerArmyDivisor);
        var continents = _board.Continents
            .Where(c => c.Members.All(m => _territories[m].Owner == reb))
            .Select(c => (c.Code, c.Bonus))
            .ToList();
        int total = yield + continents.Sum(c => c.Bonus);
        Emit(EarnLine(reb, total, territoryCount, yield, continents, new List<(int, int)>()));
        var placed = PlaceRandomly(reb, total);
        if (placed.Count > 0) Emit(RandomPlacementLine(reb, placed));
        Emit($"TURN END {reb}");
    }

    private void EndTurn()
    {
        var turn = _turn!;
        if (turn.Conquered) DrawCard(turn.Player);
        Emit($"TURN END {turn.Player}");
        _turn = null;
        AdvanceTurn();
    }

    private void DrawCard(string player)
    {
        if (_drawPile.Count == 0 && _discardPile.Count > 0)
        {
            // Standard table practice, not printed in the rules: traded cards are
            // reshuffled into a new draw pile when the deck runs out.
            _drawPile.AddRange(_discardPile);
            _discardPile.Clear();
            Shuffle(_drawPile);
        }
        if (_drawPile.Count == 0) return; // every card is in someone's hand; nothing to draw
        var card = _drawPile[0];
        _drawPile.RemoveAt(0);
        _players[player].Hand.Add(card);
        Emit($"{player} CRD");
    }

    // ------------------------------------------------------------------
    // Trades and placements
    // ------------------------------------------------------------------

    private void ApplyTrade(PendingState.Trade pending, IReadOnlyList<string> cardIds)
    {
        var player = _players[pending.Player];
        if (cardIds.Count != RulesConstants.CardsPerSet)
            throw Illegal($"A set is exactly {RulesConstants.CardsPerSet} cards.");

        // Multiset match against the hand: each named card must be held.
        var remaining = player.Hand.ToList();
        var chosen = new List<Card>();
        foreach (var id in cardIds)
        {
            var card = remaining.FirstOrDefault(c => c.Id == id);
            if (card is null) throw Illegal($"Card {id} is not in {pending.Player}'s hand.");
            remaining.Remove(card);
            chosen.Add(card);
        }
        if (!CardSets.IsSet(chosen))
            throw Illegal($"[{string.Join(" ", cardIds)}] is not a legal set.");

        player.Hand = remaining;
        _discardPile.AddRange(chosen);
        _setsTraded++;
        int value = CardSets.TradeValue(_setsTraded);
        _turn!.Trades.Add((_setsTraded, value));
        _turn.Placement.FreeArmies += pending.Context == PlacementContext.Inheritance ? value : 0;

        // Territory match bonus (the rulebook's wording): at
        // most +2 per turn in total, claimed by the first set traded
        // this turn that pictures a territory the trader occupies. The
        // armies go on one pictured territory: the only match, or the
        // trader's choice among several (the queued placement offers
        // exactly the matches as its candidates).
        var matches = chosen
            .Where(c => c.Territory is not null && _territories[c.Territory].Owner == pending.Player)
            .Select(c => c.Territory!)
            .ToList();
        bool bonusClaimed = matches.Count > 0 && !_turn.CardBonusClaimed;
        if (bonusClaimed)
        {
            _turn.CardBonusClaimed = true;
            _turn.Placement.MatchQueue.Enqueue((matches, RulesConstants.TerritoryMatchBonus));
        }

        var line = $"{pending.Player} TRD set#{_setsTraded} [{string.Join(" ", chosen.Select(c => c.Id))}] ={value}";
        if (bonusClaimed) line += $" match+2:{string.Join("|", matches)}";
        Emit(line);

        // Forced trades repeat until the hand is legal again. At turn start a
        // voluntary trade also repeats while the player still holds a set
        // they may choose to trade. The inheritance window is forced-only:
        // it closes the moment the hand reaches the down-target, even if a
        // set remains; that set waits for the next turn start (the 1990s
        // rulebook wording).
        bool forced = IsTradeForced(pending.Player, pending.Context);
        bool tradeAgain = forced
            || (pending.Context == PlacementContext.TurnStart && CardSets.FindSets(player.Hand).Count > 0);
        if (tradeAgain)
            _pending = new PendingState.Trade(pending.Player, pending.Context);
        else if (pending.Context == PlacementContext.TurnStart)
            _pending = FinalizeEarn();
        else
            _pending = NextPlacementOrAttack(pending.Context);
    }

    private void ApplyTradeNone(PendingState.Trade pending)
    {
        if (IsTradeForced(pending.Player, pending.Context))
            throw Illegal($"{pending.Player} holds too many cards and must trade; declining is illegal.");
        _pending = pending.Context == PlacementContext.TurnStart
            ? FinalizeEarn()
            : NextPlacementOrAttack(pending.Context);
    }

    private void ApplyPlace(PendingState.Place pending, string territory, int count)
    {
        var placement = _turn!.Placement;
        if (!_territories.TryGetValue(territory, out var state))
            throw Illegal($"Unknown territory '{territory}'.");
        if (state.Owner != pending.Player)
            throw Illegal($"Armies can only be placed on your own territory; {territory} belongs to {state.Owner ?? "nobody"}.");

        if (placement.MatchQueue.Count > 0)
        {
            var (candidates, matchArmies) = placement.MatchQueue.Peek();
            if (!candidates.Contains(territory))
                throw Illegal($"The match bonus must go on one of the territories pictured on the traded set: {string.Join(", ", candidates)}.");
            if (count != matchArmies)
                throw Illegal($"The match placement is exactly {matchArmies} armies.");
            placement.MatchQueue.Dequeue();
            state.Armies += count;
            Emit($"{pending.Player} PLC {territory}+{count} (match)");
        }
        else
        {
            if (count < 1 || count > placement.FreeArmies)
                throw Illegal($"Place between 1 and {placement.FreeArmies} armies.");
            placement.FreeArmies -= count;
            state.Armies += count;
            Emit($"{pending.Player} PLC {territory}+{count}");
        }
        _pending = NextPlacementOrAttack(pending.Context);
    }
}
