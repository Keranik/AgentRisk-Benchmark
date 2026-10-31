using System.Text.RegularExpressions;
using AgentRisk.Engine;

namespace AgentRisk.Host;

/// <summary>
/// The host's built-in scripted seat (the "stand-in"): a legal-move player
/// that reads only the prompt text it is given, exactly as a remote agent
/// must, and answers in the offered grammar. It fills seats for practice
/// games, smoke games and tests, so it has to play a game to its end, not
/// just answer: its habits are those of a plain, purposeful player.
///
/// - Claims build toward one continent, the cheapest to hold first.
/// - Setup armies go to the front a player presses hardest.
/// - Reinforcements go where the best attack of the turn will start; with
///   no attack in reach, onto the one stack nearest a worthwhile capture,
///   so the next attack grows in one place instead of spreading thin.
/// - It attacks only with the odds clearly on its side, worked out exactly
///   from the dice (see <see cref="CaptureOdds"/>), prefers captures that
///   complete or break a continent, finish off a seat, or destroy a large
///   enemy stack, and keeps attacking while the odds hold.
/// - It moves armies into a capture, keeping enough behind to match the
///   strongest player still beside the source, and none forward into a
///   capture with nothing left to take beyond it.
/// - It fortifies by merging: two stacks that each fall short of the odds
///   against the same worthwhile target become one that does not (a
///   wall it would not touch for six rounds stops being a wall).
///   Otherwise an idle stack moves to the front a player presses hardest.
/// - It trades a set whenever it holds one.
/// - Only players are threats. The Rebels never attack, so it
///   never garrisons against them or leaves armies behind for them: their
///   land is terrain to take, not a front to hold.
/// - In Duo (the game ends when one of the two players is eliminated) it
///   prefers the other player's land to the Rebels'. Once the other
///   player is beaten (a fraction of its armies, or thin stacks and fewer
///   armies), it hunts: every army goes onto one stack, the stack takes
///   the cheapest way into his land, Rebel land in between included, and
///   rolls on until he is gone. No more continent building on the way.
///
/// It is stateless: one instance serves every stand-in seat of a table,
/// and each answer comes from the prompt alone plus the printed board
/// (names, continents, borders). Every choice is deterministic, ties
/// broken by territory code, so a seeded game replays exactly. This is
/// a player, not a coach: nothing here is ever shown to another seat.
/// </summary>
public sealed class ScriptedSeatPolicy
{
    /// <summary>Tunable: attack only when an all-out attack takes the target at least this often.</summary>
    public const double AttackOdds = 0.65;

    /// <summary>Tunable: a seat left holding this few territories is a target worth extra effort (its cards, or the win in Duo).</summary>
    public const int NearlyOutTerritories = 3;

    /// <summary>Tunable, Duo: the hunt starts once the seat holds this many times the other player's armies...</summary>
    public const double HuntArmyRatio = 2.0;

    /// <summary>Tunable, Duo: ...or this many times, once his stacks average no more than <see cref="ThinStack"/> armies.</summary>
    public const double HuntThinRatio = 1.25;

    /// <summary>Tunable, Duo: the average stack at or under which the other player is spread thin.</summary>
    public const double ThinStack = 2.0;

    /// <summary>Tunable: a merge is worth the turn's fortify when it lifts the odds against its target by at least this much.</summary>
    public const double MergeGain = 0.15;

    /// <summary>The cheapest continents to hold first: fewest borders to guard for their bonus.</summary>
    private static readonly string[] ContinentPreference = { "AU", "SA", "NA", "AF", "EU", "AS" };

    private readonly IBoardData _board;

    public ScriptedSeatPolicy(IBoardData board) => _board = board ?? throw new ArgumentNullException(nameof(board));

