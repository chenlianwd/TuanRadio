namespace AIRadio.Desktop.Models;

/// <summary>电台整体状态。派生优先级见 spec §5.2.2：Error &gt; Speaking &gt; Searching &gt; Curating &gt; Playing &gt; Idle。</summary>
public enum RadioState
{
    Idle,
    Curating,
    Searching,
    Speaking,
    Playing,
    Error
}
