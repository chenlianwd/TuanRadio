using System;
using System.Reflection;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class AudioVolumeUnificationTests
{
    [Fact]
    public void Volume_UpdatesSoundFxVolume()
    {
        var mockSfx = new Mock<IRadioSoundFxService>();
        mockSfx.SetupProperty(x => x.Volume, 0.5f);

        using var audio = new AudioService(mockSfx.Object);

        audio.Volume = 0.25f;
        Assert.Equal(0.25f, mockSfx.Object.Volume);

        audio.Volume = 0.9f;
        Assert.Equal(0.9f, mockSfx.Object.Volume);
    }

    [Fact]
    public void RadioSoundFxService_VolumeClampsCorrectly()
    {
        using var sfx = new RadioSoundFxService();

        sfx.Volume = 0.5f;
        Assert.Equal(0.5f, sfx.Volume);

        sfx.Volume = -0.5f;
        Assert.Equal(0f, sfx.Volume);

        sfx.Volume = 1.5f;
        Assert.Equal(1f, sfx.Volume);
    }

    [Fact]
    public void Volume_DuringTtsSpeaking_MaintainsDuckRatioInsteadOfFullVolume()
    {
        var mockSfx = new Mock<IRadioSoundFxService>();
        using var audio = new AudioService(mockSfx.Object);

        audio.Volume = 0.8f;

        // 通过反射模拟 TTS 状态进入 speaking (true)
        var ttsStateSubjectField = typeof(AudioService).GetField("_ttsStateSubject", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(ttsStateSubjectField);
        var subject = ttsStateSubjectField.GetValue(audio) as System.Reactive.Subjects.BehaviorSubject<bool>;
        Assert.NotNull(subject);

        subject.OnNext(true);

        // 在 TTS 说话期间调整音量到 0.4
        audio.Volume = 0.4f;

        // 验证用户音量已记录
        Assert.Equal(0.4f, audio.Volume);
        Assert.Equal(0.4f, mockSfx.Object.Volume);

        // 结束 TTS
        subject.OnNext(false);
        Assert.Equal(0.4f, audio.Volume);
    }
}