    /// <summary>Answers one prompt (the delegate a <see cref="DelegateAgentChannel"/> wraps).</summary>
    /// <exception cref="InvalidOperationException">The prompt matched no known decision shape.</exception>
    public string Respond(string prompt)
    {
        // Line-ending tolerance, applied once at the door: prompts are
        // specified with "\n" endings, but a prompt that arrives with
        // "\r\n" (built on Windows before PromptBuilder pinned "\n", or
        // relayed by a foreign host) must parse identically. A seat reads
        // text; it does not get to demand one OS's bytes. ParseBoard
        // additionally trims at its anchored pattern, so the board parse
        // is safe even for a caller that bypasses this normalization.
        prompt = NormalizeNewlines(prompt);

        // The strike-3 prompt is the only one without the --- SUMMARY / BOARD /
        // DECISION sections.
        if (prompt.StartsWith("Choose one, reply with its number:", StringComparison.Ordinal))
            return "1";

        string seat = Match(prompt, @"You are ([A-Z]{3})", "seat identity");
        var view = new View(ParseBoard(prompt), seat, _board, prompt.Contains("Mode: Duo") || HasRebels(prompt));

        if (prompt.Contains("Sets you can trade:"))
            return "TRD 1";
        if (prompt.Contains("faction's claim turn"))
            return $"CLAIM {RebelClaim(view)}";
        if (prompt.Contains("Claim one unclaimed territory"))
            return $"CLAIM {OwnClaim(view)}";
        if (prompt.Contains("Setup placement:"))
            return $"PLC {SetupPlacement(view)}";
        if (prompt.Contains("match bonus"))
            return MatchPlacement(prompt, view);
        if (prompt.Contains("Place reinforcements:"))
        {
            int remaining = int.Parse(Match(prompt, @"Place reinforcements: (\d+) remaining", "placement count"));
            return $"PLC {Staging(view, remaining)} {remaining}";
        }
        if (prompt.Contains("Declare an attack") || prompt.Contains("no legal attacks"))
            return DeclareAttack(view);
        if (prompt.Contains("roll the next round"))
            return $"DICE {Match(prompt, @"1 to (\d+) dice", "attacker dice")}";
        if (prompt.Contains("Defend "))
            return $"DICE {Match(prompt, @"1 to (\d+) dice", "defender dice")}";
        if (prompt.Contains("Move between"))
            return $"MOV {CaptureMove(prompt, view)}";
        if (prompt.Contains("Fortify:"))
            return Fortify(view);

        throw new InvalidOperationException(
            $"Scripted policy cannot answer this prompt: {prompt.Split('\n')[0]}");
    }

    // ------------------------------------------------------------------
    // Setup: claims and setup placement
    // ------------------------------------------------------------------

