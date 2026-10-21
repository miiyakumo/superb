using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Superb.Desktop.Platform;

// Own only complete cycles of configured unmodified function keys. Never buffer/replay ordinary input.
internal sealed class ExclusiveKeyCycle
{
    private bool down, owned;
    internal void AlreadyDown() => down = true;
    internal (bool Suppress, bool Dispatch) Process(bool pressed, bool modified)
    {
        if (!pressed)
        {
            var suppress = owned;
            down = owned = false;
            return (suppress, false);
        }
        if (down) return (owned, false);
        down = true;
        owned = !modified;
        return (owned, owned);
    }
}

internal sealed class ExclusiveReaderHotkeys : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? dispatcher;
    private int disposed;
    internal HashSet<int> RegisteredIds { get; } = [];
    internal bool IsStopped => !thread.IsAlive;

    internal ExclusiveReaderHotkeys(nint target, IReadOnlyDictionary<int, uint> requested)
    {
        thread = new Thread(() => Run(target, requested)) { IsBackground = true, Name = "Superb exclusive reader keys" };
        thread.Start();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new TimeoutException("Exclusive reader keys did not initialize");
        }
    }

    private void Run(nint target, IReadOnlyDictionary<int, uint> requested)
    {
        nint hook = 0;
        Native.KeyboardHook? callback = null;
        var reserved = new List<int>();
        try
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var bindings = new Dictionary<uint, (int Id, ExclusiveKeyCycle Cycle)>();
            foreach (var (id, key) in requested)
            {
                // Reserve on this worker's message queue, so conflicts are reported rather than stolen.
                if (!Native.RegisterHotKey(0, id, 0x4000, key)) continue;
                reserved.Add(id);
                var cycle = new ExclusiveKeyCycle();
                if (Native.GetAsyncKeyState((int)key) < 0) cycle.AlreadyDown();
                bindings.Add(key, (id, cycle));
            }
            uint[] modifiers = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
            var held = modifiers.Where(k => Native.GetAsyncKeyState((int)k) < 0).ToHashSet();
            callback = (code, message, pointer) =>
            {
                if (code < 0) return Native.CallNextHookEx(0, code, message, pointer);
                var msg = (int)message;
                if (msg is not (0x100 or 0x101 or 0x104 or 0x105)) return Native.CallNextHookEx(0, code, message, pointer);
                var input = Marshal.PtrToStructure<Native.KeyboardInput>(pointer);
                var pressed = msg is 0x100 or 0x104;
                if (Array.IndexOf(modifiers, input.Key) >= 0)
                {
                    if (pressed) held.Add(input.Key); else held.Remove(input.Key);
                }
                if (bindings.TryGetValue(input.Key, out var binding))
                {
                    var result = binding.Cycle.Process(pressed, held.Count != 0);
                    if (result.Dispatch && Volatile.Read(ref disposed) == 0)
                        Native.PostMessage(target, 0x312, binding.Id, 0);
                    if (result.Suppress) return 1;
                }
                return Native.CallNextHookEx(0, code, message, pointer);
            };
            if (bindings.Count > 0)
                hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
            if (hook != 0) RegisteredIds.UnionWith(reserved);
            ready.TrySetResult(true);
            if (hook != 0 && Volatile.Read(ref disposed) == 0) Dispatcher.Run();
        }
        catch { ready.TrySetResult(false); }
        finally
        {
            if (hook != 0) Native.UnhookWindowsHookEx(hook);
            foreach (var id in reserved) Native.UnregisterHotKey(0, id);
            GC.KeepAlive(callback);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        thread.Join(TimeSpan.FromSeconds(2));
    }
}
