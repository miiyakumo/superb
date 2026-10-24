using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Superb.Core;
using Superb.Core.Coding;
using Superb.Desktop;
using Superb.Desktop.Platform;

namespace Superb.WindowsTests;

// Explicit opt-in network evaluation. Sends only the synthetic fixtures below, never clipboard,
// desktop, repository files, saved scene instructions, credentials in text, or real task memory.
internal static class CodingLiveEvaluation
{
    internal static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        try
        {
            string? Option(string name)
            {
                var i = Array.IndexOf(args, name);
                if (i < 0) return null;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new SafeFailure("evaluation_argument_invalid");
                return args[i + 1];
            }
            var output = Option("--output") ?? throw new SafeFailure("evaluation_output_required");
            if (!Path.IsPathFullyQualified(output) || File.Exists(output)) throw new SafeFailure("evaluation_output_invalid");
            var allowed = new HashSet<string> { "--coding-live-evaluation", "--output", "--case" };
            if (args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal) && !allowed.Contains(arg)))
                throw new SafeFailure("evaluation_argument_invalid");
            var fixture = (Option("--case") ?? "complexity").ToLowerInvariant();
            if (fixture is not ("complexity" or "waiting" or "missing")) throw new SafeFailure("evaluation_case_invalid");
            if (!File.Exists(Configuration.FilePath) || new FileInfo(Configuration.FilePath).Length > 1048576)
                throw new SafeFailure("evaluation_configuration_unavailable");
            // Intentionally avoid Configuration.Load(), which may migrate and write the user's file.
            var configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Configuration.FilePath), Configuration.Json)
                ?? throw new SafeFailure("config_invalid");
            var scene = configuration.Scenes.FirstOrDefault(s => s.Id == "coding") ?? throw new SafeFailure("scene_missing");
            var configured = SceneRequestFactory.SelectBackend(configuration, scene);
            var backend = configured with { MaxTokens = Math.Min(configured.MaxTokens, 16384) };
            var session = Fixture(fixture);
            var options = new CodingRunOptions(MaximumCalls: 12, MaximumSeconds: 300, MaximumOutputChars: 180000);
            var tracedModel = new TracedModel(new AgentModelClient(new CredentialStore()));
            var runner = new CodingAgentRunner(tracedModel);
            var updates = new List<object>();
            var watch = Stopwatch.StartNew();
            runner.Updated += update =>
            {
                // This explicit evaluation contains only synthetic task material.
                updates.Add(new { state = update.State.ToString(), update.Calls, update.Message, elapsedSeconds = watch.Elapsed.TotalSeconds });
                Console.WriteLine($"CODING LIVE: state={update.State}, calls={update.Calls}, seconds={watch.Elapsed.TotalSeconds:F1}, status={update.Message}");
            };
            Console.WriteLine($"CODING LIVE: synthetic={fixture}, transport=tool_calls, tokens={backend.MaxTokens}, maxCalls={options.MaximumCalls}");
            await runner.RunAsync(session, backend, options);
            var directive = runner.Latest?.Directive;
            var text = directive is null ? "" : directive.Direction + "\n" + directive.Prompt;
            bool Has(params string[] terms) => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
            var heuristic = fixture switch
            {
                "complexity" => Has("归并", "merge sort", "mergesort", "merge_sort", "Fenwick", "树状数组", "O(n log n)", "O(nlogn)", "O(n log(n))"),
                "waiting" => Has("deferred.resolve", "resolve(", "settle", "兑现", "未完成", "未解决", "永不", "unresolved", "resolve 被", "resolve没有"),
                "missing" => Has("count_inversions", "完整函数", "函数实现", "source", "当前代码", "完整代码"),
                _ => false
            };
            var completed = runner.Latest?.State == CodingRunState.AwaitingFeedback && directive is not null;
            var artifact = new
            {
                syntheticFixture = fixture, transport = "tool_calls", model = backend.Model,
                options = new { backend.MaxTokens, backend.TimeoutSeconds, options.MaximumCalls, options.MaximumSeconds, options.MaximumOutputChars },
                state = runner.Latest?.State.ToString(), calls = runner.Latest?.Calls,
                elapsedSeconds = watch.Elapsed.TotalSeconds, published = completed, heuristicDirectionMatch = heuristic,
                limitation = "One synthetic model run only; keyword checks are triage hints, not correctness proof or real IDE execution.",
                directive, states = updates, actions = tracedModel.Actions
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                await JsonSerializer.SerializeAsync(stream, artifact, new JsonSerializerOptions(CodingJson.Options) { WriteIndented = true });
            Console.WriteLine($"CODING LIVE: published={completed}, heuristicDirectionMatch={heuristic}, final={CodingJson.Serialize(directive)}");
            return completed && heuristic ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.WriteLine("CODING LIVE: " + (error is SafeFailure failure ? failure.Code : "evaluation_failed"));
            return 1;
        }
    }

    private sealed class TracedModel(IAgentModelClient inner) : IAgentModelClient
    {
        internal List<object> Actions { get; } = [];
        public async Task<AgentAction> NextAsync(AgentModelRequest request, CancellationToken token)
        {
            var errors = request.Messages.Where(m => m.Role == AgentRole.Tool).Select(m => m.Content.Text ?? "")
                .Select(text => JsonDocument.Parse(text)).ToArray();
            var precedingToolErrors = errors.Where(d => d.RootElement.TryGetProperty("error", out _))
                .Select(d => d.RootElement.GetProperty("error").GetString()).ToArray();
            foreach (var document in errors) document.Dispose();
            AgentAction action;
            try { action = await inner.NextAsync(request, token); }
            catch (SafeFailure failure)
            {
                Actions.Add(new { error = failure.Code, checkpointMessages = request.Messages.Length });
                throw;
            }
            Actions.Add(new { action.Name, action.Arguments, precedingToolErrors });
            Console.WriteLine($"CODING LIVE ACTION: {action.Name}; precedingErrors={string.Join(',', precedingToolErrors)}");
            return action;
        }
    }

    private static CodingSession Fixture(string name)
    {
        var session = new CodingSession();
        Observation Add(string text) => session.AddObservation(new(Guid.NewGuid(), InputKind.ClipboardText,
            Payload.FromText(text), 0, DateTimeOffset.UtcNow));
        EvidenceRef Ref(Observation observation) => new(observation.Id, 0, observation.Snapshot.Content.Text!.Length);
        if (name == "missing")
        {
            Add("合成训练题：实现 Python count_inversions(values)，返回 i<j 且 values[i]>values[j] 的对数。n<=200000，1秒，256MB；重复值不算逆序，不能修改输入。样例 [2,1,1] => 2。平台反馈只有：大数据超时。当前代码尚未提供，也没有文件树或路径。用户只负责转发。请取得定位瓶颈必需的当前源码和对应测试反馈，不要让用户自己诊断。");
            return session;
        }
        if (name == "waiting")
        {
            Add("""
合成训练题：实现 TypeScript waitForValue 返回 Promise<number>。没有网络，没有 I/O，最多 3 个值。测试必须完成，不允许使用轮询和随意延长超时。
当前全部代码（路径未知）：
type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void };
function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}
export async function waitForValue(value: number): Promise<number> {
  const deferred = createDeferred<number>();
  return await deferred.promise;
}
测试原文：await expect(waitForValue(7)).resolves.toBe(7); test timed out after 1000 ms.
平台尝试把局部变量改为 const 并将测试超时改为 3000 ms，还是超时。测试规模恒定为 1，不存在大量元素扫描。用户没有判断原因，请后端分析并给精确修复和验证。
""");
            return session;
        }
        var task = Add("""
合成公开算法练习：实现 Python 3 count_inversions(values: list[int]) -> int。
统计满足 0 <= i < j < n 且 values[i] > values[j] 的下标对数。n <= 200000，值范围 [-10^9,10^9]；1 秒，256MB。
重复值不算逆序，不能修改输入列表。空列表返回 0；[2,1,1] 返回 2；[1,2,3] 返回 0；逆序 200000 个元素结果 19999900000。
当前完整函数，路径未知：
def count_inversions(values: list[int]) -> int:
    count = 0
    n = len(values)
    for i in range(n):
        left = values[i]
        for j in range(i + 1, n):
            count += int(left > values[j])
    return count
""");
        Directive Step(string prompt) => new("解决大规模测试超时", "定位 count_inversions 函数", prompt,
            "返回当前完整函数及固定 n=200000 逆序输入的测试结果", "large-input-timeout", "constant-factor-optimization",
            "相同输入应在限制内返回精确结果", [Ref(task)]);
        var first = session.Commit(session.Freeze(), new([new("task", "contract", "逆序对计数，n<=200000，1秒；重复不计入，不修改输入。", [Ref(task)])], []),
            Step("把列表访问保存为局部变量 left，保留双重循环，运行固定大规模测试。"));
        var log1 = Add("合成测试执行 A，运行 id A-101：平台已把 values[i] 缓存为 left，仍保留双重循环。执行固定 n=200000 逆序数组，1 秒上限。结果 TIME_LIMIT_EXCEEDED。样例 [2,1,1] 返回 2。未通过大规模测试。");
        var assessment1 = Assessment(first.Id, Ref(task), Ref(log1));
        var second = session.Commit(session.Freeze(), new([], [assessment1]), Step("将条件累加换为 int(left > values[j])，保留双重循环，再运行相同测试。"));
        var log2 = Add("合成测试执行 B，运行 id B-202：平台已将累加替换成 count += int(left > values[j])，完整函数见题目材料。执行与 A 相同 n=200000 逆序数组，1 秒上限。结果 TIME_LIMIT_EXCEEDED。小规模样例仍通过。两次均无性能进展。");
        // The third pending step only gathers evidence; two earlier failed edits are independently recorded.
        session.Commit(session.Freeze(), new([], [Assessment(second.Id, Ref(task), Ref(log2))]),
            Step("不要继续修改，返回目前 count_inversions 完整函数以及刚才同规模测试日志。"));
        Add("取证指令已执行：上面提供的 count_inversions 完整函数就是目前 IDE 中的函数，当前仍使用双重循环和 int 累加。最新固定规模测试就是运行 B-202，没有新的优化或测试。请后端决定下一步具体修改。");
        return session;
    }

    private static AttemptAssessment Assessment(Guid attemptId, EvidenceRef task, EvidenceRef feedback) =>
        new(attemptId, "large-input-timeout", "constant-factor-optimization", AttemptOutcome.Unchanged,
            Comparable: true, ExecutionObserved: true, Evidence: [task, feedback], FeedbackObservationIds: [feedback.ObservationId]);
}
