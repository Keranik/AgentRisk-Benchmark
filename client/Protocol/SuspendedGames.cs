using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentRisk.Client.Protocol;

/// <summary>One suspended game found in a records folder, as the resume list shows it.</summary>
public sealed record SuspendedGame(
    string RecordPath,
    string TranscriptPath,
    string Mode,
    long Seed,
    string Setup,
    IReadOnlyList<string> SeatCodes,
    IReadOnlyList<string> SeatKinds,
    int DecisionsSoFar,
    string? StartedUtc);

/// <summary>
/// Finds the resumable games in a records folder: game records whose
/// closing section — the last one, for a record spanning several
/// suspensions — reads Suspended. A suspended record is only offered
/// when its transcript stands beside it; a resume replays the
/// transcript's recorded choices, and without it there is nothing exact
/// to resume from.
/// </summary>
public static class SuspendedGames
{
    /// <summary>The suffix a record's transcript carries beside the record itself.</summary>
    public const string TranscriptSuffix = ".transcript.log";

    public static IReadOnlyList<SuspendedGame> Scan(string recordsFolder)
    {
        var found = new List<SuspendedGame>();
        if (string.IsNullOrWhiteSpace(recordsFolder) || !Directory.Exists(recordsFolder))
            return found;

        foreach (string path in Directory.EnumerateFiles(recordsFolder, "game-*.log"))
        {
            if (path.EndsWith(TranscriptSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var game = TryRead(path);
            if (game is not null) found.Add(game);
        }
        return found
            .OrderByDescending(g => g.StartedUtc ?? "", StringComparer.Ordinal)
            .ToList();
    }

    internal static SuspendedGame? TryRead(string recordPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(recordPath);
        }
        catch (IOException)
        {
            return null; // a record being written this instant is not an offer yet
        }

        string mode = "", setup = "", started = "";
        long seed = 0;
        var seats = new List<string>();
        var kinds = new List<string>();
        bool suspended = false;
        bool inResult = false;
        int decisions = 0;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line == "--- RESULT ---")
            {
                // A later section overrules an earlier one: a game
                // suspended, resumed, and finished reads finished.
                inResult = true;
                suspended = false;
                decisions = 0;
                continue;
            }
            if (inResult)
            {
                if (line.StartsWith("status: ", StringComparison.Ordinal))
                    suspended = line == "status: Suspended";
                else if (line.StartsWith("decisions: ", StringComparison.Ordinal))
                    int.TryParse(line["decisions: ".Length..], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out decisions);
                continue;
            }
            if (line.StartsWith("mode: ", StringComparison.Ordinal)) mode = line["mode: ".Length..];
            else if (line.StartsWith("seed: ", StringComparison.Ordinal))
                long.TryParse(line["seed: ".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);
            else if (line.StartsWith("setup: ", StringComparison.Ordinal)) setup = line["setup: ".Length..];
            else if (line.StartsWith("seats: ", StringComparison.Ordinal))
            {
                seats.Clear();
                kinds.Clear();
                foreach (Match match in Regex.Matches(
                    line["seats: ".Length..], @"([A-Z]{3}) \(([a-z]+)\)"))
                {
                    seats.Add(match.Groups[1].Value);
                    kinds.Add(match.Groups[2].Value);
                }
            }
            else if (line.StartsWith("started-utc: ", StringComparison.Ordinal)) started = line["started-utc: ".Length..];
        }

        if (!suspended) return null;
        string transcriptPath = TranscriptPathFor(recordPath);
        if (!File.Exists(transcriptPath)) return null;
        return new SuspendedGame(
            recordPath, transcriptPath, mode, seed, setup, seats, kinds, decisions,
            started.Length > 0 ? started : null);
    }

    /// <summary>The transcript path standing beside a record path.</summary>
    public static string TranscriptPathFor(string recordPath) =>
        recordPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            ? recordPath[..^4] + TranscriptSuffix
            : recordPath + TranscriptSuffix;
}
