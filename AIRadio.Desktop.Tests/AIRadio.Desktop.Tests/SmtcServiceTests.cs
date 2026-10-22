using System;
using System.Runtime.InteropServices;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using Windows.Media;
using Windows.Storage.Streams;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// SMTC 集成语义锚定：状态映射与按钮分发纯逻辑直驱（HandleButton 不依赖真实 SMTC
/// 实例）、timeline 推送节流决策直测，以及真实 Win32 窗口上的 SMTC 激活冒烟
/// （对齐 KillOnCloseJobTests 真句柄先例；无交互会话环境经 SkippableFact 跳过）。
/// </summary>
public class SmtcServiceTests
{
    [Theory]
    [InlineData(PlaybackState.Playing, MediaPlaybackStatus.Playing)]
    [InlineData(PlaybackState.Paused, MediaPlaybackStatus.Paused)]
    [InlineData(PlaybackState.Stopped, MediaPlaybackStatus.Stopped)]
    [InlineData(PlaybackState.Ended, MediaPlaybackStatus.Stopped)] // Ended 归并 Stopped
    public void MapPlaybackStatus_MapsPlayerStates(PlaybackState state, MediaPlaybackStatus expected)
    {
        Assert.Equal(expected, SmtcService.MapPlaybackStatus(state));
    }

    [Fact]
    public void HandleButton_DispatchesEnabledButtonsToAudioService()
    {
        var audio = new Mock<IAudioService>();
        var sut = new SmtcService(audio.Object);

        sut.HandleButton(SystemMediaTransportControlsButton.Play);
        audio.Verify(a => a.Play(), Times.Once);

        sut.HandleButton(SystemMediaTransportControlsButton.Pause);
        audio.Verify(a => a.Pause(), Times.Once);

        sut.HandleButton(SystemMediaTransportControlsButton.Next);
        audio.Verify(a => a.Next(), Times.Once);

        sut.HandleButton(SystemMediaTransportControlsButton.Previous);
        audio.Verify(a => a.Previous(), Times.Once);
    }

    [Fact]
    public void HandleButton_IgnoresUnhandledButtons()
    {
        var audio = new Mock<IAudioService>();
        var sut = new SmtcService(audio.Object);

        // 未 enable 的按钮（Stop/Record/FastForward...）不得触碰播放面
        sut.HandleButton(SystemMediaTransportControlsButton.Stop);
        sut.HandleButton(SystemMediaTransportControlsButton.Record);
        sut.HandleButton(SystemMediaTransportControlsButton.ChannelDown);

        audio.Verify(a => a.Play(), Times.Never);
        audio.Verify(a => a.Pause(), Times.Never);
        audio.Verify(a => a.Next(), Times.Never);
        audio.Verify(a => a.Previous(), Times.Never);
        audio.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0.5, 0.4, 0, false)]   // 正常推进（微小抖动）不推
    [InlineData(10.0, 1.0, 0, true)]   // 偏差超阈（Seek 发生）立即推
    [InlineData(1.4, 0.0, 0, false)]   // 偏差恰在阈内不推（1.5s 边界内侧）
    [InlineData(1.6, 0.0, 0, true)]    // 偏差超阈推（1.5s 边界外侧）
    [InlineData(0.5, 0.0, 10, true)]   // 事件计数到周期兜底（≈5s）推
    [InlineData(0.5, 0.0, 9, false)]   // 未到计数不推
    public void ShouldPushTimeline_DecidesByDriftAndPeriodicCount(
        double positionSec, double expectedSec, int eventsSinceLastPush, bool expected)
    {
        Assert.Equal(expected, SmtcService.ShouldPushTimeline(
            TimeSpan.FromSeconds(positionSec),
            TimeSpan.FromSeconds(expectedSec),
            eventsSinceLastPush));
    }

    [Fact]
    public void Dispose_WithoutAttach_IsSafeNoOp()
    {
        var sut = new SmtcService(new Mock<IAudioService>().Object);
        sut.Dispose();
        sut.Dispose(); // 幂等
    }

    /// <summary>封面字节往返锚定：BuildThumbnail 静默吞异常，若 DataWriter 用法有误会以"空封面"伪装成功。</summary>
    [Fact]
    public async Task BuildThumbnail_RoundTripsCoverBytes()
    {
        var cover = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7, 8 };

        var reference = SmtcService.BuildThumbnail(cover);

        Assert.NotNull(reference);
        using var stream = await reference!.OpenReadAsync().AsTask();
        using var reader = new DataReader(stream);
        var size = (int)stream.Size;
        await reader.LoadAsync((uint)size).AsTask();
        var read = new byte[size];
        reader.ReadBytes(read);
        Assert.Equal(cover, read);
    }

    /// <summary>真实激活冒烟：文档要求 GetForWindow 接受"调用进程自有的顶层窗口"，用隐藏的 STATIC 类顶层窗口满足（不用 message-only——其不属于普通窗口层级）。</summary>
    [SkippableFact]
    public void GetForWindow_WithRealTopLevelWindow_ReturnsUsableControls()
    {
        var hwnd = CreateWindowExW(0, "STATIC", "smtc-smoke", 0 /* WS_OVERLAPPED */,
            0, 0, 120, 80, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Skip.If(hwnd == IntPtr.Zero, $"无法创建 Win32 测试窗口（Win32Error={Marshal.GetLastWin32Error()}）");
        try
        {
            SystemMediaTransportControls smtc;
            try
            {
                smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            }
            catch (Exception ex)
            {
                Skip.If(true, $"SMTC 激活不可用（无交互会话环境）: {ex.Message}");
                return;
            }

            Assert.NotNull(smtc);
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
            smtc.DisplayUpdater.MusicProperties.Title = "smoke";
            Assert.Equal("smoke", smtc.DisplayUpdater.MusicProperties.Title);
            smtc.DisplayUpdater.Update(); // 属性链写读回 + 提交不抛即冒烟通过
            smtc.IsEnabled = false;
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    // CharSet.Unicode 必须显式声明：W 后缀 API 收 UTF-16，默认 ANSI 封送会把类名变乱码（错误 1407 找不到窗口类）
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
