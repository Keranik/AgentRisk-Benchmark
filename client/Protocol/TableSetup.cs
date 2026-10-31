using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace AgentRisk.Client.Protocol;

/// <summary>How one seat at a hosted table will be filled.</summary>
public enum SeatPlanKind
{
    /// <summary>Played by the human at this app, on this screen.</summary>
    ThisScreen,
    /// <summary>A human joining from another copy of the app.</summary>
    Client,
    /// <summary>An agent connecting over a socket, playing in text.</summary>
    Agent,
    /// <summary>The host's built-in stand-in script.</summary>
    StandIn,
}

/// <summary>Which game the table plays.</summary>
public enum TableMode
{
    Standard,
    Duo,
}

/// <summary>How setup territories are assigned (the one special rule).</summary>
public enum SetupTerritories
{
    ManualClaims,
    RandomDeal,
}

/// <summary>One seat of a table being set up: its color, how it will be filled, and an optional display name.</summary>
public sealed class SeatPlan
{
    public SeatPlan(string color, SeatPlanKind kind)
    {
        Color = color;
        Kind = kind;
    }

    /// <summary>The seat's color code (RED, BLU, ...). The protocol and the records know the seat by this alone.</summary>
    public string Color { get; set; }

    public SeatPlanKind Kind { get; set; }

    /// <summary>Optional display name, presentation only: panels and instruction blocks. Never sent to the table.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Everything the host door asks, as one editable plan, plus the rules
/// that keep a plan valid while it is edited: colors are unique by
/// construction (choosing a color another seat holds swaps them), at most
/// one seat is played on this screen (choosing it again moves it), and
/// the seat count follows the mode. <see cref="Validate"/> is the final
/// check before a table starts.
/// </summary>
public sealed class TableSetup
{
    /// <summary>The six seat colors, in table order.</summary>
    public static readonly string[] SeatColors = { "RED", "BLU", "GRN", "YEL", "PNK", "BLK" };

    public const int MinSeats = 2;
    public const int MaxSeats = 6;

    /// <summary>
    /// The fewest seats a Standard game can start with. Two players
    /// play Duo instead (the engine seats Standard at three to six),
    /// so a two-seat Standard plan is editable but cannot start:
    /// <see cref="Validate"/> says so in plain words.
    /// </summary>
    public const int StandardMinSeats = 3;
    public const int DuoSeatCount = 2;
    public const int DefaultPort = 4000;
    public const int DefaultStallWarningSeconds = 120;

    public TableMode Mode { get; private set; } = TableMode.Standard;

    public List<SeatPlan> Seats { get; } = new();

    public string Address { get; set; } = "0.0.0.0";

    /// <summary>
    /// The address players use to reach this table, printed in the
    /// instruction blocks and the client helper line. Not the listen
    /// address: that one binds this machine (0.0.0.0 hears on every
    /// interface), while a player on another machine who dialed it
    /// would reach their own machine instead. Defaults to this
    /// machine's LAN address, or 127.0.0.1 when it has none.
    /// </summary>
    public string ConnectAddress { get; set; } = LanAddress.DefaultConnectAddress();

    public int Port { get; set; } = DefaultPort;

    /// <summary>The game seed, or null to have the host pick one when the table starts.</summary>
    public long? Seed { get; set; }

    public SetupTerritories Territories { get; set; } = SetupTerritories.ManualClaims;

    public string RecordsFolder { get; set; } = "";
    public int StallWarningSeconds { get; set; } = DefaultStallWarningSeconds;

    /// <summary>A fresh plan: three seats, the first played on this screen.</summary>
    public static TableSetup Fresh()
    {
        var setup = new TableSetup();
        setup.Seats.Add(new SeatPlan(SeatColors[0], SeatPlanKind.ThisScreen));
        setup.Seats.Add(new SeatPlan(SeatColors[1], SeatPlanKind.StandIn));
        setup.Seats.Add(new SeatPlan(SeatColors[2], SeatPlanKind.StandIn));
        return setup;
    }

