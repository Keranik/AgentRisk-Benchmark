namespace AgentRisk.Engine;

/// <summary>
/// Combat and fortify transitions, plus the shared
/// bookkeeping helpers the flow files use. Combat is the engine's hot loop:
/// it works on direct dictionary lookups and running totals, and builds a log
/// string only for the round that is actually emitted.
/// </summary>
public sealed partial class RiskGame
{
    // ------------------------------------------------------------------
    // Attack declaration and battle flow
    // ------------------------------------------------------------------

    private void ApplyAttackDeclare(string player, AttackChoice choice)
    {
        if (!_territories.TryGetValue(choice.From, out var from) || !_territories.TryGetValue(choice.To, out var to))
            throw Illegal("Attack source and target must be real territories.");
        if (from.Owner != player)
            throw Illegal($"{choice.From} is not yours to attack from.");
        if (to.Owner is null || to.Owner == player)
            throw Illegal($"{choice.To} is not an enemy territory.");
        if (!_board.AreAdjacent(choice.From, choice.To))
            throw Illegal($"{choice.From} does not border {choice.To}.");
        if (from.Armies < 2)
            throw Illegal($"{choice.From} needs at least 2 armies to attack.");

        var battle = new BattleState
        {
            From = choice.From,
            To = choice.To,
            Attacker = player,
            Defender = to.Owner,
            Mode = choice.Mode,
        };
        Emit($"{player} ATK {choice.From}>{choice.To} {(choice.Mode == AttackMode.Round ? "RND" : "ALL")}");
        _pending = choice.Mode == AttackMode.Round
            ? new PendingState.AttackDice(battle)
            : NextBlitzStep(battle);
    }

    private void ApplyEndAttack()
    {
        Emit($"{_turn!.Player} ENDATK");
        _pending = new PendingState.Fortify(_turn.Player);
    }

    private void ApplyAttackDice(BattleState battle, int dice)
    {
        int max = MaxAttackerDice(battle.From);
        if (dice < RulesConstants.MinDice || dice > max)
            throw Illegal($"Roll between {RulesConstants.MinDice} and {max} dice from {battle.From}.");
        battle.AttackerDiceForRound = dice;
        _pending = DefenderIsAutomatic(battle)
            ? ResolveAndContinue(battle, MaxDefenderDice(battle.To))
            : new PendingState.DefendDice(battle);
    }

    private void ApplyAttackStop(BattleState battle)
    {
        if (battle.Rounds == 0)
            throw Illegal("An attack must roll at least one round before it can be stopped.");
        Emit($"{battle.Attacker} STOP");
        _pending = new PendingState.AttackDeclare(battle.Attacker);
    }

    private void ApplyDefendDice(BattleState battle, int dice)
    {
        int max = MaxDefenderDice(battle.To);
        if (dice < RulesConstants.MinDice || dice > max)
            throw Illegal($"{battle.Defender} may roll between {RulesConstants.MinDice} and {max} dice.");
        _pending = ResolveAndContinue(battle, dice);
    }

    private bool DefenderIsAutomatic(BattleState battle) => _players[battle.Defender].IsRebel;

    /// <summary>
    /// In RESOLVE ALL the attacker always rolls the maximum and never
    /// re-decides; only a player defender still chooses dice each round.
    /// Rebels defend at maximum automatically.
    /// </summary>
    private PendingState NextBlitzStep(BattleState battle)
    {
        battle.AttackerDiceForRound = MaxAttackerDice(battle.From);
        return DefenderIsAutomatic(battle)
            ? ResolveAndContinue(battle, MaxDefenderDice(battle.To))
            : new PendingState.DefendDice(battle);
    }

