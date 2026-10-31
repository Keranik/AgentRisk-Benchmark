using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AgentRisk.Engine.Tests")]

namespace AgentRisk.Engine;

/// <summary>
/// The Classic Risk rules engine: a pure state machine with no I/O,
/// no rendering and no randomness except through the injected
/// <see cref="IGameRandom"/>. It always knows whose decision is pending;
/// callers ask with <see cref="PendingDecision"/>, answer with
/// <see cref="ApplyDecision"/>, and read the game through <see cref="Snapshot"/>.
/// Every event is appended to <see cref="Log"/> in the game-log grammar, and every
/// die roll to <see cref="DiceRolls"/> for audit.
/// </summary>
public sealed partial class RiskGame
{
    private readonly IBoardData _board;
    private readonly IGameRandom _random;
    private readonly List<string> _rotation;                 // seat codes in turn order (includes REB in Duo)
    private readonly Dictionary<string, PlayerState> _players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerritoryState> _territories = new(StringComparer.Ordinal);

    // Territory counts per seat, maintained incrementally on every ownership
    // change so turn bookkeeping never rescans the board (hot-path rule:
    // keep a count instead of scanning).
    private readonly Dictionary<string, int> _territoryCounts = new(StringComparer.Ordinal);

    private readonly List<Card> _drawPile = new();           // index 0 is the top of the deck
    private readonly List<Card> _discardPile = new();
    private readonly List<string> _log = new();
    private readonly List<DiceRollRecord> _diceRolls = new();

    private PendingState _pending;
    private TurnState? _turn;
    private int _rotationIndex;                              // whose claim/turn it is within _rotation
    private int _rebelChooserIndex;                          // Duo: which player chooses the next Rebel claim (0 or 1)
    private int _setsTraded;
    private int _round = 1;

    // Reused BFS scratch for fortify validation, so the per-turn check does
    // not allocate fresh collections every time (hot-path rule).
    private readonly HashSet<string> _bfsVisited = new(StringComparer.Ordinal);
    private readonly Queue<string> _bfsQueue = new();

    public RiskGame(
        IBoardData board,
        IReadOnlyList<string> playerCodes,
        GameMode mode,
        IGameRandom random,
        IEnumerable<string>? deckOrder = null,
        TerritorySetup territorySetup = TerritorySetup.ManualClaims)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        Mode = mode;
        TerritorySetup = territorySetup;

        ValidateSeats(playerCodes, mode);
        foreach (var code in playerCodes)
        {
            _players[code] = new PlayerState { Code = code };
            _territoryCounts[code] = 0;
        }

        _rotation = new List<string>(playerCodes);
        if (mode == GameMode.Duo)
        {
            _players[RulesConstants.RebelCode] = new PlayerState { Code = RulesConstants.RebelCode, IsRebel = true };
            _territoryCounts[RulesConstants.RebelCode] = 0;
            _rotation.Add(RulesConstants.RebelCode);
        }

        foreach (var territory in _board.Territories)
            _territories[territory.Code] = new TerritoryState();

