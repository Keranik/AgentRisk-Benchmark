using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using AgentRisk.Host;

namespace AgentRisk.Client.App;

/// <summary>
/// The channel for the seat played on this screen when this app hosts.
/// The session asks on its own thread; the question is queued here and the
/// table screen picks it up on the Godot main thread, presents it through
/// the same decision surface a joined game uses, and completes it with the
/// human's reply line. Briefings and the game-over block arrive through
/// <see cref="Send"/> and are queued as notices for the table to show.
/// </summary>
public sealed class UiSeatChannel : IAgentChannel
{
    /// <summary>One question from the session, waiting for the human's reply line.</summary>
    public sealed class PromptRequest
    {
        internal PromptRequest(string text, TaskCompletionSource<string?> completion, bool isResend)
        {
            Text = text;
            Completion = completion;
            IsResend = isResend;
        }

        /// <summary>The prompt block, exactly as the host sent it.</summary>
        public string Text { get; }

        /// <summary>True when this is a host poke re-sending the pending prompt.</summary>
        public bool IsResend { get; }

        internal TaskCompletionSource<string?> Completion { get; }

        /// <summary>Gives the human's reply line (null closes the seat without one).</summary>
        public void Answer(string? replyLine) => Completion.TrySetResult(replyLine);
    }

    private readonly ConcurrentQueue<PromptRequest> _requests = new();
    private readonly ConcurrentQueue<string> _notices = new();
    private PromptRequest? _current;
    private volatile bool _closed;

    public UiSeatChannel(string seatCode)
    {
        SeatCode = seatCode;
        DisplayName = $"this screen ({seatCode})";
    }

    public string SeatCode { get; }

    public string DisplayName { get; }

    /// <summary>True while a question waits for the human.</summary>
    public bool HasPendingRequest => _current is not null && !_current.Completion.Task.IsCompleted;

    public void Send(string text) => _notices.Enqueue(text);

    public string? Ask(string promptText)
    {
        if (_closed) return null;
        var request = new PromptRequest(
            promptText,
            new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
            isResend: false);
        _current = request;
        _requests.Enqueue(request);
        return request.Completion.Task.GetAwaiter().GetResult();
    }

    public void Resend(string promptText)
    {
        // A poke re-presents the pending question; it never opens a second
        // one. If the question was already answered, there is nothing to
        // re-send: the session re-asks on its own when it needs to.
        var current = _current;
        if (_closed || current is null || current.Completion.Task.IsCompleted) return;
        _requests.Enqueue(new PromptRequest(promptText, current.Completion, isResend: true));
    }

    /// <summary>Takes the next question for the table screen to present, if one waits.</summary>
    public bool TryTakeRequest(out PromptRequest request) => _requests.TryDequeue(out request!);

    /// <summary>Takes the next briefing or game-over notice, if one waits.</summary>
    public bool TryTakeNotice(out string notice) => _notices.TryDequeue(out notice!);

    /// <summary>Closes the seat: a question still waiting ends without a reply.</summary>
    public void Close()
    {
        _closed = true;
        _current?.Answer(null);
    }
}