    /// <summary>Rolls one combat round, applies losses at once, and routes to whatever comes next.</summary>
    private PendingState ResolveAndContinue(BattleState battle, int defenderDice)
    {
        int attackerDice = battle.AttackerDiceForRound;
        var attackerFaces = Dice.RollMany(_random, attackerDice);
        var defenderFaces = Dice.RollMany(_random, defenderDice);

        // Pairwise comparison, highest against highest; the defender wins
        // ties, and a second pair exists only when both sides rolled 2+.
        int attackerLosses = 0, defenderLosses = 0;
        int pairs = Math.Min(attackerDice, defenderDice);
        for (int i = 0; i < pairs; i++)
        {
            if (attackerFaces[i] > defenderFaces[i]) defenderLosses++;
            else attackerLosses++;
        }

        _territories[battle.From].Armies -= attackerLosses;
        _territories[battle.To].Armies -= defenderLosses;
        battle.Rounds++;
        battle.TotalAttackerLosses += attackerLosses;
        battle.TotalDefenderLosses += defenderLosses;
        battle.LastAttackerDice = attackerDice;
        _diceRolls.Add(new DiceRollRecord(_diceRolls.Count, battle.Attacker, battle.Defender,
            battle.From, battle.To, battle.Mode, attackerFaces, defenderFaces, attackerLosses, defenderLosses));

        bool captured = _territories[battle.To].Armies == 0;
        if (battle.Mode == AttackMode.Round)
        {
            string line = $"RND {attackerDice}v{defenderDice} A[{string.Join(" ", attackerFaces)}] D[{string.Join(" ", defenderFaces)}] A-{attackerLosses} D-{defenderLosses}";
            if (captured)
            {
                battle.HeldLogLine = line; // flushed once the move-in count is known
                return new PendingState.CaptureMove(battle);
            }
            Emit(line);
            return _territories[battle.From].Armies < 2
                ? new PendingState.AttackDeclare(battle.Attacker) // attacker spent: battle over
                : new PendingState.AttackDice(battle);
        }

        // RESOLVE ALL: rounds compress into a single line at battle end.
        if (captured) return new PendingState.CaptureMove(battle);
        if (_territories[battle.From].Armies < 2)
        {
            Emit(BlitzLine(battle));
            return new PendingState.AttackDeclare(battle.Attacker);
        }
        return NextBlitzStep(battle);
    }

    private static string BlitzLine(BattleState battle) =>
        $"ALL x{battle.Rounds} A-{battle.TotalAttackerLosses} D-{battle.TotalDefenderLosses}";

    private void ApplyCaptureMove(BattleState battle, int count)
    {
        int fromArmies = _territories[battle.From].Armies;
        int min = battle.LastAttackerDice;
        int max = fromArmies - RulesConstants.MinArmiesLeftBehind;
        if (count < min || count > max)
            throw Illegal($"Move between {min} and {max} armies into {battle.To}.");

        _territories[battle.From].Armies = fromArmies - count;
        SetOwner(battle.To, battle.Attacker);
        _territories[battle.To].Armies = count;
        _turn!.Conquered = true;

        string suffix = $" | CAP {battle.To} MOV{count}";
        Emit(battle.Mode == AttackMode.Round ? battle.HeldLogLine + suffix : BlitzLine(battle) + suffix);
        battle.HeldLogLine = null;

        _pending = AfterCapture(battle);
    }

    /// <summary>Elimination, inheritance, and the win check that follow a capture.</summary>
    private PendingState AfterCapture(BattleState battle)
    {
        string defender = battle.Defender;
        if (_territoryCounts[defender] > 0)
            return new PendingState.AttackDeclare(battle.Attacker);

        _players[defender].Alive = false;
        var inherited = _players[defender].Hand;
        int inheritedCount = inherited.Count;
        _players[battle.Attacker].Hand.AddRange(inherited);
        inherited.Clear();

        Emit(inheritedCount > 0
            ? $"ELIMINATION: {defender} is eliminated by {battle.Attacker}. {battle.Attacker} inherits {defender}'s {inheritedCount} cards."
            : $"ELIMINATION: {defender} is eliminated by {battle.Attacker}.");

        if (_territoryCounts[battle.Attacker] == RulesConstants.TotalTerritories)
            return new PendingState.GameOver(battle.Attacker);

        // Duo: the game is the two
        // players'. Once one of them is eliminated the other has won,
        // whatever the Rebels still hold. (Only a player can be the
        // attacker here: the Rebels never attack.)
        if (Mode == GameMode.Duo && !_players[defender].IsRebel)
            return new PendingState.GameOver(battle.Attacker);

        // Six or more cards after an inheritance force immediate trades
        // before anything else happens.
        return _players[battle.Attacker].Hand.Count >= RulesConstants.InheritanceForcedTradeThreshold
            ? new PendingState.Trade(battle.Attacker, PlacementContext.Inheritance)
            : new PendingState.AttackDeclare(battle.Attacker);
    }

