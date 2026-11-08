using System.Collections.Immutable;
using System.Text;

namespace Superb.Core;

internal sealed record CodingFrame(Guid TaskId, int PriorTurns, int Covered, string? Summary, bool Prepared = false);

public sealed class CodingConversation
{
    public const string DefaultSummaryPrompt = "为接手同一任务的模型写一段简洁的交接笔记。保留当前有效的目标和约束、平台实际做过的修改、已尝试的方案及结果、最后的卡点和待验证的判断。保留有辨识度的文件名、失败用例和必要的报错原文。区分已执行的修改、提出的建议和未经验证的说法。用户后来修正的信息优先。直接输出交接笔记。";
    private readonly List<(Guid InputId, Payload Input, string Answer)> turns = [];
    private Guid id = Guid.NewGuid();
    private int covered;
    private string? summary;

    internal void Clear()
    {
        id = Guid.NewGuid();
        turns.Clear();
        covered = 0;
        summary = null;
    }

    internal PreparedRequest Freeze(PreparedRequest request)
    {
        if (request.Frame is { } frame)
        {
            if (frame.TaskId != id || frame.PriorTurns < turns.Count - 1 || frame.PriorTurns > turns.Count ||
                (frame.PriorTurns < turns.Count && request.Snapshot.Id != turns[^1].InputId))
                throw new SafeFailure("coding_task_expired");
            return request;
        }
        var window = Window(request);
        var tokens = Math.Min(request.Backend.MaxTokens, Math.Min(8192, window / 4));
        return request with { Backend = request.Backend with { MaxTokens = tokens },
            Frame = new(id, turns.Count, covered, summary) };
    }

    internal void Commit(PreparedRequest request, string answer)
    {
        var frame = request.Frame!;
        if (frame.TaskId != id) throw new SafeFailure("coding_task_expired");
        var turn = (request.Snapshot.Id, request.Content, answer);
        if (frame.PriorTurns == turns.Count) turns.Add(turn);
        else if (frame.PriorTurns == turns.Count - 1) turns[^1] = turn;
        else throw new SafeFailure("coding_task_expired");
        covered = frame.Covered;
        summary = frame.Summary;
    }

