using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Superb.Core.Coding;

public enum CodingTransportStage
{
    Connecting, ResolvingDns, ConnectingSocket, TlsHandshake, UploadingRequest,
    WaitingForResponseHeaders, WaitingForResponseBody, ReadingResponseBody, ParsingResponse, Completed
}

// Only enums, numbers and a hash of the provider's request ID cross the diagnostics boundary.
public sealed record CodingTransportSnapshot(CodingTransportStage Stage, long ElapsedMilliseconds,
    long StageElapsedMilliseconds, long RequestBytes, long ResponseBytes, long? ResponseContentLength,
    long? ConnectionMilliseconds, long? DnsMilliseconds, long? TcpMilliseconds, long? TlsMilliseconds,
    long? UploadMilliseconds, long? HeadersReceivedMilliseconds, long? FirstByteMilliseconds,
    long? MillisecondsSinceLastByte, int? HttpStatusCode, string? RequestIdFingerprint,
    double? ServerQueueMilliseconds, double? ServerProcessingMilliseconds);

public sealed class CodingTransportTrace
{
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private CodingTransportStage stage = CodingTransportStage.Connecting;
    private long stageStart, requestBytes, responseBytes;
    private long? length, connection, dns, tcp, tls, upload, headers, firstByte, lastByte;
    private long? dnsStart, tcpStart, tlsStart, uploadStart;
    private int? status;
    private string? requestId;
    private double? serverQueue, serverProcessing;
    private bool finished;
    private bool dnsFailed, tcpFailed, tlsFailed;
    internal bool Started { get; private set; }
    private static readonly AsyncLocal<CodingTransportTrace?> Current = new();
    private static readonly NetworkListener Listener = new();

