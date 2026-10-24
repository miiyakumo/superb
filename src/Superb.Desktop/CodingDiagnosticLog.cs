using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Superb.Core.Coding;

namespace Superb.Desktop;

internal sealed class CodingDiagnosticLog(string directory, long maximumFileBytes = 2 * 1024 * 1024)
{
    internal static string DefaultDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Superb", "diagnostics");
    internal string FilePath => Path.Combine(directory, "coding.jsonl");
    internal bool Available { get; private set; } = true;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    internal bool Write(CodingDiagnosticEvent entry)
    {
        lock (gate)
        {
            try
            {
                if (!Enum.IsDefined(entry.Event) || !Enum.IsDefined(entry.Phase) ||
                    entry.Tool is { } tool && !Enum.IsDefined(tool)) return false;
                // Revalidate the one string field at the persistence boundary. No payload text is accepted.
                entry = entry with { ErrorCode = CodingDiagnostics.SafeCode(entry.ErrorCode), Transport = CodingTransportTrace.Redact(entry.Transport) };
                var line = JsonSerializer.Serialize(entry, Json) + "\n";
                Directory.CreateDirectory(directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + Encoding.UTF8.GetByteCount(line) > maximumFileBytes)
                {
                    // Four bounded files in this application's diagnostics directory, no recursive cleanup.
                    for (var i = 2; i >= 1; i--)
                    {
                        var source = Path.Combine(directory, $"coding-{i}.jsonl");
                        if (File.Exists(source)) File.Move(source, Path.Combine(directory, $"coding-{i + 1}.jsonl"), true);
                    }
                    File.Move(FilePath, Path.Combine(directory, "coding-1.jsonl"), true);
                }
                File.AppendAllText(FilePath, line, new UTF8Encoding(false));
                Available = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Available = false;
                return false;
            }
        }
    }
}
