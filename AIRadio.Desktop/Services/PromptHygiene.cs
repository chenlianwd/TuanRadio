namespace AIRadio.Desktop.Services;

using System;

/// <summary>提示词卫生：第三方元数据进 LLM 提示词前的协议标记消毒（纵深防御）。</summary>
internal static class PromptHygiene
{
    /// <summary>
    /// 剥除曲目元数据中的 DJ 控制块标记字面量：恶意/异常音源可借标题把 &lt;cmd&gt; 块
    /// 带进提示词、诱导模型在聊天回复中回显控制块（命令词表封闭、影响有界，属纵深
    /// 防御而非漏洞修复）。标记不在则原文返回。
    /// </summary>
    public static string StripControlMarkers(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("<cmd>", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("</cmd>", " ", StringComparison.OrdinalIgnoreCase);
}
