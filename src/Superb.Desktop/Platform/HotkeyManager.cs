using System.Windows.Input;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal sealed class HotkeyManager(nint handle) : IDisposable
{
    private readonly Dictionary<int, string> actions = [];
    private readonly HashSet<int> regularIds = [];
    private ExclusiveReaderHotkeys? exclusive;
    public List<string> Register(IEnumerable<Hotkey> keys, bool exclusiveReaderKeys = false)
    {
        Dispose();
        var failures = new List<string>();
        var converter = new KeyGestureConverter();
        var id = 100;
        var exclusiveActions = new Dictionary<int, string>();
        var exclusiveKeys = new Dictionary<int, uint>();
        foreach (var entry in keys)
        {
            ++id;
            if (exclusiveReaderKeys && ExclusiveReaderHotkeys.Keys.TryGetValue(entry.Action, out var key))
            {
                exclusiveActions[id] = entry.Action;
                exclusiveKeys[id] = key;
                continue;
            }
            try
            {
                var gesture = (KeyGesture?)converter.ConvertFromInvariantString(entry.Gesture) ?? throw new SafeFailure("hotkey_invalid");
                uint mods = 0x4000;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 1;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 2;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 4;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= 8;
                if (!Native.RegisterHotKey(handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key))) failures.Add(entry.Action);
                else { actions[id] = entry.Action; regularIds.Add(id); }
            }
            catch { failures.Add(entry.Action); }
        }
        if (exclusiveKeys.Count > 0)
        {
            try { exclusive = new(handle, exclusiveKeys); }
            catch { exclusive = null; }
            foreach (var (keyId, action) in exclusiveActions)
                if (exclusive?.RegisteredIds.Contains(keyId) == true) actions[keyId] = action;
                else failures.Add(action);
        }
        return failures;
    }
    public string? GetAction(int id) => actions.GetValueOrDefault(id);
    public bool HasAction(string action) => actions.Values.Contains(action);
    public void Dispose()
    {
        exclusive?.Dispose(); exclusive = null;
        foreach (var id in regularIds) Native.UnregisterHotKey(handle, id);
        regularIds.Clear(); actions.Clear();
    }
}
