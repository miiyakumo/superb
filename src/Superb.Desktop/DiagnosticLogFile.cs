using System.IO;
using System.Text;

namespace Superb.Desktop;

// Used only by typed diagnostic writers after their privacy validation.
internal sealed class DiagnosticLogFile(string directory, string name, long maximumFileBytes)
{
    internal string FilePath => Path.Combine(directory, name + ".jsonl");
    internal bool Available { get; private set; } = true;
    private readonly object gate = new();
    internal bool Write(string line)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + Encoding.UTF8.GetByteCount(line) > maximumFileBytes)
                {
                    for (var i = 2; i >= 1; i--)
                    {
                        var source = Path.Combine(directory, $"{name}-{i}.jsonl");
                        if (File.Exists(source)) File.Move(source, Path.Combine(directory, $"{name}-{i + 1}.jsonl"), true);
                    }
                    File.Move(FilePath, Path.Combine(directory, $"{name}-1.jsonl"), true);
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
