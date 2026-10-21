using System.Runtime.InteropServices;
using System.Windows.Threading;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal sealed class CornerClicks
{
    internal Native.Point? First { get; private set; }
    internal Native.Point? Second { get; private set; }
    private bool owned;
    internal bool Complete { get; private set; }
    internal bool Process(bool down, Native.Point position)
    {
        if (down)
        {
            if (Complete) return false;
            if (owned) return true;
            owned = true;
            if (First is null) First = position;
            else if (Second is null) Second = position;
            return true;
        }
        if (!owned) return false;
        owned = false; Complete = Second is not null; return true;
    }
}

// Short-lived, in-process hooks. No overlay or process injection; clicks used to select corners
// are consumed as complete down/up pairs instead of activating links in the underlying page.
internal sealed class TwoPointSelector : IDisposable
{
    private readonly TaskCompletionSource<(Native.Point First, Native.Point Second)?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private Dispatcher? dispatcher;
    private int cancelled;
    private TwoPointSelector(Action<int> progress)
    {
        thread = new Thread(() => Run(progress)) { IsBackground = true, Name = "Superb two-point capture" };
        thread.Start();
    }
    internal static async Task<(Native.Point First, Native.Point Second)?> SelectAsync(Action<int> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var selector = new TwoPointSelector(progress);
        using var registration = token.Register(selector.Cancel);
        var result = await selector.completion.Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result;
    }
    private void Run(Action<int> progress)
    {
        nint mouse = 0, keyboard = 0;
        Native.KeyboardHook? mouseCallback = null, keyboardCallback = null;
        (Native.Point, Native.Point)? result = null;
        Exception? error = null;
        try
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var clicks = new CornerClicks(); var rightOwned = false; var escapeOwned = false;
            var rightAlreadyDown = Native.GetAsyncKeyState(2) < 0;
            var leftAlreadyDown = Native.GetAsyncKeyState(1) < 0;
            void End() => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            mouseCallback = (code, message, pointer) =>
            {
                if (code < 0 || Volatile.Read(ref cancelled) != 0) return Native.CallNextHookEx(0, code, message, pointer);
                var msg = (int)message;
                if (msg is 0x201 or 0x202)
                {
                    if (leftAlreadyDown) { if (msg == 0x202) leftAlreadyDown = false; return Native.CallNextHookEx(0, code, message, pointer); }
                    var input = Marshal.PtrToStructure<Native.MouseInput>(pointer);
                    if (clicks.Process(msg == 0x201, input.Position))
                    {
                        if (msg == 0x201 && clicks.Second is null) progress(1);
                        if (clicks.Complete) { result = (clicks.First!.Value, clicks.Second!.Value); End(); }
                        return 1;
                    }
                }
                if (msg is 0x204 or 0x205)
                {
                    if (rightAlreadyDown) { if (msg == 0x205) rightAlreadyDown = false; return Native.CallNextHookEx(0, code, message, pointer); }
                    if (msg == 0x204) { rightOwned = true; return 1; }
                    if (rightOwned) { End(); return 1; }
                }
                return Native.CallNextHookEx(0, code, message, pointer);
            };
            keyboardCallback = (code, message, pointer) =>
            {
                if (code >= 0 && Volatile.Read(ref cancelled) == 0)
                {
                    var input = Marshal.PtrToStructure<Native.KeyboardInput>(pointer);
                    if (input.Key == 0x1B)
                    {
                        if ((int)message is 0x100 or 0x104) { escapeOwned = true; return 1; }
                        if (escapeOwned && (int)message is 0x101 or 0x105) { End(); return 1; }
                    }
                }
                return Native.CallNextHookEx(0, code, message, pointer);
            };
            mouse = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
            keyboard = Native.SetWindowsHookEx(13, keyboardCallback, Native.GetModuleHandle(null), 0);
            if (mouse == 0 || keyboard == 0) throw new SafeFailure("capture_selection_unavailable");
            if (Volatile.Read(ref cancelled) == 0) Dispatcher.Run();
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            if (mouse != 0) Native.UnhookWindowsHookEx(mouse);
            if (keyboard != 0) Native.UnhookWindowsHookEx(keyboard);
            GC.KeepAlive(mouseCallback); GC.KeepAlive(keyboardCallback);
            if (error is not null) completion.TrySetException(error); else completion.TrySetResult(result);
        }
    }
    private void Cancel()
    {
        Interlocked.Exchange(ref cancelled, 1);
        var active = dispatcher;
        if (active is not null && !active.HasShutdownStarted) active.BeginInvokeShutdown(DispatcherPriority.Send);
    }
    public void Dispose() { Cancel(); if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(1)); }
}
