namespace AgentRisk.Host;

/// <summary>
/// A seat reopened over a socket (a host replace to the socket kind),
/// waiting for its new client to claim it. The claim runs on its own
/// task through the connector's accept pump — the same path a
/// replacement claim has always taken — and every channel operation
/// waits for the claim to land, then forwards to the claimed channel.
/// From the session loop's side this is simply a seat whose briefing
/// and first prompt take as long as the new player takes to connect:
/// the table waits on the seat, exactly as it waits on any slow seat.
///
/// The session builds one of these when a host sets a seat's kind to
/// socket mid-game; hosts never construct it directly. Disposing the
/// channel disposes the claimed channel inside it, so a session that
/// ends closes the seat's socket like any other channel's.
/// </summary>
internal sealed class PendingJoinChannel : IAgentChannel, IDisposable
{
    private readonly TaskCompletionSource<IAgentChannel> _joined =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingJoinChannel(string seatCode, SocketSeatConnector connector)
    {
        DisplayName = $"socket seat {seatCode} (awaiting join)";
        _ = Task.Run(() =>
        {
            try
            {
                var (channel, _) = connector.AcceptSeat(seatCode);
                _joined.TrySetResult(channel);
            }
            catch (Exception ex)
            {
                _joined.TrySetException(ex);
            }
        });
    }

    public string DisplayName { get; }

    /// <summary>True once a client has claimed the seat.</summary>
    public bool HasJoined => _joined.Task.IsCompletedSuccessfully;

    /// <summary>The claimed channel, or null while the seat is unclaimed.</summary>
    public IAgentChannel? InnerOrNull =>
        _joined.Task.IsCompletedSuccessfully ? _joined.Task.Result : null;

    public void Send(string text) => AwaitInner().Send(text);

    public string? Ask(string promptText) => AwaitInner().Ask(promptText);

    public void Resend(string promptText) => AwaitInner().Resend(promptText);

    public void Dispose()
    {
        if (_joined.Task.IsCompletedSuccessfully)
            (_joined.Task.Result as IDisposable)?.Dispose();
    }

    private IAgentChannel AwaitInner() => _joined.Task.GetAwaiter().GetResult();
}
