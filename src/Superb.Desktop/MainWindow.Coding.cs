using Superb.Core;
using Superb.Core.Coding;
using System.Diagnostics;
using System.Windows.Threading;

namespace Superb.Desktop;

public partial class MainWindow
{
    private readonly CodingSession codingSession = new();
    private readonly CodingAgentRunner codingRunner;
    private readonly CodingDiagnosticLog? codingDiagnosticLog;
    private readonly DispatcherTimer codingProgressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch codingDisplayClock = new();
    private Backend? codingTarget;
    private uint codingClipboardSequence;
    internal CodingSession WorkspaceCodingSession => codingSession;
    internal CodingAgentRunner WorkspaceCodingRunner => codingRunner;
    private Directive? CodingDirective => codingSession.Memory.Attempts.LastOrDefault()?.Directive;
    private bool CanReanalyzeCoding => codingSession.Observations.Count > 0 &&
        codingRunner.Latest?.State is CodingRunState.Failed or CodingRunState.Cancelled or CodingRunState.Paused;

    private async Task SendCodingWorkspace(Backend? frozenBackend = null, string? frozenInstructions = null, CodingRunOptions? frozenOptions = null)
    {
        if (!Ready()) return;
        if (Working) { Status("正在分析；新材料可继续加入待分析队列"); return; }
        var hasPending = codingWorkspace.HasInput("");
        var reanalyzing = CanReanalyzeCoding;
        if (!hasPending && !reanalyzing) { Status("请带回新的代码或测试反馈，再继续分析"); return; }
        var target = frozenBackend ?? SceneRequestFactory.SelectBackend(config, WorkspaceScene);
        Policy.ValidateBackend(target);
        var options = frozenOptions ?? config.CodingAnalysis;
        options.Validate();
        var instructions = frozenInstructions ?? WorkspaceScene?.SystemPrompt ?? "";
        // Validate the complete pending packet before consuming any of it. Each original
        // capture remains an independent observation with its own provenance.
        if (hasPending)
        {
            var packet = codingWorkspace.Freeze("", clipboard!.Sequence);
            Policy.Check(new(packet, packet.Content, target, instructions, "coding"));
        }
        codingClipboardSequence = clipboard!.Sequence;
        var observationCount = codingSession.Observations.Count;
        var observations = codingWorkspace.Items.Select(entry => entry.Snapshot).ToList();
        codingSession.AddObservations(observations);
        codingWorkspace.Clear(); reader.ClearInput();
        if (codingSession.Observations.Count == observationCount && !reanalyzing)
        { Status("材料与已有记录重复 · 未再次调用模型；请带回新反馈或重试未完成的分析"); return; }
        reader.SetClickThrough(false);
        if (!reader.IsVisible) reader.Show();
        codingTarget = target;
        await codingRunner.RunAsync(codingSession, target, options, instructions);
        RefreshWorkspace();
    }

    private async Task RetryCoding()
    {
        if (!Ready() || Working) return;
        if (!codingRunner.CanRetry) { Status("当前没有可重试的未完成分析；请带回新反馈继续"); return; }
        // Retry deliberately excludes newly queued observations and settings.
        await codingRunner.RetryAsync(codingSession);
        RefreshWorkspace();
    }

    private void OnCodingUpdate(CodingRunUpdate update)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnCodingUpdate(update)); return; }
        if (!ReferenceEquals(codingRunner.Latest, update) || !IsCoding || coordinator.Mode != RunState.Ready) return;
        if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return; }
        codingDisplayClock.Restart();
        if (update.State is CodingRunState.Failed or CodingRunState.Cancelled or CodingRunState.Paused or CodingRunState.AwaitingFeedback) codingProgressTimer.Stop();
        else codingProgressTimer.Start();
        RenderCoding();
        RefreshWorkspace();
        if (update.State != CodingRunState.AwaitingFeedback || update.Directive is null || !config.AutoCopy) return;
        var attempt = codingSession.Memory.Attempts.LastOrDefault();
        if (attempt is null) return;
        try
        {
            if (clipboard!.Write(attempt.Directive.Prompt, attempt.Id, codingSession.Memory.Revision, codingClipboardSequence))
            { codingSession.MarkCopied(attempt.Id); Status("指令已复制 · 转发给平台 AI，执行后带回反馈"); }
            else Status("指令已生成 · 剪贴板已变化，跳过自动写回");
        }
        catch (Exception ex) { Report(ex); }
    }

    private void RenderCoding()
    {
        var status = codingRunner.Latest is { } update
            ? $"{(update.State is CodingRunState.Failed or CodingRunState.Paused ? ConnectionDiagnostics.Explain(update.Message) : update.Message)} · {update.Calls} 次调用" : "等待题目、当前代码和测试反馈";
        StatusLabel.Text = status;
        SetError(codingRunner.Latest is { State: CodingRunState.Failed or CodingRunState.Paused } failed ? failed.Message : null);
        reader.RenderCodingProgress(codingRunner.Latest, CodingDirective is not null, codingDiagnosticLog?.Available ?? true);
        if (CodingDirective is { } directive) reader.RenderDirective(directive, status);
        else reader.Render("", status);
    }

    private void RefreshCodingClock()
    {
        if (!IsCoding || !codingRunner.IsRunning || coordinator.Mode != RunState.Ready)
        { codingProgressTimer.Stop(); return; }
        if (!protection.Check() || !reader.Check()) { ProtectionFailed(); return; }
        reader.RenderCodingProgress(codingRunner.Latest, CodingDirective is not null, codingDiagnosticLog?.Available ?? true,
            codingDisplayClock.ElapsedMilliseconds);
    }

    private void ReportCodingStartFailure(Exception ex)
    {
        var code = CodingDiagnostics.SafeCode(ConnectionDiagnostics.CodeFor(ex))!;
        var runId = Guid.NewGuid();
        codingDiagnosticLog?.Write(new(runId, 1, DateTimeOffset.UtcNow, CodingDiagnosticKind.Failed,
            CodingRunState.Failed, 0, 0, ErrorCode: code));
        Report(ex);
        reader.RenderCodingProgress(new(runId, CodingRunState.Failed, 0, code, ErrorCode: code), CodingDirective is not null,
            codingDiagnosticLog?.Available ?? true);
    }

    private void CopyCodingDirective()
    {
        if (!Ready() || Working || codingSession.Memory.Attempts.LastOrDefault() is not { } attempt) return;
        clipboard!.Write(attempt.Directive.Prompt, attempt.Id, codingSession.Memory.Revision, null);
        codingSession.MarkCopied(attempt.Id);
        Status("平台提示词已复制 · 复制不代表平台已经执行");
    }
}
