using System;
using System.Runtime.InteropServices;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 任务栏缩略图工具栏语义锚定：WM_COMMAND 匹配/按钮分发/按钮布局纯函数直驱，
/// 字形像素与 GDI 图标创建 headless 可跑（纯 Skia/GDI，无窗口依赖），真实 COM
/// 激活冒烟对齐 SmtcServiceTests 真实激活先例（无交互会话经 SkippableFact 跳过）。
/// TryAttach 全链路（wndproc 子类化+真任务栏按钮注册）依赖真实窗口与任务栏，
/// 属实机验收范围，测试不覆盖。
/// </summary>
public class ThumbnailToolbarServiceTests
{
    // MAKELONG：低字 id + 高字通知码，还原 WM_COMMAND 的 wParam 布局
    private static IntPtr MakeWParam(int id, uint notification)
        => (IntPtr)(((ulong)notification << 16) | (uint)id);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void MatchThumbCommand_ResolvesToolbarClicks(int id)
    {
        var resolved = ThumbnailToolbarService.MatchThumbCommand(
            ThumbnailToolbarService.WmCommand,
            MakeWParam(id, ThumbnailToolbarService.ThbnClicked));
        Assert.Equal(id, resolved);
    }

    [Fact]
    public void MatchThumbCommand_RejectsForeignCommands()
    {
        // 非 WM_COMMAND 消息
        Assert.Null(ThumbnailToolbarService.MatchThumbCommand(0x0112,
            MakeWParam(2, ThumbnailToolbarService.ThbnClicked)));
        // WM_COMMAND 但通知码不是 THBN_CLICKED（菜单/控件命令）
        Assert.Null(ThumbnailToolbarService.MatchThumbCommand(
            ThumbnailToolbarService.WmCommand, MakeWParam(2, 0)));
        // 通知码对但 id 超出本工具栏范围
        Assert.Null(ThumbnailToolbarService.MatchThumbCommand(
            ThumbnailToolbarService.WmCommand, MakeWParam(0, ThumbnailToolbarService.ThbnClicked)));
        Assert.Null(ThumbnailToolbarService.MatchThumbCommand(
            ThumbnailToolbarService.WmCommand, MakeWParam(5, ThumbnailToolbarService.ThbnClicked)));
    }

    [Fact]
    public void HandleCommand_DispatchesTransportButtons()
    {
        var audio = new Mock<IAudioService>();
        var sut = new ThumbnailToolbarService(audio.Object);

        Assert.True(sut.HandleCommand(ThumbnailToolbarService.ButtonIdPrevious));
        audio.Verify(a => a.Previous(), Times.Once);

        Assert.True(sut.HandleCommand(ThumbnailToolbarService.ButtonIdNext));
        audio.Verify(a => a.Next(), Times.Once);

        Assert.False(sut.HandleCommand(99));
        audio.VerifyNoOtherCalls();
    }

    [Fact]
    public void HandleCommand_PlayPause_WhenPlaying_Pauses()
    {
        var audio = new Mock<IAudioService>();
        audio.SetupGet(a => a.IsPlaying).Returns(true);
        var sut = new ThumbnailToolbarService(audio.Object);

        Assert.True(sut.HandleCommand(ThumbnailToolbarService.ButtonIdPlayPause));
        audio.VerifyGet(a => a.IsPlaying, Times.Once);
        audio.Verify(a => a.Pause(), Times.Once);
        audio.VerifyNoOtherCalls();
    }

    [Fact]
    public void HandleCommand_PlayPause_WhenPaused_Plays()
    {
        var audio = new Mock<IAudioService>();
        audio.SetupGet(a => a.IsPlaying).Returns(false);
        var sut = new ThumbnailToolbarService(audio.Object);

        Assert.True(sut.HandleCommand(ThumbnailToolbarService.ButtonIdPlayPause));
        audio.VerifyGet(a => a.IsPlaying, Times.Once);
        audio.Verify(a => a.Play(), Times.Once);
        audio.VerifyNoOtherCalls();
    }

    [Fact]
    public void HandleCommand_Favorite_RaisesEventWithoutTouchingAudio()
    {
        var audio = new Mock<IAudioService>();
        var sut = new ThumbnailToolbarService(audio.Object);
        var raised = 0;
        sut.FavoritePressed += () => raised++;

        Assert.True(sut.HandleCommand(ThumbnailToolbarService.ButtonIdFavorite));
        Assert.Equal(1, raised);
        audio.VerifyNoOtherCalls();
    }

