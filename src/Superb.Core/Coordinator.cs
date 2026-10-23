using System.Text;

namespace Superb.Core;

public interface IBackendClient
{
    IAsyncEnumerable<BackendEvent> SendAsync(PreparedRequest request, CancellationToken cancellationToken);
}

// Public methods must run on one event loop (the desktop Dispatcher).
// UI subscribers must check IsCurrent again when marshaling queued updates.
public sealed class Coordinator(IBackendClient client)
{
    private long generation;
    private CancellationTokenSource? currentCancellation;
    public RunState Mode { get; private set; } = RunState.Paused;
    public Attempt? Current { get; private set; }
    public RequestState State { get; private set; } = RequestState.Idle;
    public RequestUpdate? Latest { get; private set; }
    public event Action<RequestUpdate>? Updated;
    public bool IsCurrent(Attempt attempt) => Current?.Id == attempt.Id && attempt.Generation == generation && Mode == RunState.Ready;

    public void SetMode(RunState mode)
    {
        Invalidate();
        Mode = mode;
    }
    public void Invalidate()
    {
        generation++;
        currentCancellation?.Cancel();
        currentCancellation = null;
        Current = null;
        Latest = null;
        State = RequestState.Cancelled;
    }
    // Keep a frozen, explicitly incomplete answer while invalidating every queued transport event.
    public void Stop()
    {
        if (Current is null || State is RequestState.Completed or RequestState.Failed or RequestState.Cancelled) return;
        var prior = Latest;
        var attempt = Current;
        Invalidate();
        Current = attempt with { Generation = generation };
        State = RequestState.Cancelled;
        Latest = new(Current, State, prior?.Text ?? "");
        Updated?.Invoke(Latest);
    }
    public async Task RunAsync(PreparedRequest request)
    {
        if (Mode != RunState.Ready) throw new SafeFailure("session_not_ready");
        Invalidate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(request.Backend.TimeoutSeconds));
        currentCancellation = cts;
        var attempt = new Attempt(Guid.NewGuid(), generation, request);
        Current = attempt;
        var output = new StringBuilder();
        void Emit(RequestState state, string? code = null)
        {
            if (!IsCurrent(attempt)) return;
            State = state;
            Latest = new(attempt, state, output.ToString(), code);
            Updated?.Invoke(Latest);
        }
        try
        {
            Emit(RequestState.PolicyCheck);
            Policy.Check(request);
            cts.Token.ThrowIfCancellationRequested();
            Emit(RequestState.Connecting);
            var complete = false;
            await foreach (var item in client.SendAsync(request, cts.Token))
            {
                if (!IsCurrent(attempt)) return;
                cts.Token.ThrowIfCancellationRequested();
                if (item.Waiting) Emit(RequestState.Waiting);
                if (item.Thinking && output.Length == 0 && State != RequestState.Thinking) Emit(RequestState.Thinking);
                if (item.Delta is { } delta)
                {
                    if ((long)output.Length + delta.Length > request.Backend.MaxOutputChars) throw new SafeFailure("output_too_large");
                    output.Append(delta);
                    Emit(RequestState.Streaming);
                }
                if (item.Completed) { complete = true; break; }
            }
            if (!IsCurrent(attempt)) return;
            cts.Token.ThrowIfCancellationRequested();
            if (!complete) throw new SafeFailure("stream_incomplete");
            if (string.IsNullOrWhiteSpace(output.ToString())) throw new SafeFailure("response_empty");
            Emit(RequestState.Completed);
        }
        catch (OperationCanceledException) { Emit(RequestState.Failed, "request_timed_out"); }
        catch (SafeFailure ex) { Emit(RequestState.Failed, ex.Code); }
        catch (Exception ex) { Emit(RequestState.Failed, ConnectionDiagnostics.CodeFor(ex)); }
        finally
        {
            output.Clear();
            if (ReferenceEquals(currentCancellation, cts)) currentCancellation = null;
        }
    }
}
