using Superb.Core;

namespace Superb.Desktop;

public partial class MainWindow
{
    private readonly Guid diagnosticInstanceId = Guid.NewGuid();
    private long workspaceDiagnosticSequence;
    private readonly Dictionary<string, Guid> diagnosticSceneTokens = new(StringComparer.Ordinal);
    private readonly WorkspaceDiagnosticLog? workspaceDiagnosticLog;
    private Guid workspaceTaskId = Guid.NewGuid();
    private WorkspaceSceneRef DiagnosticScene(string? id)
    {
        var key = id ?? "";
        if (!diagnosticSceneTokens.TryGetValue(key, out var token)) diagnosticSceneTokens[key] = token = Guid.NewGuid();
        return new(key switch { "" => WorkspaceSceneKind.Direct, "general" => WorkspaceSceneKind.General,
            "algorithm" => WorkspaceSceneKind.Algorithm, "coding" => WorkspaceSceneKind.Programming, _ => WorkspaceSceneKind.Custom }, token);
    }
    private void TraceWorkspace(WorkspaceEvent kind, WorkspaceReason reason = WorkspaceReason.None,
        Guid? operationId = null, InputSnapshot? snapshot = null, Guid? observationId = null,
        Guid? runId = null, WorkspaceSceneRef? previousScene = null, Guid? previousTaskId = null, string? errorCode = null)
    {
        if (workspaceDiagnosticLog is null) return;
        static WorkspaceQueueSummary Queue(WorkspaceDraft draft) => new(draft.Items.Select(i => i.Snapshot.Id).ToArray(),
            draft.Items.Sum(i => (long)(i.Snapshot.Content.Text?.Length ?? 0)),
            draft.Items.Sum(i => i.Snapshot.Content.Size), draft.ImageCount);
        var material = snapshot is null ? null : new WorkspaceMaterial(snapshot.Id, snapshot.Kind,
            snapshot.Content.Text?.Length ?? 0, snapshot.Content.Size, snapshot.Content.Images.Count());
        workspaceDiagnosticLog.Write(new(diagnosticInstanceId, ++workspaceDiagnosticSequence, DateTimeOffset.UtcNow,
            kind, reason, DiagnosticScene(workspaceSceneId), Queue(ordinaryWorkspace), workspaceTaskId,
            operationId, material, observationId, runId, previousScene, previousTaskId, errorCode));
    }
    private void RemoveQueuedMaterial(Guid id, WorkspaceReason reason)
    {
        var entry = workspace.Items.FirstOrDefault(i => i.Snapshot.Id == id);
        if (entry is null) return;
        workspace.Remove(id);
        TraceWorkspace(WorkspaceEvent.QueueRemoved, reason, snapshot: entry.Snapshot);
    }
    private void ClearQueuedMaterials(WorkspaceReason reason)
    {
        var id = Guid.NewGuid();
        TraceWorkspace(WorkspaceEvent.QueueClearing, reason, operationId: id);
        workspace.Clear();
        TraceWorkspace(WorkspaceEvent.QueueCleared, reason, operationId: id);
    }
}