    private static int Window(PreparedRequest request) => request.Backend.ContextWindowTokens ??
        (DeepSeekSetup.UsesMillionTokenWindow(request.Backend) ? 1_000_000 : 32768);
    private static long MessageCost(Backend backend, string role, Payload content)
    {
        var text = content.Text ?? "";
        long envelope = 8L + role.Length;
        if (!DeepSeekSetup.UsesMillionTokenWindow(backend))
            return envelope + Encoding.UTF8.GetByteCount(text);

        // DeepSeek documents averages for English and Chinese. Budget code punctuation,
        // digits and other scripts separately; this is an estimate, not a tokenizer.
        long tenths = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            tenths += rune.Value switch
            {
                >= 'A' and <= 'Z' or >= 'a' and <= 'z' or ' ' => 3,
                >= 0x4E00 and <= 0x9FFF => 6,
                _ => 10 * rune.Utf8SequenceLength
            };
        }
        return envelope + (tenths + 9) / 10 + 1024L * content.Images.Count();
    }

    internal static long Count(PreparedRequest request) => 32L +
        (string.IsNullOrWhiteSpace(request.SystemPrompt) ? 0 : MessageCost(request.Backend, "system", Payload.FromText(request.SystemPrompt))) +
        request.History.Sum(m => MessageCost(request.Backend, m.Role, m.Content)) + MessageCost(request.Backend, "user", request.Content);

    private static bool Fits(PreparedRequest request, long budget)
    {
        if (Count(request) > budget) return false;
        try { Policy.Check(request); return true; }
        catch (SafeFailure ex) when (ex.Code is "input_too_large" or "image_queue_full" or "image_dimensions_invalid")
        { return false; }
    }

    private PreparedRequest Compose(PreparedRequest request, CodingFrame frame)
    {
        var history = ImmutableArray.CreateBuilder<ChatMessage>();
        if (frame.Summary is not null)
            history.Add(new("user", Payload.FromText($"此前对话摘要（截至第 {frame.Covered} 轮）\n{frame.Summary}")));
        for (int i = frame.Covered; i < frame.PriorTurns; i++)
        {
            history.Add(new("user", turns[i].Input));
            history.Add(new("assistant", Payload.FromText(turns[i].Answer)));
        }
        return request with { History = history.ToImmutable(), Frame = frame with { Prepared = true } };
    }

    internal async Task<PreparedRequest> PrepareAsync(PreparedRequest request, IBackendClient client,
        Action onSummary, bool force, CancellationToken token)
    {
        var frame = request.Frame!;
        long budget = (long)Window(request) - request.Backend.MaxTokens;
        var bare = request with { History = [] };
        Policy.Check(bare);
        // The generic 32K fallback schedules compaction; only the server knows this endpoint's limit.
        bool provisionalWindow = request.Backend.ContextWindowTokens == null && !DeepSeekSetup.UsesMillionTokenWindow(request.Backend);
        if (!provisionalWindow && Count(bare) > budget) throw new SafeFailure("context_length_exceeded");
        var current = frame.Prepared ? request : Compose(request, frame);
        if (!force && frame.Prepared)
        {
            Policy.Check(current);
            if (!provisionalWindow && Count(current) > budget) throw new SafeFailure("context_length_exceeded");
            return current;
        }
        if (!force && Count(current) < budget * 80 / 100 && Fits(current, budget)) return current;
        if (frame.PriorTurns == 0) return current;

        int limit = (int)Math.Clamp(budget / 8, 1, 1024);
        long target = budget * 60 / 100;
        long initial = Count(current);
        int initialImages = current.History.Sum(m => m.Content.Images.Count());
        bool Shorter(PreparedRequest candidate) => Count(candidate) < initial ||
            candidate.History.Sum(m => m.Content.Images.Count()) < initialImages;
        while (frame.Covered < frame.PriorTurns)
        {
            // Choose the smallest complete prefix predicted to reach the low watermark.
            int end = frame.Covered;
            long remaining = Count(current);
            if (frame.Summary != null) remaining -= MessageCost(request.Backend, "user", current.History[0].Content);
            do
            {
                var turn = turns[end++];
                remaining -= MessageCost(request.Backend, "user", turn.Input) + MessageCost(request.Backend, "assistant", Payload.FromText(turn.Answer));
            } while (end < frame.PriorTurns && remaining + limit > target);
            var old = new List<ChatMessage>();
            for (int i = frame.Covered; i < end; i++)
            {
                old.Add(new("user", turns[i].Input));
                old.Add(new("assistant", Payload.FromText(turns[i].Answer)));
            }
            onSummary();
            var note = await SummarizeAsync(request, frame.Summary, old, limit, client, token);
            token.ThrowIfCancellationRequested();
            frame = frame with { Covered = end, Summary = note };
            current = Compose(request, frame);
            if (Fits(current, target) && (!force || Shorter(current))) return current;
        }
        if (Fits(current, provisionalWindow ? long.MaxValue : budget) && (!force || Shorter(current))) return current;
        if (frame.Summary != null)
        {
            onSummary();
            limit = (int)Math.Clamp((budget - Count(bare) - 64) / 4, 1, limit);
            var shorter = await SummarizeAsync(request, null, [new("user", Payload.FromText(frame.Summary))], limit, client, token);
            token.ThrowIfCancellationRequested();
            current = Compose(request, frame with { Summary = shorter });
            if (Fits(current, provisionalWindow ? long.MaxValue : budget) && (!force || Shorter(current))) return current;
        }
        throw new SafeFailure("context_length_exceeded");
    }

    private static async Task<string> SummarizeAsync(PreparedRequest request, string? prior, List<ChatMessage> old,
        int limit, IBackendClient client, CancellationToken token)
    {
        var backend = request.Backend with { MaxTokens = Math.Min(limit, request.Backend.MaxTokens) };
        var budget = Window(request) - backend.MaxTokens;
        var prompt = string.IsNullOrWhiteSpace(request.SummaryPrompt) ? DefaultSummaryPrompt : request.SummaryPrompt;
        var instruction = Payload.FromText("请将上面的历史整理成一段交接摘要。");
        var summaryInput = new InputSnapshot(Guid.NewGuid(), InputKind.ManualText, instruction, 0, DateTimeOffset.UtcNow);
        var template = new PreparedRequest(summaryInput, instruction, backend, prompt);
        var pending = new LinkedList<ChatMessage>(old);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var batch = template;
            if (prior != null) batch = batch with { History = [new("user", Payload.FromText("此前摘要\n" + prior))] };
            bool progressed = false;
            while (pending.First is { } next)
            {
                var candidate = batch with { History = batch.History.Add(next.Value) };
                if (Fits(candidate, budget))
                {
                    batch = candidate;
                    pending.RemoveFirst();
                    progressed = true;
                }
                else if (progressed) break;
                else SplitOldMessage(pending);
            }
            var output = new StringBuilder();
            bool complete = false;
            await foreach (var item in client.SendAsync(batch, token))
            {
                token.ThrowIfCancellationRequested();
                if (item.Delta != null)
                {
                    if ((long)output.Length + item.Delta.Length > backend.MaxOutputChars) throw new SafeFailure("output_too_large");
                    output.Append(item.Delta);
                }
                if (item.Completed) { complete = true; break; }
            }
            if (!complete) throw new SafeFailure("stream_incomplete");
            prior = output.ToString();
            if (string.IsNullOrWhiteSpace(prior)) throw new SafeFailure("response_empty");
        }
        return prior!;
    }

    // Split only old material, keeping Unicode scalars and attachments in their original order.
    private static void SplitOldMessage(LinkedList<ChatMessage> pending)
    {
        var msg = pending.First!.Value;
        pending.RemoveFirst();
        var text = msg.Content.Text ?? "";
        var images = msg.Content.Images.ToArray();
        if (images.Length > 0 && (text.Length > 0 || images.Length > 1))
        {
            foreach (var img in images.Reverse())
                pending.AddFirst(msg with { Content = new Payload(null, img.Png, img.Width, img.Height) });
            if (text.Length > 0) pending.AddFirst(msg with { Content = Payload.FromText(text) });
            return;
        }
        var middle = text.Length / 2;
        if (middle > 0 && char.IsLowSurrogate(text[middle])) middle++;
        if (middle == text.Length) throw new SafeFailure("context_length_exceeded");
        if (middle == 0) throw new SafeFailure("context_length_exceeded");
        pending.AddFirst(msg with { Content = Payload.FromText(text[middle..]) });
        pending.AddFirst(msg with { Content = Payload.FromText(text[..middle]) });
    }
}
