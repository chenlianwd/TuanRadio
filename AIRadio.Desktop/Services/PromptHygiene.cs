namespace AIRadio.Desktop.Services;

using System;

/// <summary>提示词卫生：第三方元数据进 LLM 提示词前的协议标记消毒（纵深防御）。</summary>
internal static class PromptHygiene
{
    /// <summary>
    /// 剥除曲目元数据中的 DJ 控制块标记字面量并压平换行：恶意/异常音源可借标题把 &lt;cmd&gt; 块
    /// 带进提示词、诱导模型在聊天回复中回显控制块，或借换行伪造提示词的列表条目/截断段落
    /// （如标题尾接 "\n- …" 冒充现场记忆条目；命令词表封闭、影响有界，属纵深防御而非漏洞修复）。
    /// 标记不在则仅压平换行。
    /// </summary>
    public static string StripControlMarkers(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("<cmd>", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("</cmd>", " ", StringComparison.OrdinalIgnoreCase)
                .ReplaceLineEndings(" ");
}
