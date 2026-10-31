using System.Globalization;
using AgentRisk.Engine;

namespace AgentRisk.Host;

/// <summary>
/// The full game record: one text file per game carrying the
/// header (mode, seed, setup mode, seat configuration), every game-log event line, and
/// every dice roll with its faces, appended as the game produces them, so
/// the record on disk is complete up to the last resolved decision even if
/// the host dies mid-game. The format is the host's own audit format (it
/// is host-side, never sent to a seat): event lines are the engine's
/// verbatim; dice lines are prefixed DICE and carry their global index.
///
/// Host interventions are written into the same log section as
/// HOST lines: a host's touch is never invisible in the
/// record. They are not engine events, so they never enter a round
/// summary; they are the session's lines, written at the moment the
/// intervention takes effect. Writes take a lock: interventions are
/// recorded from whichever thread makes them take effect (a poke lands
/// from the host's thread), while the game appends from the loop's.
/// </summary>
public sealed class GameRecordWriter : IDisposable
{
    /// <summary>
    /// How much of a record's head the host-edited flag scan reads:
    /// the header is a handful of short lines, so a small bound always
    /// covers it, and a record too short to hold it is simply unflagged.
    /// </summary>
    private const int HeaderScanBytes = 8192;

    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private int _eventsWritten;
    private int _diceWritten;

    // The host-edited flag: the header carries a `host-edited:`
    // line from creation, its value written padded to a fixed width so
    // the first edit can patch it in place (see MarkHostEdited). The
    // offset is the flag value's byte position in the file: computed
    // from the header lines as Create writes them, and found by
    // scanning the header when a record is reopened for resume. -1
    // when the file has no flag line (a record written before the
    // flag existed).
    private long _hostEditedValueOffset = -1;
    private bool _hostEditedMarked;

    // Resume mode: the writer is opened over a suspended game's
    // existing record and appends nothing until CatchUp is called at the
    // replay boundary. The spans being replayed already stand in the
    // file; the replay re-derives them in the engine, and only what the
    // game does after it goes live may be appended.
    private bool _catchingUp;