    // ------------------------------------------------------------------
    // Fortify
    // ------------------------------------------------------------------

    private void ApplyFortify(string player, FortifyChoice choice)
    {
        if (!_territories.TryGetValue(choice.From, out var from) || !_territories.TryGetValue(choice.To, out var to))
            throw Illegal("Fortify source and destination must be real territories.");
        if (from.Owner != player || to.Owner != player)
            throw Illegal("A fortify moves between two of your own territories.");
        if (choice.From == choice.To)
            throw Illegal("Fortify source and destination must differ.");
        int max = from.Armies - RulesConstants.MinArmiesLeftBehind;
        if (choice.Count < 1 || choice.Count > max)
            throw Illegal($"Move between 1 and {max} armies; the source must keep at least {RulesConstants.MinArmiesLeftBehind}.");
        if (!ReachableOwned(player, choice.From).Contains(choice.To))
            throw Illegal($"{choice.To} is not connected to {choice.From} through your territories.");

        from.Armies -= choice.Count;
        to.Armies += choice.Count;
        Emit($"{player} FRT {choice.From}>{choice.To} {choice.Count}");
        EndTurn();
    }

    private void ApplyFortifySkip()
    {
        Emit($"{_turn!.Player} FRT SKIP");
        EndTurn();
    }

    // ------------------------------------------------------------------
    // Shared bookkeeping
    // ------------------------------------------------------------------

    /// <summary>Changes a territory's owner and keeps the per-seat territory counts exact.</summary>
    private void SetOwner(string code, string? owner)
    {
        var state = _territories[code];
        if (state.Owner == owner) return;
        if (state.Owner is not null) _territoryCounts[state.Owner]--;
        state.Owner = owner;
        if (owner is not null) _territoryCounts[owner]++;
    }

    private void Emit(string line) => _log.Add(line);

    /// <summary>
    /// White-box arrangement access for the test suite: hands, positions and
    /// pending state a test needs to set up before asserting behavior through
    /// the public API. Production code never touches this.
    /// </summary>
    internal sealed class GameInternals
    {
        private readonly RiskGame _game;

        internal GameInternals(RiskGame game) => _game = game;

        public TerritoryState Territory(string code) => _game._territories[code];

        public void SetTerritory(string code, string? owner, int armies)
        {
            _game.SetOwner(code, owner);
            _game._territories[code].Armies = armies;
        }

        public PlayerState Player(string code) => _game._players[code];

        public PendingState Pending
        {
            get => _game._pending;
            set => _game._pending = value;
        }

        public TurnState? Turn
        {
            get => _game._turn;
            set => _game._turn = value;
        }

        public int SetsTraded
        {
            get => _game._setsTraded;
            set => _game._setsTraded = value;
        }

        public List<Card> DrawPile => _game._drawPile;

        public BattleState NewBattle(string from, string to, AttackMode mode)
        {
            var fromState = _game._territories[from];
            var toState = _game._territories[to];
            return new BattleState
            {
                From = from,
                To = to,
                Attacker = fromState.Owner!,
                Defender = toState.Owner!,
                Mode = mode,
            };
        }

        /// <summary>Starts a fresh turn state for a seat, with the EARN inputs computed as BeginTurn computes them.</summary>
        public void SetTurn(string player)
        {
            int count = _game._territoryCounts[player];
            _game._turn = new TurnState
            {
                Player = player,
                TerritoryCountAtStart = count,
                TerritoryYield = Math.Max(RulesConstants.MinTerritoryIncome,
                    count / RulesConstants.TerritoriesPerArmyDivisor),
                ContinentBonuses = _game._board.Continents
                    .Where(c => c.Members.All(m => _game._territories[m].Owner == player))
                    .Select(c => (c.Code, c.Bonus))
                    .ToList(),
                Conquered = false,
            };
        }
    }
}
