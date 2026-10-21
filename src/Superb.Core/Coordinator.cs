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
        State = RequestState.Cancelled;
    }
    public async Task RunAsync(PreparedRequest request, bool automatic = false, SyncGrant? grant = null)
    {
        if (Mode != RunState.Ready) throw new SafeFailure("session_not_ready");
        if (automatic && (grant is null || !grant.Allows(request.Snapshot.Kind, request.Backend.Id, DateTimeOffset.UtcNow, true)))
            throw new SafeFailure("sync_authorization_expired");
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
            Updated?.Invoke(new(attempt, state, output.ToString(), code));
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