    /// <summary>
    /// A claim builds toward a continent: the one the seat holds most of,
    /// the cheapest to hold breaking ties, and within it the territory
    /// touching the most of the seat's own land.
    /// </summary>
    private string OwnClaim(View view)
    {
        var open = view.Unclaimed();
        return open
            .OrderByDescending(code => 4.0 * view.ContinentShare(_board.Territory(code).Continent, view.Seat)
                                       - 3.0 * view.ContinentRivalShare(_board.Territory(code).Continent)
                                       + ContinentAppeal(_board.Territory(code).Continent)
                                       + 0.4 * view.Neighbors(code).Count(n => view.Owner(n) == view.Seat))
            .ThenBy(code => code, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// The Rebels' land, chosen by a player in Duo: put it where it blocks
    /// the other player's best continent, and keep it out of the seat's own.
    /// </summary>
    private string RebelClaim(View view)
    {
        var open = view.Unclaimed();
        return open
            .OrderByDescending(code => view.ContinentRivalShare(_board.Territory(code).Continent)
                                       - view.ContinentShare(_board.Territory(code).Continent, view.Seat))
            .ThenBy(code => code, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// One setup army: the front a player presses hardest, weighted toward
    /// the seat's continent. Land that faces only the Rebels needs nothing
    /// (they never attack); when every front is Rebel land, the army goes
    /// where the first attack will grow.
    /// </summary>
    private string SetupPlacement(View view)
    {
        var fronts = view.Owned().Where(view.FacesThreat).ToList();
        if (fronts.Count == 0) return BuildUp(view, 1) ?? view.Owned().First();
        return fronts
            .OrderByDescending(code => view.Pressure(code)
                                       + 2.0 * view.ContinentShare(_board.Territory(code).Continent, view.Seat))
            .ThenBy(code => code, StringComparer.Ordinal)
            .First();
    }

    /// <summary>The front a player outnumbers most, or null when no player outnumbers any.</summary>
    private static string? MostPressedFront(View view) =>
        view.Owned()
            .Where(code => view.FacesThreat(code) && view.Pressure(code) > 0)
            .OrderByDescending(view.Pressure)
            .ThenBy(code => code, StringComparer.Ordinal)
            .FirstOrDefault();

    private static double ContinentAppeal(string continent)
    {
        int rank = Array.IndexOf(ContinentPreference, continent);
        return rank < 0 ? 0 : (ContinentPreference.Length - rank) * 0.25;
    }

    // ------------------------------------------------------------------
    // Reinforcement and the card bonus
    // ------------------------------------------------------------------

    /// <summary>
    /// Where the turn's reinforcements stand. On the hunt, all on the
    /// hammer. Otherwise the start of the best attack they make possible;
    /// with none in reach, the front a player outnumbers, and with no such
    /// front, the stack nearest a worthwhile capture, so armies gather in
    /// one place over the turns rather than spreading.
    /// </summary>
    private string Staging(View view, int reinforcements)
    {
        if (StartHunt(view) is { } hunt && Hammer(view, hunt, reinforcements) is { } hammer)
            return hammer;
        var best = BestAttack(view, extraArmies: reinforcements);
        return best?.From ?? MostPressedFront(view) ?? BuildUp(view, reinforcements) ?? view.Owned().First();
    }

    /// <summary>
    /// The seat's territory whose stack, with these armies added, comes
    /// nearest a worthwhile capture: worth of the target and the odds
    /// together. Placing here again and again builds one attack, where
    /// spreading would leave every stack short.
    /// </summary>
    private string? BuildUp(View view, int extraArmies)
    {
        string? best = null;
        double bestScore = double.MinValue;
        foreach (string from in view.Owned())
        foreach (string to in view.Neighbors(from))
        {
            if (!view.IsEnemy(to)) continue;
            double score = AttackWorth(view, to) + 6.0 * CaptureOdds(view.Armies(from) + extraArmies - 1, view.Armies(to));
            if (score > bestScore || (score == bestScore && string.CompareOrdinal(from, best) < 0))
            {
                bestScore = score;
                best = from;
            }
        }
        return best;
    }

    /// <summary>
    /// The card bonus: among the offered territories, where this turn's
    /// armies are going anyway (the hammer, the best attack's start), else
    /// the front a player presses hardest.
    /// </summary>
    private string MatchPlacement(string prompt, View view)
    {
        var offered = Regex.Matches(prompt, @"PLC ([A-Z]{3}) (\d+)")
            .Select(m => (Code: m.Groups[1].Value, Count: m.Groups[2].Value))
            .Distinct()
            .ToList();
        if (offered.Count == 0)
            throw new InvalidOperationException("Scripted policy could not find the match placement in the prompt.");
        string staging = Staging(view, int.Parse(offered[0].Count));
        var pick = offered
            .OrderByDescending(o => o.Code == staging ? 1 : 0)
            .ThenByDescending(o => view.FacesThreat(o.Code) ? 1 : 0)
            .ThenByDescending(o => view.Pressure(o.Code))
            .ThenBy(o => o.Code, StringComparer.Ordinal)
            .First();
        return $"PLC {pick.Code} {pick.Count}";
    }

    // ------------------------------------------------------------------
    // Attack and capture
    // ------------------------------------------------------------------

    private string DeclareAttack(View view)
    {
        var best = StartHunt(view) is { } hunt ? HuntAttack(view, hunt) : BestAttack(view, extraArmies: 0);
        return best is { } attack ? $"ATK {attack.From} {attack.To} ALL" : "ENDATK";
    }

    /// <summary>
    /// The most worthwhile attack the seat can make with the odds on its
    /// side, or null when none is. Worth: completing a continent, breaking
    /// another seat's continent, finishing off a nearly beaten seat (the
    /// other player, in Duo), the seat's own continent plan, and the
    /// margin of armies itself.
    /// </summary>
    private (string From, string To)? BestAttack(View view, int extraArmies)
    {
        (string From, string To)? best = null;
        double bestScore = double.MinValue;
        foreach (string from in view.Owned())
        {
            int attackers = view.Armies(from) + extraArmies;
            if (attackers < 2) continue;
            foreach (string to in view.Neighbors(from))
            {
                if (!view.IsEnemy(to)) continue;
                int defenders = view.Armies(to);
                double odds = CaptureOdds(attackers - 1, defenders);
                if (odds < AttackOdds) continue;

                double score = AttackWorth(view, to) + 4.0 * odds;
                if (score > bestScore
                    || (score == bestScore && best is { } b
                        && string.CompareOrdinal(from + to, b.From + b.To) < 0))
                {
                    bestScore = score;
                    best = (from, to);
                }
            }
        }
        return best;
    }

    private double AttackWorth(View view, string target)
    {
        string owner = view.Owner(target);
        string continent = _board.Territory(target).Continent;
        var members = _board.Continent(continent).Members;
        int bonus = _board.Continent(continent).Bonus;
        double worth = 0;

        // Completing a continent of the seat's own.
        int missing = members.Count(m => view.Owner(m) != view.Seat);
        if (missing == 1) worth += 3.0 * bonus;
        worth += 3.0 * view.ContinentShare(continent, view.Seat) + ContinentAppeal(continent);

        // Breaking a continent another seat holds whole.
        if (members.All(m => view.Owner(m) == owner)) worth += 2.0 * bonus;

        // A seat nearly out: its cards, or in Duo, the game.
        int left = view.TerritoryCount(owner);
        if (owner != RulesConstants.RebelCode && left <= NearlyOutTerritories)
            worth += view.IsDuo ? 30 : 8;

        // A large stack destroyed is a threat gone (and, once few seats
        // remain, what decides the game); the Rebels' stacks never attack.
        if (owner != RulesConstants.RebelCode) worth += 0.08 * view.Armies(target);

        // In Duo the Rebels are in the way, not the goal.
        if (view.IsDuo && owner != RulesConstants.RebelCode) worth += 4;
        return worth;
    }

    /// <summary>
    /// Armies into a capture. On the hunt, the stack goes wherever the
    /// cheapest way on into the prey's land continues. Otherwise none
    /// forward when nothing beyond the capture is left to take; else all
    /// but enough to match the strongest player still beside the source
    /// (the Rebels never attack, so nothing stays behind for them).
    /// </summary>
    private int CaptureMove(string prompt, View view)
    {
        var move = Regex.Match(prompt, @"Move between (\d+) and (\d+) armies from ([A-Z]{3}) into ([A-Z]{3})");
        if (!move.Success)
            return int.Parse(Match(prompt, @"between \d+ and (\d+)", "capture move range"));
        int min = int.Parse(move.Groups[1].Value);
        int max = int.Parse(move.Groups[2].Value);
        string from = move.Groups[3].Value;
        string into = move.Groups[4].Value;
        if (StartHunt(view) is { } hunt)
        {
            int onFromInto = hunt.NextStep(view, into, except: from);
            int onFromSource = hunt.NextStep(view, from, except: into);
            return onFromInto <= onFromSource ? max : min;
        }
        // A capture that faces no enemy (every neighbour the seat's own,
        // once it is taken) needs no more than the least.
        if (!view.Neighbors(into).Any(n => n != from && view.IsEnemy(n) && n != into))
            return min;
        int threat = view.Neighbors(from)
            .Where(n => n != into && view.IsThreat(n))
            .Select(view.Armies)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Clamp(max - threat, min, max);
    }

    // ------------------------------------------------------------------
    // The dice: exact capture odds for an all-out attack
    // ------------------------------------------------------------------

    private const int OddsTableSize = 201;
    private static readonly Lazy<double[,]> OddsTable = new(BuildOddsTable);

    /// <summary>
    /// The chance that an all-out attack with this many armies able to
    /// roll (the source's armies less the one that stays) takes a
    /// territory this many defend, both sides rolling the most dice they
    /// may (the attacker up to 3, the defender up to 2). Exact, from the
    /// per-roll outcome table, for up to 200 a side; larger stacks are
    /// scaled down in proportion, which keeps their ratio.
    /// </summary>
    public static double CaptureOdds(int attackers, int defenders)
    {
        if (defenders <= 0) return 1;
        if (attackers <= 0) return 0;
        int largest = Math.Max(attackers, defenders);
        if (largest >= OddsTableSize)
        {
            double scale = (OddsTableSize - 1.0) / largest;
            attackers = Math.Max(1, (int)Math.Round(attackers * scale));
            defenders = Math.Max(1, (int)Math.Round(defenders * scale));
        }
        return OddsTable.Value[attackers, defenders];
    }

    private static double[,] BuildOddsTable()
    {
        // (attacker dice, defender dice) -> outcomes as
        // (attacker losses, defender losses, probability), the standard
        // enumeration of six-sided dice compared highest to highest.
        static (int A, int D, double P)[] Outcomes(int attackDice, int defendDice) => (attackDice, defendDice) switch
        {
            (3, 2) => new[] { (2, 0, 2275 / 7776.0), (1, 1, 2611 / 7776.0), (0, 2, 2890 / 7776.0) },
            (2, 2) => new[] { (2, 0, 581 / 1296.0), (1, 1, 420 / 1296.0), (0, 2, 295 / 1296.0) },
            (1, 2) => new[] { (1, 0, 161 / 216.0), (0, 1, 55 / 216.0) },
            (3, 1) => new[] { (1, 0, 441 / 1296.0), (0, 1, 855 / 1296.0) },
            (2, 1) => new[] { (1, 0, 91 / 216.0), (0, 1, 125 / 216.0) },
            _ => new[] { (1, 0, 21 / 36.0), (0, 1, 15 / 36.0) },
        };

        var odds = new double[OddsTableSize, OddsTableSize];
        for (int a = 0; a < OddsTableSize; a++)
        {
            for (int d = 0; d < OddsTableSize; d++)
            {
                if (d == 0) { odds[a, d] = 1; continue; }
                if (a == 0) { odds[a, d] = 0; continue; }
                double p = 0;
                foreach (var (lostA, lostD, chance) in Outcomes(Math.Min(3, a), Math.Min(2, d)))
                    p += chance * odds[Math.Max(0, a - lostA), Math.Max(0, d - lostD)];
                odds[a, d] = p;
            }
        }
        return odds;
    }

    // ------------------------------------------------------------------
    // Fortify
    // ------------------------------------------------------------------

    /// <summary>
    /// The turn's one fortify. On the hunt, the largest stack that can
    /// reach a territory nearer the prey's land moves there. Otherwise a
    /// merge, when one lifts the odds against a worthwhile target enough;
    /// else the largest idle stack (no player beside it, and no attack of
    /// its own in reach) moves, all but one, to the front a player presses
    /// hardest that it can reach through the seat's own land, or, with no
    /// such front in reach, to where the next attack is building.
    /// </summary>
    private string Fortify(View view)
    {
        if (StartHunt(view) is { } hunt && HuntFortify(view, hunt) is { } closing)
            return closing;
        if (MergeFortify(view) is { } merge)
            return merge;

        var source = view.Owned()
            .Where(code => view.Armies(code) >= 2 && !view.FacesThreat(code) && !HasAttack(view, code))
            .OrderByDescending(view.Armies)
            .ThenBy(code => code, StringComparer.Ordinal)
            .FirstOrDefault();
        if (source is null) return "FRT SKIP";

        var reachable = view.ReachableThroughOwn(source);
        var target = reachable
            .Where(view.FacesThreat)
            .OrderByDescending(view.Pressure)
            .ThenBy(code => code, StringComparer.Ordinal)
            .FirstOrDefault();
        if (target is null)
        {
            // No player in reach (in Duo, a pocket walled in by Rebel
            // land): the armies join the attack building beside the wall.
            string? building = BuildUp(view, view.Armies(source) - 1);
            target = building is not null && reachable.Contains(building) ? building : null;
        }
        return target is null ? "FRT SKIP" : $"FRT {source} {target} {view.Armies(source) - 1}";
    }

    /// <summary>True when the territory's own stack already has an attack at the odds.</summary>
    private static bool HasAttack(View view, string code) =>
        view.Neighbors(code).Any(n => view.IsEnemy(n) && CaptureOdds(view.Armies(code) - 1, view.Armies(n)) >= AttackOdds);

    /// <summary>
    /// Split stacks become one. For an enemy territory that no single
    /// stack beside it can take at the odds, a stack the seat can bring
    /// to one of its besiegers (through its own land, and not one holding
    /// a front against another player) joins it, when together they lift
    /// the odds by at least <see cref="MergeGain"/>. The most worthwhile
    /// target wins, the merged odds breaking ties.
    /// </summary>
    private string? MergeFortify(View view)
    {
        (string Source, string Into)? best = null;
        double bestScore = double.MinValue;
        foreach (string target in view.All())
        {
            if (!view.IsEnemy(target)) continue;
            var besiegers = view.Neighbors(target).Where(n => view.Owner(n) == view.Seat).ToList();
            if (besiegers.Count == 0) continue;
            int defenders = view.Armies(target);
            double alone = besiegers.Max(code => CaptureOdds(view.Armies(code) - 1, defenders));
            if (alone >= AttackOdds) continue;
            double worth = AttackWorth(view, target);
            foreach (string into in besiegers)
            foreach (string source in view.ReachableThroughOwn(into))
            {
                if (view.Armies(source) < 2 || view.FacesThreatBesides(source, target)) continue;
                double merged = CaptureOdds(view.Armies(into) + view.Armies(source) - 2, defenders);
                if (merged - alone < MergeGain) continue;
                double score = worth + 6.0 * merged + 0.001 * view.Armies(into);
                if (score > bestScore
                    || (score == bestScore && best is { } b
                        && string.CompareOrdinal(source + into, b.Source + b.Into) < 0))
                {
                    bestScore = score;
                    best = (source, into);
                }
            }
        }
        return best is { } move ? $"FRT {move.Source} {move.Into} {view.Armies(move.Source) - 1}" : null;
    }

    // ------------------------------------------------------------------
    // The hunt (Duo): one stack, the cheapest way in, no detours
    // ------------------------------------------------------------------

    /// <summary>
    /// The other player in Duo, once he is beaten enough that the seat
    /// should stop building and finish him: holding a fraction of the
    /// seat's armies, or spread thin and outnumbered, or down to his last
    /// few territories. Null otherwise, and always outside Duo.
    /// </summary>
    private static Hunt? StartHunt(View view)
    {
        if (!view.IsDuo) return null;
        string? prey = view.OtherPlayers().FirstOrDefault();
        if (prey is null) return null;
        int mine = view.TotalArmies(view.Seat);
        int his = view.TotalArmies(prey);
        int held = view.TerritoryCount(prey);
        double ratio = his == 0 ? double.MaxValue : (double)mine / his;
        bool thin = (double)his / held <= ThinStack;
        bool beaten = ratio >= HuntArmyRatio
                      || (thin && ratio >= HuntThinRatio)
                      || (held <= NearlyOutTerritories && ratio >= 1);
        return beaten ? new Hunt(prey, RouteCosts(view, prey)) : null;
    }

    /// <summary>
    /// For every territory not the seat's: the fewest armies to beat
    /// taking it and then the cheapest way on, until a territory of the
    /// prey falls. Each step costs its defenders plus the one army a
    /// capture leaves behind, so a longer way costs more than a shorter
    /// one through the same armies. Rebel land is just more of the way.
    /// </summary>
    private static Dictionary<string, int> RouteCosts(View view, string prey)
    {
        var cost = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, int>();
        foreach (string code in view.All().Where(code => view.Owner(code) == prey))
        {
            cost[code] = view.Armies(code) + 1;
            queue.Enqueue(code, cost[code]);
        }
        while (queue.TryDequeue(out string? code, out int reached))
        {
            if (reached > cost[code]) continue;
            foreach (string next in view.Neighbors(code))
            {
                if (view.Owner(next) == view.Seat) continue;
                int through = reached + view.Armies(next) + 1;
                if (cost.TryGetValue(next, out int known) && known <= through) continue;
                cost[next] = through;
                queue.Enqueue(next, through);
            }
        }
        return cost;
    }

    /// <summary>
    /// The stack that rolls the prey up: the seat's territory whose armies,
    /// with the ones being placed, best cover the cheapest way into his
    /// land; the largest stack among equals, so everything gathers on one.
    /// </summary>
    private static string? Hammer(View view, Hunt hunt, int extraArmies) =>
        view.Owned()
            .Select(code => (Code: code, Need: hunt.NextStep(view, code)))
            .Where(x => x.Need != int.MaxValue)
            .OrderByDescending(x => CaptureOdds(view.Armies(x.Code) + extraArmies - 1, x.Need))
            .ThenByDescending(x => view.Armies(x.Code))
            .ThenBy(x => x.Code, StringComparer.Ordinal)
            .Select(x => x.Code)
            .FirstOrDefault();

    /// <summary>
    /// The hunt's attack: the prey's own land first (his weakest first),
    /// then the next step on the cheapest way into it; the bigger stack
    /// leads. Nothing off the way: no continent sightseeing while he lives.
    /// </summary>
    private static (string From, string To)? HuntAttack(View view, Hunt hunt)
    {
        (string From, string To)? best = null;
        double bestScore = double.MinValue;
        foreach (string from in view.Owned())
        {
            int attackers = view.Armies(from);
            if (attackers < 2) continue;
            int onward = hunt.NextStep(view, from);
            foreach (string to in view.Neighbors(from))
            {
                if (!hunt.Route.TryGetValue(to, out int route)) continue;
                if (CaptureOdds(attackers - 1, view.Armies(to)) < AttackOdds) continue;
                double score;
                if (view.Owner(to) == hunt.Prey) score = 1000 - route;
                else if (route == onward) score = 500 - route;
                else continue;
                score += 0.01 * attackers;
                if (score > bestScore
                    || (score == bestScore && best is { } b
                        && string.CompareOrdinal(from + to, b.From + b.To) < 0))
                {
                    bestScore = score;
                    best = (from, to);
                }
            }
        }
        return best;
    }

    /// <summary>
    /// The hunt's fortify: the largest stack that can reach, through the
    /// seat's own land, a territory nearer the prey moves onto the nearest
    /// one (the strongest among equals), all but one army.
    /// </summary>
    private static string? HuntFortify(View view, Hunt hunt)
    {
        foreach (string source in view.Owned()
                     .Where(code => view.Armies(code) >= 2)
                     .OrderByDescending(view.Armies)
                     .ThenBy(code => code, StringComparer.Ordinal))
        {
            int here = hunt.NextStep(view, source);
            var nearer = view.ReachableThroughOwn(source)
                .Select(code => (Code: code, Need: hunt.NextStep(view, code)))
                .Where(x => x.Need < here)
                .OrderBy(x => x.Need)
                .ThenByDescending(x => view.Armies(x.Code))
                .ThenBy(x => x.Code, StringComparer.Ordinal)
                .Select(x => x.Code)
                .FirstOrDefault();
            if (nearer is not null)
                return $"FRT {source} {nearer} {view.Armies(source) - 1}";
        }
        return null;
    }

    /// <summary>The prey, and the cost of the cheapest way into his land from every territory not the seat's.</summary>
    private sealed record Hunt(string Prey, Dictionary<string, int> Route)
    {
        /// <summary>From one of the seat's territories: the cost of the cheapest way into the prey's land (a neighbour's route cost), optionally not through one neighbour.</summary>
        public int NextStep(View view, string own, string? except = null) =>
            view.Neighbors(own)
                .Where(n => n != except && Route.ContainsKey(n))
                .Select(n => Route[n])
                .DefaultIfEmpty(int.MaxValue)
                .Min();
    }

    // ------------------------------------------------------------------
    // Reading the board block back out of the prompt text
    // ------------------------------------------------------------------

    /// <summary>The prompt's board, read the way a player reads it, with the printed borders beside it.</summary>
    private sealed class View
    {
        private readonly Dictionary<string, (string Owner, int Armies)> _territories;
        private readonly IBoardData _board;
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public View(Dictionary<string, (string Owner, int Armies)> territories, string seat, IBoardData board, bool isDuo)
        {
            _territories = territories;
            _board = board;
            Seat = seat;
            IsDuo = isDuo;
            foreach (var (owner, _) in territories.Values)
                _counts[owner] = _counts.GetValueOrDefault(owner) + 1;
        }

        public string Seat { get; }

        public bool IsDuo { get; }

        public string Owner(string code) => _territories.TryGetValue(code, out var t) ? t.Owner : HostConstants.UnclaimedOwnerToken;

        public int Armies(string code) => _territories.TryGetValue(code, out var t) ? t.Armies : 0;

        public int TerritoryCount(string owner) => _counts.GetValueOrDefault(owner);

        public IReadOnlyList<string> Neighbors(string code) => _board.Territory(code).Neighbors;

        public IEnumerable<string> Owned() =>
            _territories.Where(kv => kv.Value.Owner == Seat).Select(kv => kv.Key).OrderBy(c => c, StringComparer.Ordinal);

        public List<string> Unclaimed() =>
            _territories.Where(kv => kv.Value.Owner == HostConstants.UnclaimedOwnerToken)
                .Select(kv => kv.Key).OrderBy(c => c, StringComparer.Ordinal).ToList();

        /// <summary>Another seat's territory (the Rebels' included); unclaimed land is nobody's.</summary>
        public bool IsEnemy(string code)
        {
            string owner = Owner(code);
            return owner != Seat && owner != HostConstants.UnclaimedOwnerToken;
        }

        /// <summary>Another player's seat code: one that can attack (not the Rebels, who never do, and not the unclaimed token).</summary>
        public bool IsPlayer(string owner) =>
            owner != Seat && owner != HostConstants.UnclaimedOwnerToken && owner != RulesConstants.RebelCode;

        /// <summary>A territory another player holds: one from which an attack can come.</summary>
        public bool IsThreat(string code) => IsPlayer(Owner(code));

        /// <summary>The seat's territory with another player's territory beside it: a front to hold.</summary>
        public bool FacesThreat(string code) => Neighbors(code).Any(IsThreat);

        /// <summary>The seat's territory with another player's beside it, other than the one given.</summary>
        public bool FacesThreatBesides(string code, string except) => Neighbors(code).Any(n => n != except && IsThreat(n));

        /// <summary>How far the strongest player beside a territory outnumbers it (negative when none does).</summary>
        public int Pressure(string code) =>
            Neighbors(code).Where(IsThreat).Select(Armies).DefaultIfEmpty(0).Max() - Armies(code);

        /// <summary>Every territory on the board, in code order.</summary>
        public IEnumerable<string> All() => _territories.Keys.OrderBy(c => c, StringComparer.Ordinal);

        /// <summary>Every other player still holding land.</summary>
        public IEnumerable<string> OtherPlayers() => _counts.Keys.Where(IsPlayer).OrderBy(c => c, StringComparer.Ordinal);

        /// <summary>A seat's armies on the board.</summary>
        public int TotalArmies(string owner) => _territories.Values.Where(t => t.Owner == owner).Sum(t => t.Armies);

        /// <summary>The share of a continent's territories a seat holds.</summary>
        public double ContinentShare(string continent, string owner)
        {
            var members = _board.Continent(continent).Members;
            return (double)members.Count(m => Owner(m) == owner) / members.Count;
        }

        /// <summary>The largest share of a continent any other player (not the Rebels) holds.</summary>
        public double ContinentRivalShare(string continent)
        {
            var members = _board.Continent(continent).Members;
            return members
                .Select(Owner)
                .Where(o => o != Seat && o != HostConstants.UnclaimedOwnerToken && o != RulesConstants.RebelCode)
                .GroupBy(o => o)
                .Select(g => (double)g.Count() / members.Count)
                .DefaultIfEmpty(0)
                .Max();
        }

        /// <summary>The seat's territories reachable from one of them through its own land.</summary>
        public List<string> ReachableThroughOwn(string start)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var queue = new Queue<string>();
            queue.Enqueue(start);
            while (queue.Count > 0)
                foreach (string next in Neighbors(queue.Dequeue()))
                    if (Owner(next) == Seat && seen.Add(next))
                        queue.Enqueue(next);
            seen.Remove(start);
            return seen.OrderBy(c => c, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>True when the board block shows the Rebels' faction holding land.</summary>
    private static bool HasRebels(string prompt) =>
        Regex.IsMatch(prompt, $@"(?m)^[A-Z]{{3}} {RulesConstants.RebelCode} \d+\r?$");

    /// <summary>Parses the board block's territory lines into code -> (owner, armies).</summary>
    private static Dictionary<string, (string Owner, int Armies)> ParseBoard(string prompt)
    {
        var map = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        foreach (string rawLine in prompt.Split('\n'))
        {
            // The pattern is anchored, so a trailing '\r' left over from
            // CRLF line endings would silently match nothing at all —
            // a real failure, where the map came
            // up empty on Windows and the seat threw below. Trim it.
            string line = rawLine.TrimEnd('\r');
            var match = Regex.Match(line, @"^([A-Z]{3}) ([A-Z]+) (\d+)$");
            if (match.Success)
                map[match.Groups[1].Value] = (match.Groups[2].Value, int.Parse(match.Groups[3].Value));
        }
        if (map.Count == 0) throw new InvalidOperationException("Scripted policy found no board block in the prompt.");
        return map;
    }

    /// <summary>Normalizes CRLF (and any lone CR) line endings to "\n"; see Respond.</summary>
    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Match(string text, string pattern, string what)
    {
        var match = Regex.Match(text, pattern);
        if (!match.Success) throw new InvalidOperationException($"Scripted policy could not find the {what} in the prompt.");
        return match.Groups[1].Value;
    }
}
