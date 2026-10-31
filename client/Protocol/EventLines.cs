using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentRisk.Client.Protocol;

/// <summary>One combat round of a round-by-round battle, faces as rolled.</summary>
public sealed record CombatRound(
    int AttackerDice, int DefenderDice,
    IReadOnlyList<int> AttackerFaces, IReadOnlyList<int> DefenderFaces,
    int AttackerLosses, int DefenderLosses,
    string? CapturedCode, int? MovedIn);

/// <summary>A whole blitz, compressed in the log to totals; the individual faces live only in the full game log.</summary>
public sealed record BlitzResult(
    int Rounds, int AttackerLosses, int DefenderLosses,
    string? CapturedCode, int? MovedIn)
{
    /// <summary>
    /// The totals as the dice tray labels them, one captioned fact
    /// each. The round count is a counted noun in words ("1 round",
    /// never "1 rounds"), the losses stand as the log reports them,
    /// and a capture is named when there was one. Territory names
    /// resolve through the caller's catalog.
    /// </summary>
    public IReadOnlyList<(string Caption, string Value)> Facts(Func<string, string> territoryName)
    {
        var facts = new List<(string Caption, string Value)>
        {
            ("Resolved", $"all at once, in {Rounds} {(Rounds == 1 ? "round" : "rounds")}"),
            ("Attacker lost", AttackerLosses.ToString()),
            ("Defender lost", DefenderLosses.ToString()),
        };
        if (CapturedCode is not null) facts.Add(("Captured", territoryName(CapturedCode)));
        return facts;
    }
}

/// <summary>
/// One battle as the summaries report it: who attacked, from where, at what,
/// the mode, and each round's dice as they surface. The defender is resolved
/// from the table's ownership at the moment of the declaration (see
/// <see cref="BattleTracker"/>), because by the time a summary is read the
/// target may already have changed hands.
/// </summary>
public sealed class Battle
{
    public required string Attacker { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public string? Defender { get; internal set; }
    public required bool IsBlitz { get; init; }
    public List<CombatRound> Rounds { get; } = new();
    public BlitzResult? Blitz { get; internal set; }
    public bool Captured => Blitz?.CapturedCode is not null || Rounds.Any(r => r.CapturedCode is not null);
}

/// <summary>
/// Folds round-summary lines into battles for the dice display.
/// The tracker walks the lines in order with a working copy of who owns
/// what — seeded from the previous board the table showed — so a battle's
/// defender is the seat that held the target when the attack was declared,
/// and a capture updates the copy for whatever the same summary reports
/// next. Everything else in a summary passes through untouched; this class
/// reads combat only.
/// </summary>
public sealed class BattleTracker
{
    private static readonly Regex AttackDeclaredPattern = new(
        @"^([A-Z]{3}) ATK ([A-Z]{3})>([A-Z]{3}) (RND|ALL)$");
    private static readonly Regex CombatRoundPattern = new(
        @"^RND (\d+)v(\d+) A\[([\d ]+)\] D\[([\d ]+)\] A-(\d+) D-(\d+)(?: \| CAP ([A-Z]{3}) MOV(\d+))?$");
    private static readonly Regex BlitzPattern = new(
        @"^ALL x(\d+) A-(\d+) D-(\d+)(?: \| CAP ([A-Z]{3}) MOV(\d+))?$");
    private static readonly Regex ClaimPattern = new(
        @"^([A-Z]{3}) CLAIM ([A-Z]{3})(?: BY [A-Z]{3})?$");

    private readonly Dictionary<string, string?> _ownership = new(StringComparer.Ordinal);
    private Battle? _open;

    /// <summary>The most recent battle seen, still collecting rounds while it is the latest event.</summary>
    public Battle? LastBattle { get; private set; }

    /// <summary>
    /// Seeds the ownership copy from a board the table showed. Call with the
    /// board from the seat's previous prompt before folding a new summary,
    /// or with any board to (re)anchor the tracker.
    /// </summary>
    public void SeedOwnership(BoardState board)
    {
        _ownership.Clear();
        foreach (var territory in board.Territories.Values)
            _ownership[territory.Code] = territory.Owner;
    }

    /// <summary>Folds one summary, line by line. Returns every battle the summary completed or started.</summary>
    public IReadOnlyList<Battle> FoldSummary(IEnumerable<string> lines)
    {
        var touched = new List<Battle>();
        foreach (string line in lines)
        {
            var claim = ClaimPattern.Match(line);
            if (claim.Success)
            {
                // A Rebel claim line names the recipient as its actor; a
                // self-claim names the claimant. Either way the actor owns it.
                _ownership[claim.Groups[2].Value] = claim.Groups[1].Value;
                continue;
            }

            var attack = AttackDeclaredPattern.Match(line);
            if (attack.Success)
            {
                string to = attack.Groups[3].Value;
                _open = new Battle
                {
                    Attacker = attack.Groups[1].Value,
                    From = attack.Groups[2].Value,
                    To = to,
                    Defender = _ownership.TryGetValue(to, out string? owner) ? owner : null,
                    IsBlitz = attack.Groups[4].Value == "ALL",
                };
                LastBattle = _open;
                touched.Add(_open);
                continue;
            }

            var round = CombatRoundPattern.Match(line);
            if (round.Success && _open is not null)
            {
                var parsed = new CombatRound(
                    int.Parse(round.Groups[1].Value), int.Parse(round.Groups[2].Value),
                    ParseFaces(round.Groups[3].Value), ParseFaces(round.Groups[4].Value),
                    int.Parse(round.Groups[5].Value), int.Parse(round.Groups[6].Value),
                    round.Groups[7].Success ? round.Groups[7].Value : null,
                    round.Groups[8].Success ? int.Parse(round.Groups[8].Value) : null);
                _open.Rounds.Add(parsed);
                if (parsed.CapturedCode is not null)
                    _ownership[parsed.CapturedCode] = _open.Attacker;
                continue;
            }

            var blitz = BlitzPattern.Match(line);
            if (blitz.Success && _open is not null)
            {
                _open.Blitz = new BlitzResult(
                    int.Parse(blitz.Groups[1].Value),
                    int.Parse(blitz.Groups[2].Value), int.Parse(blitz.Groups[3].Value),
                    blitz.Groups[4].Success ? blitz.Groups[4].Value : null,
                    blitz.Groups[5].Success ? int.Parse(blitz.Groups[5].Value) : null);
                if (_open.Blitz.CapturedCode is not null)
                    _ownership[_open.Blitz.CapturedCode] = _open.Attacker;
            }
        }
        return touched;
    }

    private static IReadOnlyList<int> ParseFaces(string faces) =>
        faces.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
}
