using Superb.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Superb.Desktop;

internal enum WorkspaceEvent
{
    ApplicationStarted, SceneChanged, AcquisitionStarted, AcquisitionSkipped, AcquisitionRead,
    AcquisitionFailed, AcquisitionCancelled, MaterialQueued, QueueRemoved, QueueClearing,
    QueueCleared, QueueReplacing, MaterialSubmitted, AnalysisStarted,
    TaskClearing, TaskCleared
}
internal enum WorkspaceReason
{
    None, Clipboard, RegionScreenshot, ScreenScreenshot, ScreenAndSend, OwnClipboardOutput,
    NotReady, Busy, QueueLimit, CapturePending, StaleOperation, UserCancelled, CaptureCancelled,
    ClickThroughUnavailable, RemoveItem, RemoveLast, RemoveAll, ReplaceAfterAnswer, ReplaceForScreenshot,
    Submitted, NewTask, EmergencyPause, SessionLocked, SessionReturned, SystemSuspend,
    DisplayChanged, ProtectionLost, InternalFailure, Exit
}
internal enum WorkspaceSceneKind { Direct, General, Algorithm, Programming, Custom }
internal sealed record WorkspaceSceneRef(WorkspaceSceneKind Kind, Guid Token);
internal sealed record WorkspaceQueueSummary(Guid[] SnapshotIds, long TextCharacters, long PayloadBytes, int Images);
internal sealed record WorkspaceMaterial(Guid SnapshotId, InputKind Kind, int TextCharacters, long PayloadBytes, int Images);

// No source, labels, scene names, paths, hashes, clipboard contents or exception messages.
internal sealed record WorkspaceDiagnosticEvent(Guid AppInstanceId, long Sequence, DateTimeOffset TimestampUtc,
    WorkspaceEvent Event, WorkspaceReason Reason, WorkspaceSceneRef Scene,
    WorkspaceQueueSummary OrdinaryQueue, Guid TaskId,
    Guid? OperationId = null, WorkspaceMaterial? Material = null, Guid? ObservationId = null,
    Guid? RunId = null, WorkspaceSceneRef? PreviousScene = null, Guid? PreviousTaskId = null,
    string? ErrorCode = null);

internal sealed class WorkspaceDiagnosticLog(string directory, long maximumFileBytes = 2 * 1024 * 1024)
{
    internal static string DefaultDirectory { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Superb", "diagnostics");
    private readonly DiagnosticLogFile file = new(directory, "workspace", maximumFileBytes);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    internal string FilePath => file.FilePath;
    internal bool Available => file.Available;
    internal bool Write(WorkspaceDiagnosticEvent entry)
    {
        if (!Enum.IsDefined(entry.Event) || !Enum.IsDefined(entry.Reason) || !Enum.IsDefined(entry.Scene.Kind) ||
            entry.PreviousScene is { } previous && !Enum.IsDefined(previous.Kind) ||
            entry.Material is { } material && !Enum.IsDefined(material.Kind)) return false;
        var code = entry.ErrorCode switch
        {
            "clipboard_busy" or "clipboard_bitmap_invalid" or "clipboard_empty" or "clipboard_empty_or_unsupported" or
            "clipboard_changed_during_read" or "clipboard_too_large_or_invalid" or "clipboard_read_failed" or
            "clipboard_text_unterminated" or "capture_unavailable" or "image_queue_full" or "input_queue_full" or
            "image_dimensions_invalid" or "input_empty" => entry.ErrorCode,
            null => null,
            "input_too_large" or "capture_timed_out" or "capture_cross_monitor" or "capture_region_too_small" or
            "capture_monitor_lost" or "capture_geometry_changed" or "backend_missing" or "scene_backend_missing" or
            "backend_has_no_vision" or "endpoint_invalid" or "credential_unavailable" => entry.ErrorCode,
            _ => "operation_failed"
        };
        return file.Write(JsonSerializer.Serialize(entry with { ErrorCode = code }, Json) + "\n");
    }
}
