namespace AgentRisk.Client.Protocol;

/// <summary>
/// Where a hosted game stands, in the terms the Host area's game
/// controls care about. The table screen maps its session state onto
/// these; nothing here knows the session's own vocabulary.
/// </summary>
public enum HostGamePhase
{
    /// <summary>No session yet: seats are still joining.</summary>
    Filling,

    /// <summary>Play is running.</summary>
    Running,

    /// <summary>The host has the game paused.</summary>
    HostPaused,

    /// <summary>The failure policy has the game paused: a seat could not answer.</summary>
    StrikePaused,

    /// <summary>The game is over — completed, ended by the host, or suspended and filed away.</summary>
    Over,
}

/// <summary>Which of the Host area's game controls stand live in a phase.</summary>
public readonly record struct HostGameControlStates(
    bool PauseEnabled, bool ResumeEnabled, bool SuspendEnabled, bool EndEnabled);

/// <summary>
/// The Host area's game controls (pause, resume, suspend,
/// end), kept in the protocol layer so the mapping is testable
/// headlessly: each control stands live exactly when the session
/// would honor it, and never once the game is over. The screen
/// renders what this returns; the session remains the authority
/// that refuses a stale call.
/// </summary>
public static class HostGameControls
{
    public static HostGameControlStates For(HostGamePhase phase) => phase switch
    {
        // Pause is the running game's control; resume is the
        // host-pause's. Suspend and end stay live in every live
        // phase: a game can be filed away or ended out of either
        // pause as well as out of play.
        HostGamePhase.Running => new HostGameControlStates(
            PauseEnabled: true, ResumeEnabled: false, SuspendEnabled: true, EndEnabled: true),
        HostGamePhase.HostPaused => new HostGameControlStates(
            PauseEnabled: false, ResumeEnabled: true, SuspendEnabled: true, EndEnabled: true),
        HostGamePhase.StrikePaused => new HostGameControlStates(
            PauseEnabled: false, ResumeEnabled: false, SuspendEnabled: true, EndEnabled: true),
        // Filling and Over: nothing to control yet, or nothing left
        // to control.
        _ => new HostGameControlStates(
            PauseEnabled: false, ResumeEnabled: false, SuspendEnabled: false, EndEnabled: false),
    };
}
