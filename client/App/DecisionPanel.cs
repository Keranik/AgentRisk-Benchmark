using System;
using System.Collections.Generic;
using System.Linq;
using AgentRisk.Client.Protocol;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>What the board should ring for the decision in progress.</summary>
public sealed record HighlightRequest(
    IReadOnlyCollection<string> Selectable, string? Selected, IReadOnlyCollection<string> Targets);

/// <summary>
/// The decision surface: for each prompt, the legal choices as controls —
/// board picks with a confirm, dropdown fallbacks where a phase has many
/// routes, buttons for dice and sets, a stepper for counts. Every control
/// sends exactly one reply line in the prompt's own grammar
/// (<see cref="ReplyComposer"/>); the panel never invents a choice the
/// board state and the printed board do not make legal. It computes
/// legality only — which territories border which, what connects through
/// the player's own land — never anything about what a choice is worth.
/// </summary>
public partial class DecisionPanel : VBoxContainer
{
    private HFlowContainer _context = null!;
    private FactField _phaseFact = null!;
    private FactField _roundFact = null!;
    private FactField _numberFact = null!;
    private Label _headline = null!;
    private Label _subline = null!;
    private Label _errorLine = null!;
    private VBoxContainer _body = null!;
    private ScrollContainer _scroll = null!;

    // The body grows to hold its controls, so a decision's buttons are on
    // screen without scrolling; only a long list (the strike-3 moves, a
    // fistful of trade sets) past this height scrolls inside the panel.
    private const float BodyFloor = 60f;
    private const float BodyCap = 340f;

    private DecisionPrompt? _prompt;
    private BoardCatalog? _catalog;
    private string _mySeat = "";
    private bool _answered = true;

    // Board-pick state shared by the click flows and their dropdown twins.
    private string? _pickedTerritory;
    private string? _routeFrom;
    private string? _routeTo;

    /// <summary>Raised with the one reply line the player chose.</summary>
    public event Action<string>? ReplyReady;

