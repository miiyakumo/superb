using Superb.Core;
using Superb.Desktop;

namespace Superb.WindowsTests;

internal static class WorkspaceTestInput
{
    internal static void QueueText(this MainWindow window, string text) => window.QueueSnapshot(
        new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText(text), 1, DateTimeOffset.UtcNow), "synthetic clipboard text");
    internal static string QueuedText(this MainWindow window) => string.Join("\n\n", window.WorkspaceItems.Select(i => i.Snapshot.Content.Text).Where(t => !string.IsNullOrEmpty(t)));
}