        BuildDeck(deckOrder);
        _rotationIndex = 0;
        // A dealt board is assigned here, in construction, so the deal is
        // part of what the seed means: the same seed replays the same deal.
        _pending = territorySetup == TerritorySetup.RandomDeal
            ? DealTerritories()
            : NextClaimPending();
    }

    public GameMode Mode { get; }

    /// <summary>How this game's territories were assigned at setup.</summary>
    public TerritorySetup TerritorySetup { get; }

    /// <summary>Every game-log event line so far, in order.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>Every combat round ever rolled, faces included (for audit).</summary>
    public IReadOnlyList<DiceRollRecord> DiceRolls => _diceRolls;

    /// <summary>White-box access for the test suite's arrangement helpers.</summary>
    internal GameInternals Internals => new(this);

    // ------------------------------------------------------------------
    // Construction helpers
    // ------------------------------------------------------------------

    private static void ValidateSeats(IReadOnlyList<string> playerCodes, GameMode mode)
    {
        if (playerCodes.Count < RulesConstants.MinPlayers || playerCodes.Count > RulesConstants.MaxPlayers)
            throw new ArgumentException($"A game seats {RulesConstants.MinPlayers}..{RulesConstants.MaxPlayers} players.", nameof(playerCodes));
        if (playerCodes.Distinct(StringComparer.Ordinal).Count() != playerCodes.Count)
            throw new ArgumentException("Player codes must be distinct.", nameof(playerCodes));
        if (playerCodes.Any(c => !RulesConstants.PlayerCodes.Contains(c)))
            throw new ArgumentException("Player codes must come from the standard seat codes.", nameof(playerCodes));
        // Plain 2-player is not a format; Duo is the 2-player game.
        if (mode == GameMode.Duo && playerCodes.Count != 2)
            throw new ArgumentException("Duo mode seats exactly 2 players plus the Rebels.", nameof(playerCodes));
        if (mode == GameMode.Standard && playerCodes.Count == 2)
            throw new ArgumentException("Two-player games are played in Duo mode.", nameof(playerCodes));
    }

    private void BuildDeck(IEnumerable<string>? deckOrder)
    {
        var deck = new List<Card>();
        if (deckOrder is not null)
        {
            // Test/determinism hook: an exact draw order, ids top-first.
            deck.AddRange(deckOrder.Select(Card.FromId));
        }
        else
        {
            deck.AddRange(_board.Territories.Select(t => new Card(t.Code, _board.CardTypeOf(t.Code))));
            for (int i = 0; i < RulesConstants.WildCardCount; i++) deck.Add(new Card(null, CardType.Wild));
            Shuffle(deck);
        }
        _drawPile.AddRange(deck);
    }

    private void Shuffle<T>(List<T> items)
    {
        // Fisher-Yates through the injected RNG, so shuffles replay from the seed.
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = _random.NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static int StartingArmies(int playerCount) => RulesConstants.StartingArmiesByPlayerCount[playerCount];

    private int ArmiesPlacedBy(string seat) => _territories.Values.Where(t => t.Owner == seat).Sum(t => t.Armies);

    /// <summary>
    /// The active turn's reinforcement breakdown by source, or
    /// null outside a player's turn. Trade entries accumulate as trades happen.
    /// </summary>
    public EarnBreakdown? CurrentTurnBreakdown()
    {
        if (_turn is null) return null;
        int total = _turn.TerritoryYield
                    + _turn.ContinentBonuses.Sum(c => c.Bonus)
                    + _turn.Trades.Sum(t => t.Value);
        return new EarnBreakdown(_turn.TerritoryCountAtStart, _turn.TerritoryYield,
            _turn.ContinentBonuses.ToList(), _turn.Trades.ToList(), total);
    }

    // ------------------------------------------------------------------
    // Public decision API
    // ------------------------------------------------------------------

    /// <summary>The decision the game is waiting on, or null when the game is over.</summary>
    public Engine.PendingDecision? PendingDecision()
    {
        switch (_pending)
        {
            case PendingState.GameOver:
                return null;
            case PendingState.Claim claim:
                return new ClaimDecision(claim.Player, UnclaimedTerritories());
            case PendingState.ClaimForRebels claim:
                return new ClaimForRebelsDecision(claim.Chooser, RulesConstants.RebelCode, UnclaimedTerritories());
            case PendingState.SetupPlace place:
                return new SetupPlaceDecision(place.Player, OwnedTerritories(place.Player),
                    StartingArmies(PlayerCount) - ArmiesPlacedBy(place.Player));
            case PendingState.Trade trade:
            {
                var hand = _players[trade.Player].Hand;
                bool forced = IsTradeForced(trade.Player, trade.Context);
                return new TradeDecision(trade.Player, forced, !forced, trade.Context,
                    CardSets.FindSets(hand).Select(s => (IReadOnlyList<string>)s.Select(c => c.Id).ToList()).ToList(),
                    CardSets.TradeValue(_setsTraded + 1));
            }
            case PendingState.Place place:
            {
                var placement = _turn!.Placement;
                if (placement.MatchQueue.Count > 0)
                {
                    var (candidates, armies) = placement.MatchQueue.Peek();
                    return new PlaceDecision(place.Player, candidates, armies, place.Context, candidates, armies);
                }
                return new PlaceDecision(place.Player, OwnedTerritories(place.Player), placement.FreeArmies,
                    place.Context, null, null);
            }
            case PendingState.AttackDeclare attack:
                return new AttackDeclareDecision(attack.Player, LegalAttacks(attack.Player));
            case PendingState.AttackDice dice:
                return new AttackDiceDecision(dice.Battle.Attacker, dice.Battle.From, dice.Battle.To,
                    MaxAttackerDice(dice.Battle.From), dice.Battle.Rounds > 0);
            case PendingState.DefendDice defend:
                return new DefendDiceDecision(defend.Battle.Defender, defend.Battle.Attacker,
                    defend.Battle.From, defend.Battle.To, MaxDefenderDice(defend.Battle.To));
            case PendingState.CaptureMove move:
                return new CaptureMoveDecision(move.Battle.Attacker, move.Battle.From, move.Battle.To,
                    move.Battle.LastAttackerDice, _territories[move.Battle.From].Armies - RulesConstants.MinArmiesLeftBehind);
            case PendingState.Fortify fortify:
                return new FortifyDecision(fortify.Player, LegalFortifies(fortify.Player));
            default:
                throw new InvalidOperationException($"Unhandled pending state {_pending.GetType().Name}.");
        }
    }

    /// <summary>
    /// Applies one choice to the pending decision. Anything illegal is rejected
    /// with <see cref="IllegalChoiceException"/> and changes nothing.
    /// </summary>
    public void ApplyDecision(Choice choice)
    {
        switch ((_pending, choice))
        {
            case (PendingState.Claim claim, ClaimChoice c):
                ApplyClaim(claim.Player, c.Territory);
                break;
            case (PendingState.ClaimForRebels claim, ClaimForRebelsChoice c):
                ApplyClaim(RulesConstants.RebelCode, c.Territory, claim.Chooser);
                break;
            case (PendingState.SetupPlace place, SetupPlaceChoice c):
                ApplySetupPlace(place.Player, c.Territory);
                break;
            case (PendingState.Trade trade, TradeChoice c):
                ApplyTrade(trade, c.CardIds);
                break;
            case (PendingState.Trade trade, TradeNoneChoice):
                ApplyTradeNone(trade);
                break;
            case (PendingState.Place place, PlaceChoice c):
                ApplyPlace(place, c.Territory, c.Count);
                break;
            case (PendingState.AttackDeclare attack, AttackChoice c):
                ApplyAttackDeclare(attack.Player, c);
                break;
            case (PendingState.AttackDeclare, EndAttackChoice):
                ApplyEndAttack();
                break;
            case (PendingState.AttackDice dice, AttackDiceChoice c):
                ApplyAttackDice(dice.Battle, c.Dice);
                break;
            case (PendingState.AttackDice dice, AttackStopChoice):
                ApplyAttackStop(dice.Battle);
                break;
            case (PendingState.DefendDice defend, DefendDiceChoice c):
                ApplyDefendDice(defend.Battle, c.Dice);
                break;
            case (PendingState.CaptureMove move, CaptureMoveChoice c):
                ApplyCaptureMove(move.Battle, c.Count);
                break;
            case (PendingState.Fortify fortify, FortifyChoice c):
                ApplyFortify(fortify.Player, c);
                break;
            case (PendingState.Fortify, FortifySkipChoice):
                ApplyFortifySkip();
                break;
            case (PendingState.GameOver, _):
                throw Illegal("The game is over; no further decisions exist.");
            default:
                throw Illegal($"A {choice.GetType().Name} does not answer the pending {_pending.GetType().Name} decision.");
        }
    }

    /// <summary>A plain-data snapshot of the whole game (see Snapshot.cs).</summary>
    public GameSnapshot Snapshot()
    {
        var territories = _board.Territories
            .Select(t => new TerritorySnapshot(t.Code, _territories[t.Code].Owner, _territories[t.Code].Armies))
            .ToList();
        var players = _rotation
            .Select(code => _players[code])
            .Select(p => new PlayerSnapshot(p.Code, p.Alive, p.IsRebel, _territoryCounts[p.Code],
                _territories.Values.Where(t => t.Owner == p.Code).Sum(t => t.Armies),
                p.Hand.Select(c => c.Id).ToList()))
            .ToList();
        string? winner = _pending is PendingState.GameOver over ? over.Winner : null;
        return new GameSnapshot(territories, players, _turn?.Player, _round,
            CardSets.TradeValue(_setsTraded + 1), _setsTraded, winner, winner is not null);
    }

    private static IllegalChoiceException Illegal(string reason) => new(reason);

    private int PlayerCount => _rotation.Count(code => !_players[code].IsRebel);

    // ------------------------------------------------------------------
    // Board queries
    // ------------------------------------------------------------------

    private List<string> UnclaimedTerritories() =>
        _board.Territories.Where(t => _territories[t.Code].Owner is null).Select(t => t.Code).ToList();

    private List<string> OwnedTerritories(string seat) =>
        _board.Territories.Where(t => _territories[t.Code].Owner == seat).Select(t => t.Code).ToList();

    private int MaxAttackerDice(string from) =>
        Math.Min(RulesConstants.MaxAttackerDice, _territories[from].Armies - RulesConstants.MinArmiesLeftBehind);

    private int MaxDefenderDice(string to) =>
        Math.Min(RulesConstants.MaxDefenderDice, _territories[to].Armies);

    private List<AttackOption> LegalAttacks(string player)
    {
        var options = new List<AttackOption>();
        foreach (var territory in _board.Territories)
        {
            var state = _territories[territory.Code];
            if (state.Owner != player || state.Armies < 2) continue;
            foreach (var neighbor in _board.Territory(territory.Code).Neighbors)
            {
                var target = _territories[neighbor];
                if (target.Owner is null || target.Owner == player) continue;
                options.Add(new AttackOption(territory.Code, neighbor, MaxAttackerDice(territory.Code)));
            }
        }
        return options;
    }

    private List<FortifyOption> LegalFortifies(string player)
    {
        var options = new List<FortifyOption>();
        foreach (var source in _board.Territories)
        {
            var state = _territories[source.Code];
            if (state.Owner != player || state.Armies < 2) continue;
            foreach (var destination in ReachableOwned(player, source.Code))
                options.Add(new FortifyOption(source.Code, destination, state.Armies - RulesConstants.MinArmiesLeftBehind));
        }
        return options;
    }

    /// <summary>All territories reachable from <paramref name="from"/> moving only through <paramref name="player"/>'s land (BFS).</summary>
    private List<string> ReachableOwned(string player, string from)
    {
        _bfsVisited.Clear();
        _bfsQueue.Clear();
        _bfsVisited.Add(from);
        _bfsQueue.Enqueue(from);
        var reached = new List<string>();
        while (_bfsQueue.Count > 0)
        {
            var current = _bfsQueue.Dequeue();
            foreach (var neighbor in _board.Territory(current).Neighbors)
            {
                if (_territories[neighbor].Owner != player || !_bfsVisited.Add(neighbor)) continue;
                reached.Add(neighbor);
                _bfsQueue.Enqueue(neighbor);
            }
        }
        return reached;
    }

    private bool IsTradeForced(string player, PlacementContext context)
    {
        int handSize = _players[player].Hand.Count;
        // Turn start forces at the printed threshold. The inheritance window
        // opens at its own trigger (InheritanceForcedTradeThreshold, applied
        // where the window opens in AfterCapture) and then stays forced until
        // the hand is down to the target: trade until at 4 or fewer, then
        // trading must stop (the 1990s rulebook wording).
        return context == PlacementContext.TurnStart
            ? handSize >= RulesConstants.ForcedTradeThreshold
            : handSize > RulesConstants.InheritanceTradeDownTarget;
    }
}
