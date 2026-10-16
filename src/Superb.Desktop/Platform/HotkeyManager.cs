using System.Windows.Input;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal sealed class HotkeyManager(nint handle) : IDisposable
{
    private readonly Dictionary<int, string> actions = [];
    public List<string> Register(IEnumerable<Hotkey> keys)
    {
        Dispose();
        var failures = new List<string>();
        var converter = new KeyGestureConverter();
        var id = 100;
        foreach (var entry in keys)
        {
            try
            {
                var gesture = (KeyGesture?)converter.ConvertFromInvariantString(entry.Gesture) ?? throw new SafeFailure("hotkey_invalid");
                uint mods = 0x4000;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 1;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 2;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 4;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= 8;
                if (!Native.RegisterHotKey(handle, ++id, mods, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key))) failures.Add(entry.Action);
                else actions[id] = entry.Action;
            }
            catch { failures.Add(entry.Action); }
        }
        return failures;
    }
    public string? GetAction(int id) => actions.GetValueOrDefault(id);
    public bool HasAction(string action) => actions.Values.Contains(action);
    public void Dispose() { foreach (var id in actions.Keys) Native.UnregisterHotKey(handle, id); actions.Clear(); }
}