    public CodingTransportSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var now = clock.ElapsedMilliseconds;
                return new(stage, now, now - stageStart, requestBytes, responseBytes, length, connection,
                    dns, tcp, tls, upload, headers, firstByte, lastByte is { } last ? now - last : null,
                    status, requestId, serverQueue, serverProcessing);
            }
        }
    }

    internal IDisposable Begin(long bytes)
    {
        _ = Listener;
        lock (gate) { requestBytes = bytes; clock.Start(); Started = true; }
        var previous = Current.Value;
        Current.Value = this;
        return new Scope(this, previous);
    }

    private sealed class Scope(CodingTransportTrace trace, CodingTransportTrace? previous) : IDisposable
    {
        public void Dispose()
        {
            trace.Finish();
            Current.Value = previous;
        }
    }

    private void Finish() { lock (gate) { finished = true; clock.Stop(); } }
    private void SetStage(CodingTransportStage next)
    {
        if (stage == next) return;
        stage = next; stageStart = clock.ElapsedMilliseconds;
    }

    // Public networking events are correlated through the request's async flow. Never inspect
    // their payloads: they can contain hosts, URLs, certificate details and exception messages.
    private sealed class NetworkListener : EventListener
    {
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name is "System.Net.Http" or "System.Net.NameResolution" or "System.Net.Sockets" or "System.Net.Security")
                EnableEvents(source, EventLevel.Informational, EventKeywords.All);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            Current.Value?.NetworkEvent(data.EventSource.Name, data.EventName);
        }
    }

    private void NetworkEvent(string source, string? name)
    {
        lock (gate)
        {
            if (finished) return;
            var now = clock.ElapsedMilliseconds;
            // DNS/connect work can outlive its originating request. It must never rewind
            // an upload/response stage, nor mutate a finished request's measurements.
            if (stage <= CodingTransportStage.TlsHandshake)
            {
                switch ((source, name))
                {
                    case ("System.Net.NameResolution", "ResolutionStart"):
                        dnsStart = now; dnsFailed = false; SetStage(CodingTransportStage.ResolvingDns); break;
                    case ("System.Net.NameResolution", "ResolutionFailed"):
                        dnsFailed = true; if (dnsStart is { } failedDns) dns = now - failedDns;
                        SetStage(CodingTransportStage.ResolvingDns); break;
                    case ("System.Net.NameResolution", "ResolutionStop"):
                        if (dnsStart is { } d) dns = now - d;
                        if (!dnsFailed) SetStage(CodingTransportStage.Connecting); break;
                    case ("System.Net.Sockets", "ConnectStart"):
                        tcpStart = now; tcpFailed = false; SetStage(CodingTransportStage.ConnectingSocket); break;
                    case ("System.Net.Sockets", "ConnectFailed"):
                        tcpFailed = true; if (tcpStart is { } failedTcp) tcp = now - failedTcp;
                        SetStage(CodingTransportStage.ConnectingSocket); break;
                    case ("System.Net.Sockets", "ConnectStop"):
                        if (tcpStart is { } c) tcp = now - c;
                        if (!tcpFailed) SetStage(CodingTransportStage.Connecting); break;
                    case ("System.Net.Security", "HandshakeStart"):
                        tlsStart = now; tlsFailed = false; SetStage(CodingTransportStage.TlsHandshake); break;
                    case ("System.Net.Security", "HandshakeFailed"):
                        tlsFailed = true; if (tlsStart is { } failedTls) tls = now - failedTls;
                        SetStage(CodingTransportStage.TlsHandshake); break;
                    case ("System.Net.Security", "HandshakeStop"):
                        if (tlsStart is { } t) tls = now - t;
                        if (!tlsFailed) SetStage(CodingTransportStage.Connecting); break;
                }
            }
            if (source != "System.Net.Http") return;
            switch (name)
            {
                case "RequestHeadersStart":
                    connection ??= now; uploadStart ??= now;
                    SetStage(CodingTransportStage.UploadingRequest); break;
                case "RequestContentStop":
                    if (uploadStart is { } u) upload = now - u;
                    SetStage(CodingTransportStage.WaitingForResponseHeaders); break;
            }
        }
    }

    internal void ReceivedHeaders(HttpResponseMessage response)
    {
        lock (gate)
        {
            if (finished) return;
            headers = clock.ElapsedMilliseconds; status = (int)response.StatusCode;
            length = response.Content.Headers.ContentLength;
            foreach (var key in new[] { "x-request-id", "request-id", "x-ms-request-id" })
            {
                if (!response.Headers.TryGetValues(key, out var values)) continue;
                var id = values.FirstOrDefault();
                if (id is { Length: > 0 and <= 256 })
                    requestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..24];
                break;
            }
            if (response.Headers.TryGetValues("Server-Timing", out var timing))
            {
                // Free-form descriptions and unknown metric names never leave this method.
                foreach (var metric in timing.Take(8).SelectMany(v => v.Length <= 4096 ? v.Split(',') : []))
                {
                    var parts = metric.Split(';');
                    var key = parts[0].Trim().ToLowerInvariant();
                    if (key is not ("queue" or "queue_time" or "processing" or "inference")) continue;
                    foreach (var part in parts.Skip(1))
                    {
                        var pair = part.Trim().Split('=', 2);
                        if (pair.Length != 2 || !pair[0].Equals("dur", StringComparison.OrdinalIgnoreCase) ||
                            !double.TryParse(pair[1].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
                            !double.IsFinite(duration) || duration is < 0 or > 86400000) continue;
                        if (key is "queue" or "queue_time") serverQueue = duration;
                        else serverProcessing = duration;
                    }
                }
            }
            SetStage(CodingTransportStage.WaitingForResponseBody);
        }
    }

    internal void ReceivedBytes(int count)
    {
        lock (gate)
        {
            if (finished) return;
            responseBytes += count; lastByte = clock.ElapsedMilliseconds; firstByte ??= lastByte;
            SetStage(CodingTransportStage.ReadingResponseBody);
        }
    }
    internal void Parsing() { lock (gate) if (!finished) SetStage(CodingTransportStage.ParsingResponse); }
    internal void Complete() { lock (gate) if (!finished) SetStage(CodingTransportStage.Completed); }

    public static string StageLabel(CodingTransportStage stage) => stage switch
    {
        CodingTransportStage.ResolvingDns => "解析域名", CodingTransportStage.ConnectingSocket => "建立网络连接",
        CodingTransportStage.TlsHandshake => "TLS 握手", CodingTransportStage.UploadingRequest => "上传请求",
        CodingTransportStage.WaitingForResponseHeaders => "等待服务端响应头",
        CodingTransportStage.WaitingForResponseBody => "等待响应体首字节",
        CodingTransportStage.ReadingResponseBody => "接收响应体", CodingTransportStage.ParsingResponse => "解析工具调用",
        CodingTransportStage.Completed => "响应接收完成", _ => "建立连接／代理协商"
    };

    // Used for both live progress and failures; no provider text is included.
    public static string Describe(CodingTransportSnapshot value, long extraMilliseconds = 0) =>
        $"请求阶段：{StageLabel(value.Stage)} · 阶段已用 {(value.StageElapsedMilliseconds + extraMilliseconds) / 1000.0:F1} 秒" +
        $" · 已接收 {value.ResponseBytes:N0} 字节" +
        (value.MillisecondsSinceLastByte is { } idle ? $" · 距最后收包 {(idle + extraMilliseconds) / 1000.0:F1} 秒" : "");

    public static CodingTransportSnapshot? Redact(CodingTransportSnapshot? value)
    {
        if (value is null || !Enum.IsDefined(value.Stage)) return null;
        // Recheck at persistence even if an arbitrary client supplied the snapshot.
        double? SafeDuration(double? duration) => duration is { } d && double.IsFinite(d) && d is >= 0 and <= 86400000 ? d : null;
        return value with
        {
            RequestIdFingerprint = value.RequestIdFingerprint is { Length: 24 } id && id.All(Uri.IsHexDigit) ? id : null,
            ServerQueueMilliseconds = SafeDuration(value.ServerQueueMilliseconds),
            ServerProcessingMilliseconds = SafeDuration(value.ServerProcessingMilliseconds)
        };
    }
}
