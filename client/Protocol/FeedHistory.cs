using System;
using System.Collections.Generic;

namespace AgentRisk.Client.Protocol;

/// <summary>
/// The scrolling history of a game's event lines, built from the spectator
/// feed. Every seat's prompt carries a summary of everything since that
/// seat's last decision, so the same events arrive many times in
/// overlapping windows; this folds each window against what is already
/// shown (the shown lines are a prefix of the game's one event stream, and
/// each window is a contiguous slice of it) so every event appears exactly
/// once, in order. Battles fold through the same exactly-once stream, so
/// the latest battle is never doubled either.
///
/// In a joined game whose watch connection failed, the local seat's own
/// summaries feed the same fold through <see cref="FoldLocalSummary"/>.
/// </summary>
public sealed class FeedHistory
{
    private readonly List<string> _lines = new();
    private readonly BattleTracker _battles = new();
    private bool _battlesStarted;
    private bool _resultShown;
    private bool _gameOverShown;

    /// <summary>Every history line so far, oldest first.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>The most recent battle the summaries reported, with its dice.</summary>
    public Battle? LatestBattle => _battles.LastBattle;

    /// <summary>How many battles have been folded into the history.</summary>
    public int BattleCount { get; private set; }

    /// <summary>Folds one feed entry into the history.</summary>
    public void Consume(FeedEntry entry)
    {
        switch (entry.Kind)
        {
            case FeedEntryKind.Board:
                // Anchor the battle tracker's ownership to the table's
                // standing until the first battle folds; captures carry it
                // from there, exactly as the tracker is designed to walk.
                if (!_battlesStarted && entry.Board is not null)
                    _battles.SeedOwnership(entry.Board);
                break;
            case FeedEntryKind.Prompt:
                if (entry.Prompt is null) break;
                if (!_battlesStarted && entry.Prompt.Board is not null)
                    _battles.SeedOwnership(entry.Prompt.Board);
                FoldSummaryLines(entry.Prompt.SummaryLines);
                break;
            case FeedEntryKind.HostEvent:
                _lines.Add(DisplayHostEvent(entry));
                break;
            case FeedEntryKind.Result:
                if (_resultShown) break;
                _resultShown = true;
                foreach (string line in SplitLines(entry.Payload))
                    _lines.Add(line);
                break;
            case FeedEntryKind.GameOver:
                if (_gameOverShown) break;
                _gameOverShown = true;
                foreach (string line in SplitLines(entry.Payload))
                    if (line.Length > 0) _lines.Add(line);
                break;
        }
    }

    /// <summary>
    /// Folds the local seat's own prompt summary (the no-watch fallback),
    /// anchored on the board the previous local prompt showed.
    /// </summary>
    public void FoldLocalSummary(IReadOnlyList<string> summaryLines, BoardState? boardBefore)
    {
        if (!_battlesStarted && boardBefore is not null)
            _battles.SeedOwnership(boardBefore);
        FoldSummaryLines(summaryLines);
    }

    private void FoldSummaryLines(IReadOnlyList<string> summaryLines)
    {
        int overlap = LargestOverlap(_lines, summaryLines);
        for (int i = overlap; i < summaryLines.Count; i++)
            _lines.Add(summaryLines[i]);

        if (overlap < summaryLines.Count)
        {
            var fresh = new List<string>();
            for (int i = overlap; i < summaryLines.Count; i++)
                fresh.Add(summaryLines[i]);
            var touched = _battles.FoldSummary(fresh);
            if (touched.Count > 0)
            {
                _battlesStarted = true;
                BattleCount += touched.Count;
            }
        }
    }

    /// <summary>
    /// The largest k such that the last k shown lines equal the first k
    /// incoming lines: the overlap of this window with what already stands.
    /// </summary>
    internal static int LargestOverlap(IReadOnlyList<string> shown, IReadOnlyList<string> incoming)
    {
        int max = Math.Min(shown.Count, incoming.Count);
        for (int k = max; k > 0; k--)
        {
            bool matches = true;
            for (int i = 0; i < k; i++)
            {
                if (!string.Equals(shown[shown.Count - k + i], incoming[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return k;
        }
        return 0;
    }

    private static string DisplayHostEvent(FeedEntry entry)
    {
        string headline = entry.Marker.Length > 4 ? entry.Marker[4..] : entry.Marker;
        string payload = entry.Payload.Trim();
        return payload.Length == 0 ? headline : $"{headline}: {FirstLine(payload)}";
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).TrimEnd('\r');
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            yield return line.TrimEnd('\r');
    }
}