    private GameRecordWriter(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    /// <summary>The record file's path.</summary>
    public string Path { get; }

    /// <summary>Creates the record file in <paramref name="directory"/> and writes its header.</summary>
    public static GameRecordWriter Create(string directory, GameConfig config, Func<string, SeatKind> seatKindOf)
    {
        Directory.CreateDirectory(directory);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = System.IO.Path.Combine(directory, $"game-{stamp}-seed{config.Seed}.log");
        var writer = new StreamWriter(path, append: false) { AutoFlush = true };

        var record = new GameRecordWriter(path, writer);
        // The flag value's byte offset is counted as the header is
        // written: every byte before it is on these lines (the writer
        // is UTF-8 without a preamble, and WriteLine appends the
        // writer's newline), so the count is exact.
        long offset = 0;
        void HeaderLine(string line)
        {
            writer.WriteLine(line);
            offset += System.Text.Encoding.UTF8.GetByteCount(line)
                      + System.Text.Encoding.UTF8.GetByteCount(writer.NewLine);
        }
        HeaderLine("AGENT RISK GAME RECORD v1");
        HeaderLine($"mode: {config.Mode}");
        HeaderLine($"seed: {config.Seed}");
        HeaderLine($"setup: {config.TerritorySetup}");
        HeaderLine($"seats: {string.Join(" ", config.Seats.Select(s => $"{s.Code} ({seatKindOf(s.Code).ToString().ToLowerInvariant()})"))}");
        // The host-edited flag: "no" padded with a trailing space to the
        // width of "yes", so the first edit patches the value in place
        // without moving a single byte after it (see MarkHostEdited).
        record._hostEditedValueOffset = offset + "host-edited: ".Length;
        HeaderLine("host-edited: no ");
        HeaderLine($"started-utc: {DateTime.UtcNow:O}");
        HeaderLine("--- GAME LOG (events and dice, chronological) ---");
        return record;
    }

    /// <summary>
    /// Reopens a suspended game's record for the resume: no
    /// header is written (the original stands), and nothing is appended
    /// until <see cref="CatchUp"/> marks the replay boundary. The file
    /// must exist; <see cref="ResumePlan.FromFiles"/> has already read
    /// and validated it before this is called. The stream is opened
    /// read/write and positioned at the end by hand rather than in
    /// append mode, so the host-edited flag can still be patched in
    /// place when the resumed game is edited (see MarkHostEdited).
    /// </summary>
    public static GameRecordWriter OpenForResume(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var (offset, isYes) = ScanHostEditedFlag(stream);
        stream.Seek(0, SeekOrigin.End);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        return new GameRecordWriter(path, writer)
        {
            _catchingUp = true,
            _hostEditedValueOffset = offset,
            _hostEditedMarked = isYes,
        };
    }

    /// <summary>
    /// Flags the record as host-edited, permanently: the
    /// header's `host-edited:` value is patched from "no " to "yes"
    /// in place, overwriting the padded value bytes and nothing else,
    /// so the rest of the file — including everything already appended
    /// after the header — stands byte for byte. Idempotent; a no-op
    /// while the writer is still catching up at a resume boundary (the
    /// session calls it again once the boundary is verified), and on a
    /// record old enough to have no flag line, where the HOST EDIT
    /// lines in the log are the flag's only form.
    /// </summary>
    public void MarkHostEdited()
    {
        lock (_gate)
        {
            if (_hostEditedMarked || _catchingUp) return;
            _hostEditedMarked = true;
            if (_hostEditedValueOffset < 0) return;
            _writer.Flush();
            var stream = _writer.BaseStream;
            long restore = stream.Position;
            stream.Position = _hostEditedValueOffset;
            stream.Write("yes"u8);
            stream.Position = restore;
            stream.Flush();
        }
    }

    /// <summary>
    /// Finds the header's `host-edited:` value in the first bytes of a
    /// record stream: its byte offset, and whether it already reads
    /// "yes". The header is plain ASCII, so character positions in the
    /// scanned text are byte positions in the file. The stream's
    /// position is restored before returning.
    /// </summary>
    private static (long Offset, bool IsYes) ScanHostEditedFlag(Stream stream)
    {
        long restore = stream.Position;
        try
        {
            stream.Position = 0;
            var buffer = new byte[(int)Math.Min(stream.Length, HeaderScanBytes)];
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk == 0) break;
                read += chunk;
            }
            string head = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            const string flag = "host-edited: ";
            int flagAt = head.IndexOf(flag, StringComparison.Ordinal);
            int logAt = head.IndexOf("--- GAME LOG", StringComparison.Ordinal);
            if (flagAt < 0 || (logAt >= 0 && flagAt > logAt)) return (-1, false);
            int valueAt = flagAt + flag.Length;
            bool isYes = head.AsSpan(valueAt).StartsWith("yes", StringComparison.Ordinal);
            return (valueAt, isYes);
        }
        finally
        {
            stream.Position = restore;
        }
    }

    /// <summary>
    /// Ends resume catch-up: the game's log and dice counts at the
    /// replay boundary become the append baseline, so exactly the
    /// events already standing in the file are skipped and everything
    /// the game produces from here is appended. The session calls this
    /// only after verifying the replay reproduced the record's counts
    /// exactly (see <see cref="ResumePlan"/>); appending on a diverged
    /// replay would silently lose lines.
    /// </summary>
    public void CatchUp(RiskGame game)
    {
        lock (_gate)
        {
            _eventsWritten = game.Log.Count;
            _diceWritten = game.DiceRolls.Count;
            _catchingUp = false;
        }
    }

    /// <summary>
    /// Appends whatever the game has produced since the last call: new
    /// event lines first, then the new dice records of the same span.
    /// </summary>
    public void AppendNew(RiskGame game)
    {
        lock (_gate) AppendNewLocked(game);
    }

    /// <summary>
    /// Appends one HOST line at the current position in the
    /// log section, flushing the game's own new events first so the
    /// record stays chronological: the intervention stands after
    /// everything the game had already done when it took effect.
    /// </summary>
    public void AppendHostLine(RiskGame game, string line)
    {
        lock (_gate)
        {
            if (_catchingUp) return; // a replayed intervention already stands in the file
            AppendNewLocked(game);
            _writer.WriteLine(line);
        }
    }

    private void AppendNewLocked(RiskGame game)
    {
        if (_catchingUp) return; // the replayed span already stands in the file
        var log = game.Log;
        for (int i = _eventsWritten; i < log.Count; i++)
            _writer.WriteLine(log[i]);
        _eventsWritten = log.Count;

        var rolls = game.DiceRolls;
        for (int i = _diceWritten; i < rolls.Count; i++)
        {
            var roll = rolls[i];
            _writer.WriteLine(
                $"DICE {roll.Index} {roll.Attacker} {roll.From}>{roll.To} vs {roll.Defender} " +
                $"{(roll.Mode == AttackMode.Round ? "RND" : "ALL")} " +
                $"A[{string.Join(" ", roll.AttackerFaces)}] D[{string.Join(" ", roll.DefenderFaces)}] " +
                $"A-{roll.AttackerLosses} D-{roll.DefenderLosses}");
        }
        _diceWritten = rolls.Count;
    }

    /// <summary>Writes the closing section: how the game ended, and when.</summary>
    public void Finish(SessionResult result)
    {
        lock (_gate)
        {
            if (_catchingUp)
                throw new InvalidOperationException(
                    "A record still catching up at a resume boundary cannot be finished.");
            _writer.WriteLine("--- RESULT ---");
            _writer.WriteLine($"status: {result.Status}");
            _writer.WriteLine(result.Winner is not null ? $"winner: {result.Winner}" : "winner: none");
            if (result.EndReason is not null) _writer.WriteLine($"end-reason: {result.EndReason}");
            _writer.WriteLine($"decisions: {result.Decisions}");
            _writer.WriteLine($"ended-utc: {DateTime.UtcNow:O}");
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
