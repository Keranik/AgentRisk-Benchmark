using System.Net;

namespace AgentRisk.Host;

/// <summary>
/// Everything a host fixes before it seats a game through
/// <see cref="GameSession"/>: the game itself (<see cref="Game"/>), where
/// its record and transcript are written, the listen endpoint the host
/// serves socket seats on, and the stall-warning interval seat
/// telemetry flags against.
///
/// The values are configuration; the live resources they describe (the
/// seat channels, the socket connector, the spectator feed) are handed
/// to the session separately, because different hosts build them
/// differently — the console host accepts socket seats before the
/// session exists, the app hosts them in-process. The listen endpoint
/// is recorded here so a session is a complete description of the table
/// it runs, and so the session can validate socket replacements against
/// the endpoint its connector is actually serving.
/// </summary>
/// <param name="Game">Mode, seats with kinds, seed, and territory setup.</param>
/// <param name="RecordDirectory">Directory the game record (and its transcript) is written into.</param>
/// <param name="ListenAddress">
/// Interface the host listens on for socket seats, when it listens at
/// all; null for a table with no socket listener.
/// </param>
/// <param name="ListenPort">Port the host listens on; 0 when there is no listener.</param>
/// <param name="StallWarningSeconds">
/// How long the table may wait on one seat before telemetry flags the
/// seat stalled (a flag for the host only; the session never acts on
/// it). Defaults to <see cref="HostConstants.DefaultStallWarningSeconds"/>.
/// </param>
public sealed record SessionConfig(
    GameConfig Game,
    string RecordDirectory,
    IPAddress? ListenAddress = null,
    int ListenPort = 0,
    int StallWarningSeconds = HostConstants.DefaultStallWarningSeconds);