    /// <summary>Switches the mode, trimming or keeping the seat list as the mode demands.</summary>
    public void SetMode(TableMode mode)
    {
        Mode = mode;
        if (mode == TableMode.Duo)
            while (Seats.Count > DuoSeatCount) Seats.RemoveAt(Seats.Count - 1);
        while (Seats.Count < MinSeats) AddSeat();
    }

    /// <summary>Adds a seat with the first unused color. Returns false when the mode's count is full.</summary>
    public bool AddSeat()
    {
        int cap = Mode == TableMode.Duo ? DuoSeatCount : MaxSeats;
        if (Seats.Count >= cap) return false;
        string color = SeatColors.First(c => Seats.All(s => s.Color != c));
        Seats.Add(new SeatPlan(color, SeatPlanKind.StandIn));
        return true;
    }

    /// <summary>Removes a seat. Returns false when the mode's minimum already stands.</summary>
    public bool RemoveSeat(int index)
    {
        if (Seats.Count <= MinSeats || index < 0 || index >= Seats.Count) return false;
        Seats.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Gives a seat a color. If another seat holds it, the two swap, so a
    /// duplicate can never exist at any point in the editing.
    /// </summary>
    public void SetSeatColor(int index, string color)
    {
        var seat = Seats[index];
        var holder = Seats.FirstOrDefault(s => s.Color == color);
        if (holder is not null && !ReferenceEquals(holder, seat))
            holder.Color = seat.Color;
        seat.Color = color;
    }

    /// <summary>
    /// Sets how a seat will be filled. Making a seat the one played on
    /// this screen moves any other this-screen seat to a stand-in, so at
    /// most one seat ever holds it.
    /// </summary>
    public void SetSeatKind(int index, SeatPlanKind kind)
    {
        if (kind == SeatPlanKind.ThisScreen)
            foreach (var seat in Seats)
                if (seat.Kind == SeatPlanKind.ThisScreen && !ReferenceEquals(seat, Seats[index]))
                    seat.Kind = SeatPlanKind.StandIn;
        Seats[index].Kind = kind;
    }

    /// <summary>The seat played on this screen, if the plan has one.</summary>
    public SeatPlan? ThisScreenSeat => Seats.FirstOrDefault(s => s.Kind == SeatPlanKind.ThisScreen);

    /// <summary>
    /// The final check before a table starts: a list of plain problems,
    /// empty when the plan can start. The editing rules already make most
    /// of these unreachable; this stands guard over values typed freely
    /// (address, port, records folder) and over plans built in code.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        int expectedMin = Mode == TableMode.Duo ? DuoSeatCount : StandardMinSeats;
        int expectedMax = Mode == TableMode.Duo ? DuoSeatCount : MaxSeats;
        if (Seats.Count < expectedMin || Seats.Count > expectedMax)
            problems.Add(Mode == TableMode.Duo
                ? "A Duo game has exactly two seats."
                : "A Standard game has three to six seats. Two players play a Duo game.");
        if (Seats.Any(s => !SeatColors.Contains(s.Color)))
            problems.Add("Every seat needs one of the six seat colors.");
        if (Seats.GroupBy(s => s.Color).Any(g => g.Count() > 1))
            problems.Add("Two seats cannot share a color.");
        if (Seats.Count(s => s.Kind == SeatPlanKind.ThisScreen) > 1)
            problems.Add("Only one seat can be played on this screen.");
        if (!IPAddress.TryParse(Address, out _))
            problems.Add("The listen address must be an IP address, like 0.0.0.0.");
        if (string.IsNullOrWhiteSpace(ConnectAddress))
            problems.Add("Enter the address players connect to.");
        if (Port < 1 || Port > 65535)
            problems.Add("The port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(RecordsFolder))
            problems.Add("Choose a folder for the game records.");
        if (StallWarningSeconds < 1)
            problems.Add("The stall warning must be at least one second.");
        return problems;
    }
}