    [Fact]
    public void BuildButtons_OrdersIdsIconsAndTooltips()
    {
        var icons = new IntPtr[6];
        for (var i = 0; i < icons.Length; i++)
            icons[i] = new IntPtr(100 + i);

        var buttons = ThumbnailToolbarService.BuildButtons(isPlaying: true, isFavorite: false, icons);

        Assert.Equal(4, buttons.Length);
        Assert.Equal((uint)ThumbnailToolbarService.ButtonIdPrevious, buttons[0].iId);
        Assert.Equal((uint)ThumbnailToolbarService.ButtonIdPlayPause, buttons[1].iId);
        Assert.Equal((uint)ThumbnailToolbarService.ButtonIdNext, buttons[2].iId);
        Assert.Equal((uint)ThumbnailToolbarService.ButtonIdFavorite, buttons[3].iId);
        // 播放中亮"暂停"图标（icons[2]）；未收藏亮空心心形（icons[5]）
        Assert.Equal(icons[2], buttons[1].hIcon);
        Assert.Equal(icons[5], buttons[3].hIcon);
        // tooltip 与服务层同源现算（语言由 AppLanguage 全局态决定，测试跟随不预设）
        Assert.Equal(AppLanguage.T("上一首", "Previous"), buttons[0].szTip);
        Assert.Equal(AppLanguage.T("收藏（同时记入长期口味）", "Favorite (feeds your taste profile)"),
            buttons[3].szTip);

        var paused = ThumbnailToolbarService.BuildButtons(isPlaying: false, isFavorite: true, icons);
        Assert.Equal(icons[1], paused[1].hIcon); // 暂停态亮"播放"
        Assert.Equal(icons[4], paused[3].hIcon); // 已收藏实心
    }

    [Fact]
    public void RenderGlyphPixels_ProducesDistinctNonEmptyGlyphs()
    {
        var renders = new byte[6][];
        for (var i = 0; i < 6; i++)
        {
            renders[i] = ThumbnailToolbarService.RenderGlyphPixels(
                (ThumbnailToolbarService.TransportGlyph)i);
            // 空画布（绘制失效）在此暴露：至少要有非零字节
            Assert.Contains(renders[i], b => b != 0);
        }

        // 字形混同（如播放/暂停画反、心形两态退化）在此暴露
        for (var i = 0; i < 6; i++)
            for (var j = i + 1; j < 6; j++)
                Assert.NotEqual(renders[i], renders[j]);
    }

    [Fact]
    public void CreateIconFromPixels_ReturnsDestroyableIcon()
    {
        var pixels = ThumbnailToolbarService.RenderGlyphPixels(
            ThumbnailToolbarService.TransportGlyph.Play);
        var icon = ThumbnailToolbarService.CreateIconFromPixels(pixels);

        Assert.NotEqual(IntPtr.Zero, icon);
        Assert.True(DestroyIcon(icon));
    }

    [Fact]
    public void SetFavoriteState_WithoutAttach_IsSafeNoOp()
        => new ThumbnailToolbarService(new Mock<IAudioService>().Object).SetFavoriteState(true);

    [Fact]
    public void Dispose_WithoutAttach_IsSafeNoOp()
    {
        var sut = new ThumbnailToolbarService(new Mock<IAudioService>().Object);
        sut.Dispose();
        sut.Dispose(); // 幂等
    }

    /// <summary>
    /// 真实 COM 激活冒烟：TaskbarList CLSID 存在且 HrInit 可调（vtable 槽 0 正确），
    /// 并锚定 ThumbBarUpdateButtons 槽位——vtable 声明错位（UpdateButtons 声明落到
    /// SetImageList 槽）时该调用返回错误 hr 而非 S_OK（盲审探针实证的锚法）。
    /// </summary>
    [SkippableFact]
    public void TaskbarList_ComCreationHrInitAndUpdateSlot_Succeeds()
    {
        try
        {
            var taskbar = (ThumbnailToolbarService.ITaskbarList3)new ThumbnailToolbarService.TaskbarListComObject();
            taskbar.HrInit();
            var dummy = new ThumbnailToolbarService.THUMBBUTTON { iId = 1 };
            var hr = taskbar.ThumbBarUpdateButtons(IntPtr.Zero, 1, new[] { dummy });
            Assert.Equal(0, hr);
        }
        catch (COMException ex)
        {
            Skip.If(true, $"TaskbarList COM 激活不可用（无交互会话环境）: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
