using System.Windows;
using System.Windows.Media;
using Superb.Core;
using Superb.Core.Coding;

namespace Superb.Desktop;

public partial class ReaderWindow
{
    private bool codingMode;
    private CodingRunUpdate? codingProgressUpdate;
    private readonly Queue<string> codingActivity = new();
    internal void SetCodingMode(bool enabled, bool acceptsInput, int pending, IEnumerable<ActionAttempt> attempts)
    {
        codingMode = enabled;
        CodingProgressPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        CodingPending.Visibility = CodingHistorySection.Visibility = ClipboardAnalyzeButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ClipboardAnalyzeButton.IsEnabled = ready && !busy;
        SendButton.Content = enabled ? "分析任务" : "发送";
        SendButton.ToolTip = enabled ? "分析当前输入与待处理材料，结合已有任务记录生成下一步执行指令。" : "发送当前问题和队列中的材料。";
        CopyButton.Content = enabled ? "复制执行指令" : copyingPartial ? "复制部分回答" : "复制回答";
        ScreenSendButton.Content = enabled ? "截屏并分析" : "截屏并发送";
        ScreenSendButton.ToolTip = enabled ? "采集本次截图，连同待分析材料加入当前 Coding 任务；不会清空任务记忆。" :
            "仅发送本次截图，使用当前场景与模型；替换旧队列和问题。采集时临时隐藏 Superb。";
        RetryButton.Content = enabled ? "重试任务分析" : "重答";
        StopButton.Content = enabled ? "停止分析" : "停止生成";
        RetryButton.ToolTip = enabled ? "从上次完整工具步骤继续，沿用冻结材料、会话、模型与预算；新材料保持在待分析队列。" :
            "沿用上次冻结的输入、场景、模型与设置。";
        if (enabled)
        {
            ClipboardButton.IsEnabled = InputQueue.IsEnabled = ClearInputButton.IsEnabled = ready && (!busy || acceptsInput);
            CodingPending.Text = pending > 0 ? $"{pending} 项待分析材料 · 尚未纳入当前指令" : "代码来自复制的片段；采集时间不代表文件版本。";
            CodingHistory.Text = string.Join("\n", attempts.Select((a, i) =>
                $"{i + 1}. {a.Directive.Direction} · {(a.Copied ? "已复制" : "已生成")} · {Outcome(a.Assessment?.Outcome)}"));
        }
        else CodingHistory.Text = "";
        UpdateControls();
    }

    internal void ClearCodingProgress()
    {
        codingProgressUpdate = null; codingActivity.Clear();
        CodingProgressState.Text = "等待任务材料"; CodingProgressMetrics.Text = "";
        CodingProgressError.Text = CodingActivityText.Text = "";
        CodingProgressError.Visibility = Visibility.Collapsed;
        CodingProgressState.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
    }

    internal void RenderCodingProgress(CodingRunUpdate? update, bool previousDirective = false, bool loggingAvailable = true, long extraMilliseconds = 0)
    {
        if (!Check()) return;
        if (update is null) { ClearCodingProgress(); return; }
        if (codingProgressUpdate?.RunId != update.RunId)
        {
            codingActivity.Clear(); BeginAnswer(); ComposerExpander.IsExpanded = false;
        }
        if (!ReferenceEquals(codingProgressUpdate, update))
        {
            var message = update.State == CodingRunState.Paused ? "分析暂停：" + update.ErrorCode : update.State == CodingRunState.Failed ? "分析失败：" + (update.ErrorCode ?? update.Message) : update.Message;
            codingActivity.Enqueue($"{update.ElapsedMilliseconds / 1000.0:F1}s  {message}");
            while (codingActivity.Count > 32) codingActivity.Dequeue();
            CodingActivityText.Text = string.Join("\n", codingActivity);
            CodingTraceScroll.ScrollToBottom();
            codingProgressUpdate = update;
        }
        var terminal = update.State is CodingRunState.Failed or CodingRunState.Cancelled or CodingRunState.Paused or CodingRunState.AwaitingFeedback;
        var elapsed = update.ElapsedMilliseconds + (terminal ? 0 : extraMilliseconds);
        var stage = update.State switch
        {
            CodingRunState.Failed => "本次分析失败", CodingRunState.Cancelled => "本次分析已停止",
            CodingRunState.AwaitingFeedback => "本次分析完成 · 等待执行反馈", CodingRunState.Paused => "材料未取得新进展 · 已暂停，可继续",
            CodingRunState.Retrieving => "正在检索材料", _ => "正在分析任务"
        };
        CodingProgressState.Text = stage + (previousDirective && update.State != CodingRunState.AwaitingFeedback ? " · 下方为上一轮指令" : "");
        if (update.State is CodingRunState.Failed or CodingRunState.Paused) CodingProgressState.Foreground = Brushes.Salmon;
        else CodingProgressState.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        var waiting = update.WaitingForModel && !terminal
            ? $" · 当前请求等待 {(update.CallElapsedMilliseconds + extraMilliseconds) / 1000.0:F0} 秒" : "";
        CodingProgressMetrics.Text = $"已用 {elapsed / 1000.0:F0} 秒 · {update.Calls} 次模型调用{waiting} · 运行 {update.RunId.ToString("N")[..8]}";
        if (update.Transport is { } transport)
            CodingProgressMetrics.Text += "\n" + CodingTransportTrace.Describe(transport, update.WaitingForModel && !terminal ? extraMilliseconds : 0);
        CodingProgressError.Text = update.State is CodingRunState.Failed or CodingRunState.Paused
            ? ConnectionDiagnostics.Explain(update.ErrorCode ?? update.Message) + "\n错误码：" + (update.ErrorCode ?? update.Message) + "；可点击“重试任务分析”，或调整程序设置后重新分析。"
            : !loggingAvailable ? "诊断日志暂时无法写入；本次分析仍继续。" : "";
        if (update.State == CodingRunState.Failed && (update.ErrorCode is "request_timed_out" or "request_timeout" or "coding_time_budget_exhausted") && update.Transport is { } failedTransport)
            CodingProgressError.Text += "\n中止位置：" + CodingTransportTrace.StageLabel(failedTransport.Stage) +
                (failedTransport.Stage == CodingTransportStage.WaitingForResponseHeaders
                    ? "（请求已上传，尚未收到完整响应头；服务端排队与计算需服务端指标区分）" : "");
        CodingProgressError.Foreground = Brushes.Salmon;
        CodingProgressError.Visibility = CodingProgressError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Outcome(AttemptOutcome? outcome) => outcome switch
    {
        AttemptOutcome.Claimed => "平台声称完成", AttemptOutcome.Improved => "有改善",
        AttemptOutcome.Unchanged => "未改善", AttemptOutcome.Regressed => "回退",
        AttemptOutcome.Verified => "已有验证证据", AttemptOutcome.Incomparable => "结果不可比较", _ => "等待执行证据"
    };

    internal void RenderDirective(Directive directive, string state)
    {
        Render($"**下一步方向**\n\n{directive.Direction}\n\n**发送前操作**\n\n{directive.BeforeSend}\n\n" +
            $"**发给平台 AI**\n\n{directive.Prompt}\n\n**执行后带回**\n\n{directive.ReturnEvidence}", state);
    }
    private void ClipboardAnalyze_Click(object sender, RoutedEventArgs e) => Command?.Invoke("clipboardAnalyze");
}
