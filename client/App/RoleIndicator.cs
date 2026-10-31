using System.Collections.Generic;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// The three roles this app can take at a table, side by side, with the
/// one it holds lit and the others dimmed: Host, Client, Watcher. It is a
/// readout, not a control (the role is chosen at the front door), but
/// showing all three says why the header names a role at all: the same
/// app can run a table, play a seat at someone else's, or watch one.
/// </summary>
public sealed partial class RoleIndicator : PanelContainer
{
    private readonly Dictionary<TableDoor, (PanelContainer Segment, Label Text)> _segments = new();
    private string _tableAddress = "";

    public RoleIndicator()
    {
        ThemeTypeVariation = UiTheme.SegmentGroup;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Pass;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        AddChild(row);
        foreach (var (door, word) in new[] { (TableDoor.Host, "Host"), (TableDoor.Join, "Client"), (TableDoor.Watch, "Watcher") })
        {
            var segment = new PanelContainer { ThemeTypeVariation = UiTheme.SegmentOff, MouseFilter = MouseFilterEnum.Pass };
            var text = new Label { Text = word, ThemeTypeVariation = UiTheme.SegmentOffLabel, MouseFilter = MouseFilterEnum.Pass };
            segment.AddChild(text);
            row.AddChild(segment);
            _segments[door] = (segment, text);
        }
    }

    /// <summary>Lights the role this app holds; the table address, where known, goes into the explanations.</summary>
    public void SetRole(TableDoor door, string tableAddress = "")
    {
        _tableAddress = tableAddress;
        foreach (var (each, (segment, text)) in _segments)
        {
            bool on = each == door;
            segment.ThemeTypeVariation = on ? UiTheme.SegmentOn : UiTheme.SegmentOff;
            text.ThemeTypeVariation = on ? UiTheme.SegmentOnLabel : UiTheme.SegmentOffLabel;
            segment.TooltipText = Explain(each, on);
        }
        TooltipText = "This app's role at the table. It can host a table, join one as a client to play a seat, " +
                      "or watch one; the lit role is the one it holds now. The role is chosen at the front door.";
    }

    private string Explain(TableDoor door, bool held)
    {
        string at = _tableAddress.Length > 0 ? $" at {_tableAddress}" : "";
        string what = door switch
        {
            TableDoor.Host => "Host: this app runs the table. It deals, rolls the dice, prompts every seat, and " +
                              "writes the record and the transcript.",
            TableDoor.Join => $"Client: this app joined a table hosted elsewhere{(held ? at : "")} to play one seat.",
            _ => $"Watcher: this app watches a table hosted elsewhere{(held ? at : "")} and holds no seat.",
        };
        return held ? what : what + " (Not this app's role at this table.)";
    }
}
