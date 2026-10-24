namespace Superb.Core.Coding;

/// <summary>Execution boundaries travel with the copied prompt and are validated with it.</summary>
public static class CodingDirectivePolicy
{
    public const string ExecutionBoundary = "执行边界：代码修改与测试由平台 AI 完成，用户只负责转发。依据已有实现修改前，先核对当前实现和接口是否与上述依据一致；不一致则停止修改，返回当前完整源码及差异，等待后端重新判断。若本条仅要求取证，返回材料后停止，不修改代码。";

    public static Directive Prepare(Directive directive)
    {
        if (string.IsNullOrWhiteSpace(directive.Prompt) || directive.Prompt.EndsWith(ExecutionBoundary, StringComparison.Ordinal))
            return directive;
        return directive with { Prompt = directive.Prompt + "\n\n" + ExecutionBoundary };
    }
}
