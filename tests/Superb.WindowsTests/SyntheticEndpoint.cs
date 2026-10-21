using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Superb.Core;

namespace Superb.WindowsTests;

// A loopback-only fake model: receives synthetic requests, never any saved configuration or credentials.
internal sealed class SyntheticEndpoint : IDisposable, ICredentialStore
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    public ConcurrentQueue<JsonElement> Requests { get; } = new();
    public string Error { get; private set; } = "";
    public string Url { get; }
    public SyntheticEndpoint()
    {
        listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/chat/completions";
        worker = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = socket.GetStream();
                    var header = new List<byte>(); var one = new byte[1];
                    while (header.Count < 16384)
                    {
                        if (await stream.ReadAsync(one, stop.Token) == 0) throw new IOException("Unexpected EOF");
                        header.Add(one[0]);
                        if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                    var length = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..]);
                    if (length < 0 || length > Policy.AbsoluteInputLimit) throw new IOException("Body size");
                    var body = new byte[length]; await stream.ReadExactlyAsync(body, stop.Token);
                    using var json = JsonDocument.Parse(body); Requests.Enqueue(json.RootElement.Clone());
                    const string response = "{\"choices\":[{\"message\":{\"content\":\"Synthetic OK\"},\"finish_reason\":\"stop\"}]}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n{response}"), stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!stop.IsCancellationRequested) Error = ex.GetType().Name; }
        });
    }
    public string Read(string reference) => throw new InvalidOperationException("Synthetic test must not read credentials");
    public void Dispose() { stop.Cancel(); listener.Stop(); worker.GetAwaiter().GetResult(); stop.Dispose(); }
}
