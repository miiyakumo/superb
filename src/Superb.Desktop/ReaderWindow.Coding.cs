using System.Windows;
using System.Windows.Controls;
using Superb.Core.Coding;

namespace Superb.Desktop;

public partial class ReaderWindow
{
    private bool codingMode, codingAcceptsInput;
    internal CodingRunOptions CodingOptions => new(
        CodingProtocol.SelectedIndex == 1 ? AgentProtocol.NativeTools : AgentProtocol.JsonActions,
        int.Parse((string)((ComboBoxItem)CodingCalls.SelectedItem).Content),
        int.Parse((string)((ComboBoxItem)CodingSeconds.SelectedItem).Content));

    internal void SetCodingMode(bool enabled, bool acceptsInput, int pending, IEnumerable<ActionAttempt> attempts)
    {
        codingMode = enabled; codingAcceptsInput = acceptsInput;
        CodingControls.Visibility = ClipboardAnalyzeButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ClipboardAnalyzeButton.IsEnabled = ready && !busy;
        SendButton.Content = enabled ? "分析" : "发送";
        CopyButton.Content = enabled ? "复制平台提示词" : copyingPartial ? "复制部分回答" : "复制回答";
        ScreenSendButton.Content = enabled ? "截屏并分析" : "截屏并发送";
        ScreenSendButton.ToolTip = enabled ? "采集本次截图，连同待分析材料加入当前 Coding 任务；不会清空任务记忆。" :
            "仅发送本次截图，使用当前场景与模型；替换旧队列和问题。采集时临时隐藏 Superb。";
        RetryButton.Content = enabled ? "重试分析" : "重答";
        RetryButton.ToolTip = enabled ? "仅重试失败或停止的分析，沿用冻结的材料、记忆、模型与预算；待分析的新材料保持在队列。" :
            "沿用上次冻结的输入、场景、模型与设置。";
        if (enabled)
        {
            ClipboardButton.IsEnabled = InputQueue.IsEnabled = ClearInputButton.IsEnabled = ready && (!busy || acceptsInput);
            CodingPending.Text = pending > 0 ? $"{pending} 项待分析材料 · 尚未纳入当前指令" : "代码来自复制的片段；采集时间不代表文件版本。";
            CodingHistory.Text = string.Join("\n", attempts.Select((a, i) =>
                $"{i + 1}. {a.Directive.Direction} · {(a.Copied ? "已复制" : "已生成")} · {Outcome(a.Assessment?.Outcome)}"));
        }
        else CodingHistory.Text = "";
        SetEditingControls();
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
