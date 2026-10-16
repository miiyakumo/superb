using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Superb.Core;

namespace Superb.Desktop.Platform;

internal sealed class CredentialStore : ICredentialStore
{
    private readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Superb", "credentials");
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    private string Resolve(string reference) => Guid.TryParseExact(reference, "N", out _) ? Path.Combine(folder, reference + ".dpapi") : throw new SafeFailure("credential_reference_invalid");
    public string Save(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8192 || secret.Any(char.IsControl)) throw new SafeFailure("credential_invalid");
        var reference = Guid.NewGuid().ToString("N");
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Resolve(reference), Transform(bytes, true));
            return reference;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Delete(string reference) { if (reference.Length > 0) File.Delete(Resolve(reference)); }
    public string Read(string reference)
    {
        try
        {
            var path = Resolve(reference);
            if (new FileInfo(path).Length > 32768) throw new SafeFailure("credential_invalid");
            var plaintext = Transform(File.ReadAllBytes(path), false);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch { throw new SafeFailure("credential_unavailable"); }
    }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            // CRYPTPROTECT_UI_FORBIDDEN; absence of LOCAL_MACHINE scopes it to the current user.
            var ok = protect ? CryptProtectData(ref input, null, 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new SafeFailure("credential_store_failed");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size); Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0) { Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size); LocalFree(output.Data); }
        }
    }
}
