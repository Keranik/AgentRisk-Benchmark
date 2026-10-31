using System;
using System.Collections.Generic;
using System.Linq;
using AgentRisk.Client.Protocol;
using AgentRisk.Engine;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// The guarded host edit panel: a modal over the table,
/// reachable only from the host's paused state, offering exactly the
/// closed set and nothing else — for every territory, an owner
/// dropdown of the game's legal owners and an army count. A live
/// summary line states every pending change in plain words before
/// anything is confirmed; confirming hands the changes to the table
/// screen, which applies them through the host library path
/// (<see cref="HostedTable.ApplyEditAsync"/>); a refusal from that
/// path is shown here, in the panel, in its own words. The form logic
/// (changes, validation, summary, gating) lives in
/// <see cref="HostEditForm"/> where the client suite pins it; this
/// class is its rendering and its wiring.
/// </summary>
public sealed partial class HostEditPanel : Control
{
    /// <summary>Raised when the host confirms: the changes, as engine edit entries.</summary>
    public event Action<IReadOnlyList<TerritoryEdit>>? Confirmed;

    /// <summary>Raised when the host cancels: nothing was changed.</summary>
    public event Action? Cancelled;

    private readonly VBoxContainer _rows = new();
    private readonly Label _summary = new() { AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.Word };
    private readonly Button _confirmButton = new() { Text = "Apply the edit", ThemeTypeVariation = UiTheme.PrimaryButton };

    private IReadOnlyList<HostEditPosition> _position = Array.Empty<HostEditPosition>();
    private IReadOnlyList<string> _ownerOptions = Array.Empty<string>();
    private readonly Dictionary<string, OptionButton> _ownerPicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpinBox> _armyPicks = new(StringComparer.Ordinal);

    public HostEditPanel()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        var dim = new ColorRect { Color = UiConstants.EditDimColor };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var card = new PanelContainer
        {
            ThemeTypeVariation = UiTheme.Card,
            CustomMinimumSize = UiConstants.EditPanelMinSize,
        };
        center.AddChild(card);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        card.AddChild(margin);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        margin.AddChild(body);

        var title = new Label { Text = "Edit the position" };
        title.AddThemeFontSizeOverride("font_size", 20);
        body.AddChild(title);
        body.AddChild(new Label
        {
            Text = "The game is paused. Here you can change who owns a territory and how many " +
                   "armies stand on it. Nothing else can be edited: not cards, the deck, the dice, " +
                   "or the turn order. When you confirm, the edit is written into the game record, " +
                   "every seat is told, and the game is marked host-edited for the rest of its life. " +
                   "One edit per pause: to edit again, resume the game and pause it once more.",
            AutowrapMode = TextServer.AutowrapMode.Word,
        });

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _rows.AddThemeConstantOverride("separation", 4);
        _rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_rows);
        body.AddChild(scroll);

        body.AddChild(_summary);
        body.AddChild(_status);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var cancel = new Button { Text = "Cancel" };
        cancel.Pressed += () => { Visible = false; Cancelled?.Invoke(); };
        buttons.AddChild(cancel);
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddChild(spacer);
        _confirmButton.Pressed += OnConfirm;
        buttons.AddChild(_confirmButton);
        body.AddChild(buttons);
    }

    /// <summary>
    /// Opens the panel over the position as it stands: one row per
    /// territory, the owner dropdown holding the game's legal owners
    /// (plus an unclaimed entry for land nobody holds), the armies
    /// stepper at the count on the board.
    /// </summary>
    public void Open(IReadOnlyList<HostEditPosition> position, IReadOnlyList<string> ownerOptions)
    {
        _position = position;
        _ownerOptions = ownerOptions;
        _ownerPicks.Clear();
        _armyPicks.Clear();
        foreach (Node child in _rows.GetChildren()) child.QueueFree();

        foreach (var row in position)
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 8);
            line.AddChild(new Label
            {
                Text = row.TerritoryName,
                CustomMinimumSize = new Vector2(UiConstants.EditNameColumnWidth, 0),
            });

            var owner = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            if (row.Owner is null)
                owner.AddItem("Unclaimed");
            foreach (string code in ownerOptions)
                owner.AddIconItem(Ui.SwatchIcon(code), UiConstants.SeatDisplayName(code));
            owner.Selected = SelectedIndexFor(row);
            owner.ItemSelected += _ => RefreshSummary();
            line.AddChild(owner);
            _ownerPicks[row.Territory] = owner;

            var armies = new SpinBox
            {
                MinValue = 1,
                MaxValue = UiConstants.EditArmiesMaximum,
                Step = 1,
                Value = Math.Max(1, row.Armies),
                CustomMinimumSize = new Vector2(110, 0),
            };
            armies.ValueChanged += _ => RefreshSummary();
            line.AddChild(armies);
            _armyPicks[row.Territory] = armies;

            _rows.AddChild(line);
        }

        _status.Text = "";
        RefreshSummary();
        Visible = true;
    }

    /// <summary>Closes the panel (an applied edit, or the table moving on).</summary>
    public void Close() => Visible = false;

    /// <summary>
    /// Shows a refusal from the session, in its own words: the panel
    /// stays open and the position unchanged, so the host can adjust
    /// or cancel.
    /// </summary>
    public void ShowRefusal(string reason) => _status.Text = reason;

    private int SelectedIndexFor(HostEditPosition row)
    {
        // The dropdown's entries: an unclaimed row leads with its
        // "Unclaimed" entry; every row then lists the legal owners.
        int offset = row.Owner is null ? 1 : 0;
        if (row.Owner is null) return 0;
        int index = _ownerOptions.ToList().IndexOf(row.Owner);
        return index < 0 ? 0 : index + offset;
    }

    private string? OwnerCodeFor(HostEditPosition row)
    {
        var pick = _ownerPicks[row.Territory];
        int offset = row.Owner is null ? 1 : 0;
        int selected = (int)pick.Selected - offset;
        return selected < 0 ? null : _ownerOptions[selected];
    }

    private void RefreshSummary()
    {
        var chosen = Chosen();
        var changes = HostEditForm.BuildChanges(_position, chosen);
        string? refusal = HostEditForm.Validate(_position, chosen, _ownerOptions);
        _summary.Text = HostEditForm.Summary(
            _position, changes, _ownerOptions, DisplayName, code => NameOf(code));
        _status.Text = refusal ?? "";
        _confirmButton.Disabled = changes.Count == 0 || refusal is not null;
    }

    private Dictionary<string, (string? Owner, int Armies)> Chosen()
    {
        var chosen = new Dictionary<string, (string? Owner, int Armies)>(StringComparer.Ordinal);
        foreach (var row in _position)
            chosen[row.Territory] = (OwnerCodeFor(row), (int)_armyPicks[row.Territory].Value);
        return chosen;
    }

    private void OnConfirm()
    {
        var changes = HostEditForm.BuildChanges(_position, Chosen());
        var edits = changes
            .Select(change => new TerritoryEdit(change.Territory, change.NewOwner, change.NewArmies))
            .ToList();
        if (edits.Count > 0) Confirmed?.Invoke(edits);
    }

    private static string DisplayName(string? code) =>
        code is null ? "no one" : UiConstants.SeatDisplayName(code);

    private string NameOf(string code) =>
        _position.FirstOrDefault(p => p.Territory == code)?.TerritoryName ?? code;
}
