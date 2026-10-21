using System.Windows.Input;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal sealed class HotkeyManager(nint handle) : IDisposable
{
    private readonly Dictionary<int, string> actions = [];
    private readonly HashSet<int> regularIds = [];
    private ExclusiveReaderHotkeys? exclusive;
    public Dictionary<string, string> RegistrationErrors { get; } = [];
    public bool LastWorkerStopped { get; private set; } = true;
    public List<string> Register(IEnumerable<Hotkey> keys, bool exclusiveEnabled = false)
    {
        var entries = keys.ToArray();
        HotkeyDefaults.Validate(entries);
        Dispose();
        RegistrationErrors.Clear();
        var failures = new List<string>();
        var id = 100;
        var exclusiveActions = new Dictionary<int, string>();
        var exclusiveKeys = new Dictionary<int, uint>();
        foreach (var entry in entries)
        {
            ++id;
            if (!entry.Enabled || (entry.Mode == HotkeyMode.Exclusive && !exclusiveEnabled)) continue;
            try
            {
                var gesture = HotkeyDefaults.Parse(entry);
                if (entry.Mode == HotkeyMode.Exclusive)
                {
                    exclusiveActions[id] = entry.Action;
                    exclusiveKeys[id] = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
                    continue;
                }
                uint mods = 0x4000;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 1;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 2;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 4;
                if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= 8;
                if (!Native.RegisterHotKey(handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)))
                {
                    failures.Add(entry.Action);
                    RegistrationErrors[entry.Action] = "win32_" + System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                }
                else { actions[id] = entry.Action; regularIds.Add(id); }
            }
            catch (Exception ex) { failures.Add(entry.Action); RegistrationErrors[entry.Action] = ex.GetType().Name; }
        }
        if (exclusiveKeys.Count > 0)
        {
            try { exclusive = new(handle, exclusiveKeys); }
            catch { exclusive = null; }
            foreach (var (keyId, action) in exclusiveActions)
                if (exclusive?.RegisteredIds.Contains(keyId) == true) actions[keyId] = action;
                else { failures.Add(action); RegistrationErrors[action] = "exclusive_registration_failed"; }
        }
        return failures;
    }
    public string? GetAction(int id) => actions.GetValueOrDefault(id);
    public bool HasAction(string action) => actions.Values.Contains(action);
    public void Dispose()
    {
        if (exclusive is not null) { exclusive.Dispose(); LastWorkerStopped = exclusive.IsStopped; exclusive = null; }
        foreach (var id in regularIds) Native.UnregisterHotKey(handle, id);
        regularIds.Clear(); actions.Clear();
    }
}
