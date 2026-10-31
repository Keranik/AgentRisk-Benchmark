using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// The table's help: a plain reference, over the table, to what each part
/// of the screen shows and what each host control does. It teaches the
/// screen, never the game's strategy (no hints, here as
/// everywhere else). The host section shows only at the host door.
/// </summary>
public sealed partial class HelpPanel : Control
{
    private readonly RichTextLabel _text = new()
    {
        BbcodeEnabled = true,
        FitContent = true,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SelectionEnabled = true,
    };

    public HelpPanel()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        var dim = new ColorRect { Color = UiConstants.EditDimColor };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        dim.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true }) Visible = false;
        };
        AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(center);

        var card = new PanelContainer
        {
            ThemeTypeVariation = UiTheme.Card,
            CustomMinimumSize = UiConstants.HelpPanelMinSize,
        };
        center.AddChild(card);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        card.AddChild(body);

        var header = new HBoxContainer();
        header.AddChild(Ui.Heading("How to read this table"));
        header.AddChild(Ui.Spacer());
        var close = Ui.Button("Close", UiTheme.SmallButton);
        close.Pressed += () => Visible = false;
        header.AddChild(close);
        body.AddChild(header);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddChild(_text);
        body.AddChild(scroll);
    }

    /// <summary>Opens the help, with the host section when this app hosts the table.</summary>
    public void Open(bool hosting)
    {
        _text.Clear();
        _text.AppendText(Content(hosting));
        Visible = true;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (Visible && @event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Visible = false;
            GetViewport().SetInputAsHandled();
        }
    }

    private static string Content(bool hosting)
    {
        string text =
            "[b]The header[/b] labels the game's facts: its game type (Standard or Duo), its seed (the number that " +
            "fixes the dice, deck and deal), the round, the current phase of the turn, and the current player. " +
            "A turn runs in phases: reinforcements (trade cards and place new armies), attack, then fortify (one " +
            "move between connected territories); setup comes before the first turn. On the right are your colour " +
            "and this app's role: the app can host a table, join one as a client to play a seat, or watch one, and " +
            "the role it holds is lit. Hover over any caption for more.\n\n" +
            "[b]The banner[/b] under it says what the game is waiting for. Blue: the decision is yours, " +
            "and the banner says what to do. Grey: another seat is deciding. Amber: the host needs to act. " +
            "The table's latest notice (a connection, a re-sent prompt, a host action), when there is one, " +
            "stands in smaller type beneath.\n\n" +
            "[b]The map[/b] shows every territory in its owner's colour, with its army count on a chip of the " +
            "same colour. Pale land with no chip is unclaimed. Heavier lines are the borders between continents; " +
            "the badges in the sea give each continent's bonus for holding all of it. Dashed lines are sea routes " +
            "between territories that border each other across water. When a decision is yours, white rings mark " +
            "the territories you can pick, a gold ring your pick, and red rings what it can reach.\n\n" +
            "[b]Players[/b] lists every seat in turn order with its territories, its armies on the board, and the " +
            "cards in its hand. Its status column marks your seat and the seat whose turn it is. Below it are your own cards and what " +
            "the next set of cards trades for.\n\n" +
            "[b]Last battle[/b] names the attack, the attacker and the defender, then shows the dice: the " +
            "attacker's in red, the defender's in white, with each side's losses beside them. An attack declared " +
            "\"until it resolves\" shows its totals only.\n\n" +
            "[b]Your decision[/b] opens with the current phase, the round, and the decision's number (the number " +
            "the record and the transcript use), then holds the controls. Pick on the map or from the " +
            "lists, then press the blue button. Each control sends one reply line, the same line an agent would " +
            "type. A reply the table cannot accept comes back with the reason; on the third try the legal moves " +
            "are listed by number.\n\n" +
            "[b]Record of play[/b] is every event of the game in order, in plain words. Tick [i]Protocol lines[/i] " +
            "to see the lines exactly as the seats receive them in their prompts.\n\n" +
            "[b]Seats[/b] shows each seat's latest prompt and its reply, as the host sees them.\n\n" +
            "The table shows only what the rules make visible: no odds, hints or suggested moves.";
        if (!hosting) return text;
        return text + "\n\n" +
            "[b]Host controls[/b] (the Host tab)\n" +
            "[b]Poke[/b] sends a seat's pending prompt again, unchanged: for an agent that seems to have missed it.\n" +
            "[b]Replace with stand-in[/b] hands the seat to the built-in player, starting with its pending decision.\n" +
            "[b]Release to an agent[/b] reopens the seat for a new connection, which claims it with JOIN and the " +
            "seat's colour code.\n" +
            "[b]Pause[/b] and [b]Resume[/b] hold the game between decisions and release it.\n" +
            "[b]Suspend[/b] closes the game at the next decision boundary and saves it; continue it later from the " +
            "front door's Resume a game tab.\n" +
            "[b]End the game now[/b] ends it with no winner recorded.\n" +
            "[b]Edit position[/b] works only while the game is paused, once per pause: it changes a territory's " +
            "owner or armies, writes the change into the record, and marks the game host-edited for good.\n\n" +
            "If a seat cannot answer after three tries, or its connection fails, the game pauses and the Host tab " +
            "lists the legal moves: answer for the seat once, replace it, or end the game. The host never makes a " +
            "move for a seat on its own. A seat slower than the stall warning is flagged STALLED; nothing happens " +
            "to it unless you act.";
    }
}
