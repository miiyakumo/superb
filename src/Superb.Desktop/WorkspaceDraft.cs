using System.Collections.Immutable;
using Superb.Core;

namespace Superb.Desktop;

internal sealed record WorkspaceAttachment(InputSnapshot Snapshot, string Label);
// Session-only input queue. Scene selection never acquires input or changes its source.
internal sealed class WorkspaceDraft
{
    internal const int MaximumItems = 10;
    private readonly List<WorkspaceAttachment> items = [];
    public IReadOnlyList<WorkspaceAttachment> Items => items;
    public InputSnapshot? Input => items.Count == 0 ? null : Build("", items[0].Snapshot.ClipboardSequence);
    public string Label => items.Count == 1 ? items[0].Label : $"本次输入 · {items.Count} 项";
    public int ImageCount => items.Sum(i => i.Snapshot.Content.Images.Count());
    public void Set(InputSnapshot snapshot, string label)
    {
        Validate([new(snapshot, label)]);
        items.Clear(); items.Add(new(snapshot, label));
    }
    public void Add(InputSnapshot snapshot, string label)
    {
        Validate(items.Append(new(snapshot, label)));
        items.Add(new(snapshot, label));
    }
    private static void Validate(IEnumerable<WorkspaceAttachment> source)
    {
        var values = source.ToArray(); var images = values.SelectMany(i => i.Snapshot.Content.Images).ToArray();
        if (values.Length > MaximumItems) throw new SafeFailure("input_queue_full");
        if (images.Length > Policy.MaximumImages) throw new SafeFailure("image_queue_full");
        if (images.Any(i => i.Width <= 0 || i.Height <= 0 || (long)i.Width * i.Height > Policy.MaximumPixels) ||
            images.Sum(i => (long)i.Width * i.Height) > Policy.MaximumPixels) throw new SafeFailure("image_dimensions_invalid");
        if (values.Sum(i => i.Snapshot.Content.Size) > Policy.AbsoluteInputLimit) throw new SafeFailure("input_too_large");
    }
    public void Remove(Guid id) => items.RemoveAll(i => i.Snapshot.Id == id);
    public void Clear() => items.Clear();
    public bool HasInput(string supplement) => items.Count > 0 || !string.IsNullOrWhiteSpace(supplement);
    public InputSnapshot Freeze(string supplement, uint sequence)
    {
        if (!HasInput(supplement)) throw new SafeFailure("input_empty");
        var snapshot = Build(supplement, sequence);
        return snapshot with { Id = Guid.NewGuid() };
    }
    private InputSnapshot Build(string supplement, uint sequence)
    {
        if (items.Count == 0) return new(Guid.NewGuid(), InputKind.ManualText, Payload.FromText(supplement), sequence, DateTimeOffset.UtcNow);
        var images = items.SelectMany(i => i.Snapshot.Content.Images).ToArray();
        var text = string.Join("\n\n", items.Select(i => i.Snapshot.Content.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (!string.IsNullOrWhiteSpace(supplement)) text = text.Length == 0 ? supplement : text + "\n\n补充要求：\n" + supplement;
        var content = images.Length == 0 ? Payload.FromText(text) : new Payload(text, images[0].Png, images[0].Width, images[0].Height,
            images.Skip(1).ToImmutableArray());
        var first = items[0].Snapshot;
        var kind = images.Length == 0 ? first.Kind : items.First(i => i.Snapshot.Content.HasImage).Snapshot.Kind;
        return first with { Kind = kind, Content = content };
    }
}
