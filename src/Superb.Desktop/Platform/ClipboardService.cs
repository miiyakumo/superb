using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal sealed class ClipboardService(nint owner)
{
    private readonly string instance = Guid.NewGuid().ToString("N");
    private readonly uint sourceFormat = Register("Superb.Source.v1");
    private readonly uint excludeFormat = Register("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint historyFormat = Register("CanIncludeInClipboardHistory");
    private readonly uint cloudFormat = Register("CanUploadToCloudClipboard");
    private string? lastWrite;
    public uint Sequence => Native.GetClipboardSequenceNumber();
    private static uint Register(string name)
    {
        var id = Native.RegisterClipboardFormat(name);
        return id == 0 ? throw new SafeFailure("clipboard_format_registration_failed") : id;
    }
    public (InputSnapshot Snapshot, bool OwnOutput) Read()
    {
        if (!Native.OpenClipboard(owner)) throw new SafeFailure("clipboard_busy");
        try
        {
            var sequence = Sequence;
            var own = ReadMarker()?.StartsWith("Superb-v1|", StringComparison.Ordinal) == true;
            if (own) return (new(Guid.NewGuid(), InputKind.ClipboardText, Payload.FromText(""), sequence, DateTimeOffset.UtcNow), true);
            Payload payload;
            InputKind kind;
            // Prefer a static bitmap if the source offers both a bitmap and alternate text.
            if (Native.IsClipboardFormatAvailable(2))
            {
                var handle = Native.GetClipboardData(2);
                if (handle == 0 || Native.GetObjectW(handle, Marshal.SizeOf<Native.Bitmap>(), out var info) == 0)
                    throw new SafeFailure("clipboard_bitmap_invalid");
                ImageProcessor.ValidateSize(info.Width, info.Height);
                var bitmap = Imaging.CreateBitmapSourceFromHBitmap(handle, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                payload = ImageProcessor.Encode(bitmap);
                kind = InputKind.ClipboardImage;
            }
            else if (Native.IsClipboardFormatAvailable(13))
            {
                var text = ReadUnicode(13, Policy.AbsoluteInputLimit);
                if (string.IsNullOrWhiteSpace(text)) throw new SafeFailure("clipboard_empty");
                payload = Payload.FromText(text);
                kind = InputKind.ClipboardText;
            }
            else throw new SafeFailure("clipboard_empty_or_unsupported");
            if (Sequence != sequence) throw new SafeFailure("clipboard_changed_during_read");
            return (new(Guid.NewGuid(), kind, payload, sequence, DateTimeOffset.UtcNow), false);
        }
        finally { Native.CloseClipboard(); }
    }
    public bool Write(string text, Guid attempt, long generation, uint? expectedSequence)
    {
        if (string.IsNullOrEmpty(text)) throw new SafeFailure("response_empty");
        if (!Native.OpenClipboard(owner)) throw new SafeFailure("clipboard_busy");
        try
        {
            // Check and mutate under the same native clipboard lock.
            if (expectedSequence.HasValue && Sequence != expectedSequence.Value) return false;
            var marker = $"Superb-v1|{instance}|{attempt:N}|{generation}";
            if (!Native.EmptyClipboard()) throw new SafeFailure("clipboard_write_failed");
            try
            {
                Put(excludeFormat, [1, 0, 0, 0]);
                Put(historyFormat, [0, 0, 0, 0]);
                Put(cloudFormat, [0, 0, 0, 0]);
                Put(sourceFormat, Encoding.Unicode.GetBytes(marker + '\0'));
                Put(13, Encoding.Unicode.GetBytes(text + '\0'));
                lastWrite = marker;
                return true;
            }
            catch { Native.EmptyClipboard(); throw; }
        }
        finally { Native.CloseClipboard(); }
    }
    public void ClearOwned()
    {
        if (lastWrite is null || !Native.OpenClipboard(owner)) return;
        try
        {
            if (Native.GetClipboardOwner() == owner && ReadMarker() == lastWrite) Native.EmptyClipboard();
            lastWrite = null;
        }
        finally { Native.CloseClipboard(); }
    }
    private string? ReadMarker() => Native.IsClipboardFormatAvailable(sourceFormat) ? ReadUnicode(sourceFormat, 2048) : null;
    private static string ReadUnicode(uint format, int limit)
    {
        var handle = Native.GetClipboardData(format);
        var size = handle == 0 ? 0 : Native.GlobalSize(handle);
        if (size == 0) return "";
        if (size > (nuint)limit || size % 2 != 0) throw new SafeFailure("clipboard_too_large_or_invalid");
        var pointer = Native.GlobalLock(handle);
        if (pointer == 0) throw new SafeFailure("clipboard_read_failed");
        try
        {
            var text = Marshal.PtrToStringUni(pointer, (int)size / 2) ?? "";
            var end = text.IndexOf('\0');
            if (end < 0) throw new SafeFailure("clipboard_text_unterminated");
            return text[..end];
        }
        finally { Native.GlobalUnlock(handle); }
    }
    private static void Put(uint format, byte[] bytes)
    {
        var memory = Native.GlobalAlloc(0x0042, (nuint)bytes.Length);
        if (memory == 0) throw new SafeFailure("clipboard_allocate_failed");
        try
        {
            var pointer = Native.GlobalLock(memory);
            if (pointer == 0) throw new SafeFailure("clipboard_allocate_failed");
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { Native.GlobalUnlock(memory); }
            if (Native.SetClipboardData(format, memory) == 0) throw new SafeFailure("clipboard_write_failed");
            memory = 0; // Ownership transferred to Windows.
        }
        finally { if (memory != 0) Native.GlobalFree(memory); Array.Clear(bytes); }
    }
}