    /// <summary>Raised whenever the board's rings should change for this decision.</summary>
    public event Action<HighlightRequest>? HighlightsChanged;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);

        // Where the game stands for this decision, each value under its
        // caption; the decision's number is the one the record and the
        // transcript use, so a reader can find it there afterwards.
        _context = FactField.Row();
        _phaseFact = new FactField("Current phase",
            "The part of the turn this decision belongs to. Setup: claiming territories and placing starting " +
            "armies. Then each turn: reinforcements, attack, fortify.");
        _roundFact = new FactField("Round", "A round is every seat taking one turn, in turn order.");
        _numberFact = new FactField("Decision",
            "This decision's number. Every seat's decisions are numbered in one sequence from the start of the " +
            "game; the record and the transcript use the same number.");
        _context.AddChild(_phaseFact);
        _context.AddChild(_roundFact);
        _context.AddChild(_numberFact);
        _context.Visible = false;
        AddChild(_context);

        _headline = Ui.Text("", UiTheme.HeadingLabel);
        AddChild(_headline);

        _subline = Ui.Text("");
        AddChild(_subline);

        _errorLine = Ui.Text("", UiTheme.WarningLabel);
        _errorLine.Visible = false;
        AddChild(_errorLine);

        _scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            // The body holds the controls that answer the decision; it
            // must never be squeezed to nothing by taller neighbours.
            CustomMinimumSize = new Vector2(0, BodyFloor),
        };
        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 8);
        _scroll.AddChild(_body);
        AddChild(_scroll);
    }

    /// <summary>
    /// Sizes the body's viewport to its controls (between the floor and
    /// the cap), now and again once the frame has laid out wrapped text.
    /// </summary>
    private void FitBody()
    {
        void Fit()
        {
            if (!IsInstanceValid(_body)) return;
            float wanted = _body.GetCombinedMinimumSize().Y;
            _scroll.CustomMinimumSize = new Vector2(0, Math.Clamp(wanted, BodyFloor, BodyCap));
        }
        Fit();
        Callable.From(Fit).CallDeferred();
    }

    // ------------------------------------------------------------------
    // Entry points (called by Main for each host block)
    // ------------------------------------------------------------------

    /// <summary>Presents one full decision prompt.</summary>
    public void ShowDecision(DecisionPrompt prompt, BoardCatalog catalog)
    {
        _prompt = prompt;
        _catalog = catalog;
        _mySeat = prompt.Seat;
        _answered = false;
        _pickedTerritory = null;
        _routeFrom = null;
        _routeTo = null;

        _errorLine.Visible = prompt.LastError is not null;
        _errorLine.Text = prompt.LastError is null ? "" : $"That move was not legal. {prompt.LastError}";
        _phaseFact.Set(UiConstants.PhaseTitle(prompt.Phase));
        _roundFact.Set(prompt.Round.ToString());
        _numberFact.Set(prompt.Number.ToString());
        _context.Visible = true;

        switch (prompt.Ask)
        {
            case DecisionAsk.Claim claim: BuildClaim(claim); break;
            case DecisionAsk.SetupPlace place: BuildSetupPlace(place); break;
            case DecisionAsk.Trade trade: BuildTrade(trade); break;
            case DecisionAsk.Place place: BuildPlace(place); break;
            case DecisionAsk.MatchPlace match: BuildMatchPlace(match); break;
            case DecisionAsk.AttackDeclare declare: BuildAttackDeclare(declare); break;
            case DecisionAsk.AttackDice dice: BuildAttackDice(dice); break;
            case DecisionAsk.DefendDice dice: BuildDefendDice(dice); break;
            case DecisionAsk.CaptureMove move: BuildCaptureMove(move); break;
            case DecisionAsk.Fortify: BuildFortify(); break;
            default: BuildUnknown(); break;
        }
        FitBody();
    }

    /// <summary>Presents the strike-3 list: the listed moves, as buttons, nothing else.</summary>
    public void ShowStrikeThree(IReadOnlyList<string> moves)
    {
        _prompt = null;
        _answered = false;
        _errorLine.Visible = false;
        _context.Visible = false;
        SetHeadline("Choose a move", "Two replies were not accepted. Pick one of the legal moves below.");
        ClearBody();
        for (int i = 0; i < moves.Count; i++)
        {
            int number = i + 1;
            AddButton($"{number}.  {moves[i]}", () => Send(ReplyComposer.NumberedMove(number)));
        }
        FitBody();
        EmitHighlights(Array.Empty<string>(), null, Array.Empty<string>());
    }

    /// <summary>
    /// Fallback for a block the client could not classify at all: the raw
    /// text, word for word, and a hand-typed reply. The seat is never left
    /// speechless by a format this window does not know.
    /// </summary>
    public void ShowRawBlock(string rawText)
    {
        _prompt = null;
        _answered = false;
        _errorLine.Visible = false;
        _context.Visible = false;
        SetHeadline("Your move", "The table sent this; answer it with one line.");
        ClearBody();
        foreach (string line in rawText.Split('\n')) AddText(line);
        var entry = new LineEdit { PlaceholderText = "Reply line" };
        _body.AddChild(entry);
        AddButton("Send the reply", () =>
        {
            string text = entry.Text.Trim();
            if (text.Length > 0) Send(text);
        });
        EmitHighlights(Array.Empty<string>(), null, Array.Empty<string>());
    }

    /// <summary>Clears the surface to a waiting state (after a reply, or between decisions).</summary>
    public void ShowWaiting(string headline, string subline = "")
    {
        _answered = true;
        _errorLine.Visible = false;
        _context.Visible = false;
        SetHeadline(headline, subline);
        ClearBody();
        EmitHighlights(Array.Empty<string>(), null, Array.Empty<string>());
    }

    /// <summary>Routes a board click into the decision in progress.</summary>
    public void HandleTerritoryClick(string code)
    {
        if (_answered || _prompt is null) return;

        switch (_prompt.Ask)
        {
            case DecisionAsk.Claim:
                if (IsUnclaimed(code)) { _pickedTerritory = code; RefreshTerritoryPicker(); }
                break;
            case DecisionAsk.SetupPlace:
                if (IsMine(code)) { _pickedTerritory = code; RefreshTerritoryPicker(); }
                break;
            case DecisionAsk.Place:
                if (IsMine(code)) { _pickedTerritory = code; RefreshTerritoryPicker(); }
                break;
            case DecisionAsk.MatchPlace match:
                // The bonus goes on a pictured territory; clicking one places it there.
                if (match.Candidates.Contains(code))
                    Send(ReplyComposer.Place(code, match.Count));
                break;
            case DecisionAsk.AttackDeclare:
                HandleRouteClick(code, AttackSources(), AttackTargetsOf);
                break;
            case DecisionAsk.Fortify:
                HandleRouteClick(code, FortifySources(), FortifyDestinationsOf);
                break;
        }
    }

    // ------------------------------------------------------------------
    // Claim / setup placement / reinforcement placement: pick, then confirm
    // ------------------------------------------------------------------

    private Button? _confirmButton;

    private void BuildClaim(DecisionAsk.Claim claim)
    {
        SetHeadline(
            claim.ForRebels ? "Choose a territory for the Rebels" : "Claim a territory",
            claim.ForRebels
                ? "It is the Rebels' claim turn. Pick the land they receive."
                : "Pick an unclaimed territory on the board.");
        BeginTerritoryPicker(UnclaimedCodes(), code => ReplyComposer.Claim(code), () => "Claim");
    }

    private void BuildSetupPlace(DecisionAsk.SetupPlace place)
    {
        SetHeadline("Place a setup army",
            $"{place.ArmiesLeftIncludingThis} of your setup armies remain, this one included. Pick one of your territories.");
        BeginTerritoryPicker(MyCodes(), code => ReplyComposer.SetupPlace(code), () => "Place 1 army on");
    }

    private void BuildPlace(DecisionAsk.Place place)
    {
        SetHeadline("Place reinforcements",
            $"{place.Remaining} to place. Pick one of your territories on the map, set how many, and place them.");
        ClearBody();
        var count = AddCountPicker("Armies to place", 1, place.Remaining, place.Remaining);
        BeginTerritoryPicker(MyCodes(),
            code => ReplyComposer.Place(code, count.Value), () => $"Place {count.Value} on", existingBody: true);
        count.ValueChanged += _ => RefreshTerritoryPicker();
    }

    private void BuildMatchPlace(DecisionAsk.MatchPlace match)
    {
        ClearBody();
        if (match.IsChoice)
        {
            // One bonus, several pictured territories: one button each,
            // and a click on any of them on the map places it there.
            SetHeadline("Place the card bonus",
                $"Your traded cards picture more than one of your territories. The {match.Count}-army bonus is " +
                "paid once this turn: choose which territory takes it.");
            foreach (string code in match.Candidates)
            {
                string territory = code;
                AddButton($"Place {match.Count} on {TerritoryName(territory)}",
                    () => Send(ReplyComposer.Place(territory, match.Count)), primary: true);
            }
            EmitHighlights(match.Candidates, null, Array.Empty<string>());
            return;
        }
        SetHeadline("Place the card bonus",
            $"Your traded card pictures {TerritoryName(match.Territory)}: its {match.Count} bonus armies go there.");
        AddButton($"Place {match.Count} on {TerritoryName(match.Territory)}",
            () => Send(ReplyComposer.Place(match.Territory, match.Count)), primary: true);
        EmitHighlights(new[] { match.Territory }, match.Territory, Array.Empty<string>());
    }

    /// <summary>
    /// The shared pick-a-territory surface: a hint, a confirm button that
    /// follows the pick, and the board rings on the legal picks.
    /// </summary>
    private void BeginTerritoryPicker(IReadOnlyCollection<string> selectable,
        Func<string, string> compose, Func<string> verb, bool existingBody = false)
    {
        if (!existingBody) ClearBody();
        // Labelled from the start, while it waits for a pick: an
        // unlabelled disabled button is invisible in practice, and a
        // player who cannot find the confirm cannot answer at all.
        _confirmButton = AddButton("Pick a territory on the map", () =>
        {
            if (_pickedTerritory is not null) Send(compose(_pickedTerritory));
        }, primary: true);
        _confirmButton.Disabled = true;
        _pickerVerb = verb;
        _pickerSelectable = selectable;
        EmitHighlights(selectable, null, Array.Empty<string>());
    }

    private Func<string> _pickerVerb = () => "";
    private IReadOnlyCollection<string> _pickerSelectable = Array.Empty<string>();

    private void RefreshTerritoryPicker()
    {
        if (_confirmButton is null || _pickedTerritory is null) return;
        _confirmButton.Text = $"{_pickerVerb()} {TerritoryName(_pickedTerritory)}";
        _confirmButton.Disabled = false;
        EmitHighlights(_pickerSelectable, _pickedTerritory, Array.Empty<string>());
    }

    // ------------------------------------------------------------------
    // Trade
    // ------------------------------------------------------------------

    private void BuildTrade(DecisionAsk.Trade trade)
    {
        SetHeadline(trade.Forced ? "Trade a set" : "Trade a set, if you like",
            trade.Forced
                ? "Your hand is over the limit: a trade must happen before anything else."
                : "Trading is worth armies now, and the next set pays more later. Your call.");
        ClearBody();

        foreach (var set in trade.Sets)
        {
            string cards = string.Join(", ", set.Cards.Select(c => _catalog!.CardDisplayName(c)));
            AddButton($"Set {set.Number}: {DescribeSet(set.Description)}. Cards: {cards}.",
                () => Send(ReplyComposer.TradeSet(set.Number)));
        }

        if (trade.CanDecline)
            AddButton("Keep my cards", () => Send(ReplyComposer.TradeNone()));
        EmitHighlights(Array.Empty<string>(), null, Array.Empty<string>());
    }

    /// <summary>The set's shape in the words the table uses (the ask prints INF/CAV/ART codes).</summary>
    private static string DescribeSet(string description) => description switch
    {
        "three INF" => "three infantry",
        "three CAV" => "three cavalry",
        "three ART" => "three artillery",
        "one of each" => "one of each",
        "wild set" => "wild set",
        _ => description,
    };

    // ------------------------------------------------------------------
    // Attack declaration: board route picking, with dropdown twins
    // ------------------------------------------------------------------

    private OptionButton? _fromPicker;
    private OptionButton? _toPicker;
    private Button? _attackRoundButton;
    private Button? _attackAllButton;
    private List<string> _fromOptions = new();
    private List<string> _toOptions = new();

    private void BuildAttackDeclare(DecisionAsk.AttackDeclare declare)
    {
        // The attack is three picks at most: where from, where into, and
        // how it rolls; the third pick sends it. The end of the phase
        // stands below, away from the attack buttons.
        SetHeadline("Your attack", declare.HasAttacks ? "" : "No attack is open to you this turn.");
        ClearBody();
        if (!declare.HasAttacks)
        {
            AddButton("End attack phase", () => Send(ReplyComposer.EndAttack()), primary: true);
            EmitHighlights(Array.Empty<string>(), null, Array.Empty<string>());
            return;
        }

        _fromOptions = AttackSources();
        _fromPicker = AddPickerRow("From", _fromOptions.Select(c => $"{TerritoryName(c)} ({ArmiesOf(c)})"),
            "Your territory to attack from");
        _toPicker = AddPickerRow("Into", Array.Empty<string>(), "The territory to attack");
        _fromPicker.ItemSelected += _ =>
        {
            _routeFrom = _fromOptions[(int)_fromPicker.Selected];
            SyncRoutePickers(isAttack: true);
        };
        _toPicker.ItemSelected += _ =>
        {
            if (_toOptions.Count > 0) _routeTo = _toOptions[(int)_toPicker.Selected];
            RefreshAttackButtons();
            EmitRouteHighlights(AttackTargetsOf);
        };

        var attackRow = new HBoxContainer();
        attackRow.AddThemeConstantOverride("separation", 8);
        _attackRoundButton = RowButton(attackRow, "Attack: round by round", primary: true,
            "Roll one round; you are asked again before each next round.",
            () => SendAttack("RND"));
        _attackAllButton = RowButton(attackRow, "Attack: until it resolves", primary: true,
            "Keep rolling with the most dice until the territory falls or you cannot attack further.",
            () => SendAttack("ALL"));
        _body.AddChild(attackRow);

        AddButton("End attack phase", () => Send(ReplyComposer.EndAttack()));
        RefreshAttackButtons();
        EmitHighlights(_fromOptions, null, Array.Empty<string>());
    }

    private void SendAttack(string mode)
    {
        if (_routeFrom is not null && _routeTo is not null)
            Send(ReplyComposer.Attack(_routeFrom, _routeTo, mode));
    }

    private void RefreshAttackButtons()
    {
        bool ready = _routeFrom is not null && _routeTo is not null;
        if (_attackRoundButton is not null) _attackRoundButton.Disabled = !ready;
        if (_attackAllButton is not null) _attackAllButton.Disabled = !ready;
    }

    // ------------------------------------------------------------------
    // Dice, capture move-in
    // ------------------------------------------------------------------

    private void BuildAttackDice(DecisionAsk.AttackDice dice)
    {
        SetHeadline($"{TerritoryName(dice.From)} attacks {TerritoryName(dice.To)}",
            dice.CanStop ? "Roll the next round, or stop attacking." : "Roll the first round.");
        ClearBody();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        for (int n = 1; n <= dice.MaxDice; n++)
        {
            int count = n;
            RowButton(row, count == 1 ? "Roll 1 die" : $"Roll {count} dice", primary: count == dice.MaxDice, "",
                () => Send(ReplyComposer.Dice(count)));
        }
        _body.AddChild(row);
        if (dice.CanStop)
            AddButton("Stop attacking", () => Send(ReplyComposer.Stop()));
        EmitHighlights(new[] { dice.From }, dice.From, new[] { dice.To });
    }

    private void BuildDefendDice(DecisionAsk.DefendDice dice)
    {
        SetHeadline($"{UiConstants.SeatDisplayName(dice.Attacker)} attacks your {TerritoryName(dice.To)}",
            $"From {TerritoryName(dice.From)}. Choose how many dice defend.");
        ClearBody();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        for (int n = 1; n <= dice.MaxDice; n++)
        {
            int count = n;
            RowButton(row, count == 1 ? "Defend with 1 die" : $"Defend with {count} dice",
                primary: count == dice.MaxDice, "", () => Send(ReplyComposer.Dice(count)));
        }
        _body.AddChild(row);
        EmitHighlights(new[] { dice.To }, dice.To, new[] { dice.From });
    }

    private void BuildCaptureMove(DecisionAsk.CaptureMove move)
    {
        SetHeadline($"{TerritoryName(move.To)} is yours",
            $"Move armies in from {TerritoryName(move.From)}: at least {move.Min}, at most {move.Max}.");
        ClearBody();
        var count = AddCountPicker("Armies to move in", move.Min, move.Max, move.Min);
        var button = AddButton("", () => Send(ReplyComposer.MoveIn(count.Value)), primary: true);
        void Retitle() => button.Text = count.Value == 1
            ? $"Move 1 army into {TerritoryName(move.To)}"
            : $"Move {count.Value} armies into {TerritoryName(move.To)}";
        count.ValueChanged += _ => Retitle();
        Retitle();
        EmitHighlights(new[] { move.From }, move.From, new[] { move.To });
    }

    // ------------------------------------------------------------------
    // Fortify
    // ------------------------------------------------------------------

    private CountPicker? _fortifyCount;
    private Button? _fortifyButton;

    private void BuildFortify()
    {
        SetHeadline("Fortify", "One move through your own land (the source keeps at least 1), or skip it.");
        ClearBody();

        _fromOptions = FortifySources();
        if (_fromOptions.Count == 0)
        {
            AddText("No fortify is open: no territory of yours can reach another through your land.");
            AddButton("Skip the fortify", () => Send(ReplyComposer.FortifySkip()), primary: true);
            EmitHighlights(Array.Empty<string>(), null, Array.Empty<string>());
            return;
        }

        _fromPicker = AddPickerRow("From", _fromOptions.Select(c => $"{TerritoryName(c)} ({ArmiesOf(c)})"),
            "Your territory to move from");
        _toPicker = AddPickerRow("Into", Array.Empty<string>(), "Where the armies go");
        _fromPicker.ItemSelected += _ =>
        {
            _routeFrom = _fromOptions[(int)_fromPicker.Selected];
            SyncRoutePickers(isAttack: false);
        };
        _toPicker.ItemSelected += _ =>
        {
            if (_toOptions.Count > 0) _routeTo = _toOptions[(int)_toPicker.Selected];
            RefreshFortifyControls();
            EmitRouteHighlights(FortifyDestinationsOf);
        };

        _fortifyCount = AddCountPicker("Armies to move", 1, 1, 1);
        _fortifyCount.ValueChanged += _ => RefreshFortifyControls();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _fortifyButton = RowButton(row, "Make the move", primary: true, "", () =>
        {
            if (_routeFrom is not null && _routeTo is not null && _fortifyCount is not null)
                Send(ReplyComposer.Fortify(_routeFrom, _routeTo, _fortifyCount.Value));
        });
        RowButton(row, "Skip the fortify", primary: false, "", () => Send(ReplyComposer.FortifySkip()));
        _body.AddChild(row);
        RefreshFortifyControls();

        EmitHighlights(_fromOptions, null, Array.Empty<string>());
    }

    private void RefreshFortifyControls()
    {
        if (_fortifyButton is null || _fortifyCount is null) return;
        if (_routeFrom is not null)
            _fortifyCount.SetMaximum(Math.Max(1, ArmiesOf(_routeFrom) - 1)); // the source always keeps at least 1
        bool ready = _routeFrom is not null && _routeTo is not null;
        _fortifyButton.Disabled = !ready;
        _fortifyButton.Text = ready
            ? $"Move {_fortifyCount.Value} to {TerritoryName(_routeTo!)}"
            : "Make the move";
    }

    // ------------------------------------------------------------------
    // Route picking (attack and fortify share the two-click shape)
    // ------------------------------------------------------------------

    private void HandleRouteClick(string code, List<string> sources, Func<string, List<string>> destinationsOf)
    {
        if (_routeFrom is null || !destinationsOf(_routeFrom).Contains(code))
        {
            if (sources.Contains(code))
            {
                _routeFrom = code;
                _routeTo = null;
                SyncRoutePickers(isAttack: _prompt!.Ask is DecisionAsk.AttackDeclare);
                return;
            }
        }
        if (_routeFrom is not null && destinationsOf(_routeFrom).Contains(code))
        {
            _routeTo = code;
            SyncRoutePickers(isAttack: _prompt!.Ask is DecisionAsk.AttackDeclare, keepRoute: true);
        }
    }

    /// <summary>Keeps the dropdown twins, count ranges, and board rings in step with the route state.</summary>
    private void SyncRoutePickers(bool isAttack, bool keepRoute = false)
    {
        if (!keepRoute) _routeTo = null;
        if (_fromPicker is not null && _routeFrom is not null)
        {
            int at = _fromOptions.IndexOf(_routeFrom);
            if (at >= 0 && _fromPicker.Selected != at) _fromPicker.Select(at);
        }

        var destinationsOf = isAttack ? (Func<string, List<string>>)AttackTargetsOf : FortifyDestinationsOf;
        _toOptions = _routeFrom is null ? new List<string>() : destinationsOf(_routeFrom);
        if (_toPicker is not null)
        {
            _toPicker.Clear();
            foreach (string code in _toOptions)
                _toPicker.AddItem($"{TerritoryName(code)} ({ArmiesOf(code)})");
            int at = _routeTo is null ? -1 : _toOptions.IndexOf(_routeTo);
            ShowPickerChoice(_toPicker, at, _toOptions.Count == 0 ? "Choose where from first" : "Choose the target");
        }

        if (isAttack) RefreshAttackButtons(); else RefreshFortifyControls();
        EmitRouteHighlights(destinationsOf);
    }

    private void EmitRouteHighlights(Func<string, List<string>> destinationsOf)
    {
        var selectable = _prompt!.Ask is DecisionAsk.AttackDeclare ? AttackSources() : FortifySources();
        var targets = _routeFrom is null ? Array.Empty<string>() : destinationsOf(_routeFrom).ToArray();
        // The ring follows the freshest pick: the destination once chosen,
        // otherwise the source.
        EmitHighlights(selectable, _routeTo ?? _routeFrom, targets);
    }

    // ------------------------------------------------------------------
    // The fallback surface: an ask the client could not classify
    // ------------------------------------------------------------------

    private void BuildUnknown()
    {
        SetHeadline("Your move", "The table's wording is new to this window; its ask is below, word for word.");
        ClearBody();
        foreach (string line in _prompt!.AskLines) AddText(line);
        var entry = new LineEdit { PlaceholderText = "Reply line" };
        _body.AddChild(entry);
        AddButton("Send the reply", () =>
        {
            string text = entry.Text.Trim();
            if (text.Length > 0) Send(text);
        });
        EmitHighlights(Array.Empty<string>(), null, Array.Empty<string>());
    }

    // ------------------------------------------------------------------
    // Legality derivations: read off the board and the printed adjacency.
    // These enumerate choices; they say nothing about which is wise.
    // ------------------------------------------------------------------

    private BoardState Board => _prompt!.Board;

    private bool IsMine(string code) => Board.Territories.TryGetValue(code, out var t) && t.Owner == _mySeat;

    private bool IsUnclaimed(string code) => Board.Territories.TryGetValue(code, out var t) && t.Owner is null;

    private int ArmiesOf(string code) => Board.Territories.TryGetValue(code, out var t) ? t.Armies : 0;

    private List<string> MyCodes() => Board.OwnedBy(_mySeat).Select(t => t.Code).ToList();

    private List<string> UnclaimedCodes() => Board.Unclaimed().Select(t => t.Code).ToList();

    private List<string> NeighborsOf(string code) =>
        _catalog is not null && _catalog.TryTerritory(code, out var info) ? info.Neighbors.ToList() : new List<string>();

    private List<string> AttackSources() => MyCodes()
        .Where(code => ArmiesOf(code) >= 2 && AttackTargetsOf(code).Count > 0)
        .ToList();

    private List<string> AttackTargetsOf(string from) => NeighborsOf(from)
        .Where(code => Board.Territories.TryGetValue(code, out var t) && t.Owner is not null && t.Owner != _mySeat)
        .ToList();

    private List<string> FortifySources() => MyCodes()
        .Where(code => ArmiesOf(code) >= 2 && FortifyDestinationsOf(code).Count > 0)
        .ToList();

    /// <summary>Own territories reachable from a source through the player's own land.</summary>
    private List<string> FortifyDestinationsOf(string from)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();
        frontier.Enqueue(from);
        reached.Add(from);
        while (frontier.Count > 0)
        {
            foreach (string next in NeighborsOf(frontier.Dequeue()))
            {
                if (!IsMine(next) || !reached.Add(next)) continue;
                frontier.Enqueue(next);
            }
        }
        reached.Remove(from);
        return reached.OrderBy(code => code, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------------
    // Small builders
    // ------------------------------------------------------------------

    private void SetHeadline(string headline, string subline)
    {
        _headline.Text = headline;
        _subline.Text = subline;
        _subline.Visible = subline.Length > 0;
    }

    private void ClearBody()
    {
        // Out of the tree at once, not at the end of the frame, so the new
        // body's size is measured without the old controls in it.
        foreach (Node child in _body.GetChildren())
        {
            _body.RemoveChild(child);
            child.QueueFree();
        }
        _confirmButton = null;
        _fromPicker = null;
        _toPicker = null;
        _attackRoundButton = null;
        _attackAllButton = null;
        _fortifyCount = null;
        _fortifyButton = null;
    }

    private Label AddText(string text)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.Word };
        _body.AddChild(label);
        return label;
    }

    private Button AddButton(string text, Action onPressed, bool primary = false)
    {
        var button = new Button { Text = text, ThemeTypeVariation = primary ? UiTheme.PrimaryButton : "" };
        button.Pressed += () => onPressed();
        _body.AddChild(button);
        return button;
    }

    private CountPicker AddCountPicker(string caption, int min, int max, int initial)
    {
        var picker = new CountPicker(caption, min, max, initial);
        _body.AddChild(picker);
        return picker;
    }

    /// <summary>A button that shares a row with others, each taking an equal width.</summary>
    private static Button RowButton(HBoxContainer row, string text, bool primary, string tooltip, Action onPressed)
    {
        var button = new Button
        {
            Text = text,
            TooltipText = tooltip,
            ThemeTypeVariation = primary ? UiTheme.PrimaryButton : "",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
        };
        button.Pressed += () => onPressed();
        row.AddChild(button);
        return button;
    }

    private OptionButton AddPickerRow(string label, IEnumerable<string> items, string placeholder)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(44, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var picker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        foreach (string item in items) picker.AddItem(item);
        // Godot selects the first entry as items arrive; the list starts
        // empty instead, so it never shows a choice the player did not make.
        ShowPickerChoice(picker, -1, placeholder);
        row.AddChild(picker);
        _body.AddChild(row);
        return picker;
    }

    /// <summary>Shows a picker's chosen entry, or a prompt in its place when nothing is chosen yet.</summary>
    private static void ShowPickerChoice(OptionButton picker, int index, string placeholder)
    {
        picker.Select(index);
        if (index < 0) picker.Text = placeholder;
    }

    private string TerritoryName(string code) =>
        _catalog is not null && _catalog.TryCatalogName(code, out string name) ? name : code;

    private void Send(string replyLine)
    {
        if (_answered) return;
        _answered = true;
        ReplyReady?.Invoke(replyLine);
    }

    private void EmitHighlights(IReadOnlyCollection<string> selectable, string? selected, IReadOnlyCollection<string> targets) =>
        HighlightsChanged?.Invoke(new HighlightRequest(selectable, selected, targets));
}

/// <summary>Small catalog helpers the panel's sentences use.</summary>
internal static class CatalogNames
{
    public static bool TryCatalogName(this BoardCatalog catalog, string code, out string name)
    {
        if (catalog.TryTerritory(code, out var info))
        {
            name = info.Name;
            return true;
        }
        name = code;
        return false;
    }
}
