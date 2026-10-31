using System;
using Godot;

namespace AgentRisk.Client.App;

/// <summary>
/// An army count, set the quick way: Min | -5 | [entry] | +5 | Max,
/// centred under its caption. The entry takes typing and its own one-step
/// arrows; the side buttons jump to the ends or move five at a time, so
/// "all of them" or "a few more" is one click rather than many. The
/// range is the decision's legal range and nothing else: the picker never
/// suggests a value.
/// </summary>
public sealed partial class CountPicker : VBoxContainer
{
    private const int Step = 5;

    private readonly SpinBox _entry;
    private readonly Button _min = Ui.Button("Min", UiTheme.SmallButton);
    private readonly Button _minus = Ui.Button("-5", UiTheme.SmallButton, "Five fewer.");
    private readonly Button _plus = Ui.Button("+5", UiTheme.SmallButton, "Five more.");
    private readonly Button _max = Ui.Button("Max", UiTheme.SmallButton);

    /// <summary>Raised with the new count whenever it changes, by any of the controls.</summary>
    public event Action<int>? ValueChanged;

    public CountPicker(string caption, int min, int max, int initial)
    {
        AddThemeConstantOverride("separation", 4);
        AddChild(new Label
        {
            Text = caption.ToUpperInvariant(),
            ThemeTypeVariation = UiTheme.CaptionLabel,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 6);
        AddChild(row);

        _entry = new SpinBox
        {
            MinValue = min,
            MaxValue = Math.Max(min, max),
            Value = Math.Clamp(initial, min, Math.Max(min, max)),
            Step = 1,
            Rounded = true,
            Alignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(96, 0),
        };
        _entry.GetLineEdit().SelectAllOnFocus = true;
        _entry.ValueChanged += _ =>
        {
            RefreshButtons();
            ValueChanged?.Invoke(Value);
        };

        _min.Pressed += () => _entry.Value = _entry.MinValue;
        _minus.Pressed += () => _entry.Value = Math.Max(_entry.MinValue, _entry.Value - Step);
        _plus.Pressed += () => _entry.Value = Math.Min(_entry.MaxValue, _entry.Value + Step);
        _max.Pressed += () => _entry.Value = _entry.MaxValue;

        row.AddChild(_min);
        row.AddChild(_minus);
        row.AddChild(_entry);
        row.AddChild(_plus);
        row.AddChild(_max);
        RefreshButtons();
    }

    /// <summary>The count as it stands.</summary>
    public int Value
    {
        get => (int)_entry.Value;
        set => _entry.Value = value;
    }

    /// <summary>Moves the top of the range (a fortify's source decides it); the count is kept inside it.</summary>
    public void SetMaximum(int max)
    {
        _entry.MaxValue = Math.Max(_entry.MinValue, max);
        if (_entry.Value > _entry.MaxValue) _entry.Value = _entry.MaxValue;
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool atMin = _entry.Value <= _entry.MinValue;
        bool atMax = _entry.Value >= _entry.MaxValue;
        _min.Disabled = atMin;
        _minus.Disabled = atMin;
        _plus.Disabled = atMax;
        _max.Disabled = atMax;
        _min.TooltipText = $"The least: {(int)_entry.MinValue}.";
        _max.TooltipText = $"The most: {(int)_entry.MaxValue}.";
    }
}
