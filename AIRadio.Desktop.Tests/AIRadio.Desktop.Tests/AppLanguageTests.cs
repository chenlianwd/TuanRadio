using System;
using System.Linq;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Xunit;

// AppLanguage 是进程级全局状态，语言切换测试会瞬时改变全Assembly的默认语言；
// 关闭测试类并行，避免其他断言中文文案的测试与其竞态
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 界面语言接线测试：AppLanguage 切换语义、zh/en 字符串表键集奇偶
/// （XAML 的 DynamicResource 键缺失会静默空白，必须用键集校验兜住）。
/// </summary>
public class AppLanguageTests : IDisposable
{
    public AppLanguageTests()
    {
        // 防御：其他测试若遗留 en 状态，先归位
        AppLanguage.Apply("zh");
    }

    public void Dispose()
    {
        AppLanguage.Apply("zh");
    }

    [Fact]
    public void T_ReturnsZhByDefaultAndEnAfterApply()
    {
        Assert.Equal("搜索", AppLanguage.T("搜索", "Search"));
        Assert.Equal("zh", AppLanguage.Current);

        AppLanguage.Apply("en");

        Assert.Equal("Search", AppLanguage.T("搜索", "Search"));
        Assert.Equal("en", AppLanguage.Current);
    }

    [Fact]
    public void Apply_NormalizesUnknownValuesToZh()
    {
        AppLanguage.Apply("en");
        AppLanguage.Apply("fr");

        Assert.Equal("zh", AppLanguage.Current);
        AppLanguage.Apply((string?)null);
        Assert.Equal("zh", AppLanguage.Current);
    }

    [Fact]
    public void Apply_SameLanguage_IsNoOpAndDoesNotRaiseChanged()
    {
        var raised = 0;
        void OnChanged() => raised++;
        AppLanguage.Changed += OnChanged;
        try
        {
            AppLanguage.Apply("zh");
            Assert.Equal(0, raised);

            AppLanguage.Apply("en");
            Assert.Equal(1, raised);

            AppLanguage.Apply("en");
            Assert.Equal(1, raised);
        }
        finally
        {
            AppLanguage.Changed -= OnChanged;
        }
    }

    [Fact]
    public void StringTables_HaveIdenticalKeySetsAndNonEmptyValues()
    {
        var zhOnly = AppLanguage.ZhStrings.Keys.Except(AppLanguage.EnStrings.Keys).ToList();
        var enOnly = AppLanguage.EnStrings.Keys.Except(AppLanguage.ZhStrings.Keys).ToList();
        Assert.True(zhOnly.Count == 0 && enOnly.Count == 0,
            $"键集不一致: 仅zh={string.Join(",", zhOnly)} 仅en={string.Join(",", enOnly)}");

        Assert.All(AppLanguage.ZhStrings.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.All(AppLanguage.EnStrings.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.NotEmpty(AppLanguage.ZhStrings);
    }

    [Fact]
    public void FormatSourceStatus_FollowsCurrentLanguage()
    {
        var status = new SourceSearchStatus("酷我音乐", "ok", 20, null);

        Assert.Equal("酷我音乐成功20条", PlaylistViewModel.FormatSourceStatus(status));

        AppLanguage.Apply("en");

        Assert.Equal("Kuwo Music: 20 result(s)", PlaylistViewModel.FormatSourceStatus(status));
    }

    [Fact]
    public void ApiFailureLocalization_UsesCurrentLanguage()
    {
        var failure = ApiFailureInfo.FromException(new TimeoutException());

        AppLanguage.Apply("en");
        var localized = ApiFailureLocalization.ForCurrentLanguage(failure);

        Assert.Equal("AI response timed out", localized.Title);
        Assert.DoesNotContain(localized.Detail, character => character is >= '\u4e00' and <= '\u9fff');
    }

    [Fact]
    public void FormatSourceStatus_DisabledAndBrokerNotes_RelocalizeAcrossLanguages()
    {
        var disabled = new SourceSearchStatus("网易云音乐", "disabled", 0, "已在设置中停用");
        var circuitOpen = new SourceSearchStatus("酷狗音乐", "disabled", 0, "连续接口故障，暂停请求 15 秒");
        var filteredNote = new SourceSearchStatus("酷我音乐", "ok", 0, null, "试听或失效片段，已过滤");
        var probeTimeout = new SourceSearchStatus("YouTube", "ok", 2, null, "可播性检查超时，已跳过");

        Assert.Equal("网易云音乐已停用（已在设置中停用）", PlaylistViewModel.FormatSourceStatus(disabled));
        Assert.Equal("酷狗音乐已停用（连续接口故障，暂停请求 15 秒）", PlaylistViewModel.FormatSourceStatus(circuitOpen));

        AppLanguage.Apply("en");

        Assert.Equal("NetEase Cloud Music disabled (Disabled in settings)", PlaylistViewModel.FormatSourceStatus(disabled));
        Assert.Equal("Kugou Music disabled (circuit open for 15s after repeated transport failures)", PlaylistViewModel.FormatSourceStatus(circuitOpen));
        Assert.Equal("Kuwo Music: 0 results (preview or stale clips only, filtered)", PlaylistViewModel.FormatSourceStatus(filteredNote));
        Assert.Equal("YouTube: 2 results (playability check timed out, skipped)", PlaylistViewModel.FormatSourceStatus(probeTimeout));
    }

    [Fact]
    public void CharacterProfile_RefreshLocalizedPresets_RaisesPropertyChanged()
    {
        var profile = AIRadio.Desktop.Models.CharacterProfile.Presets[0];
        var changed = new System.Collections.Generic.List<string?>();
        profile.PropertyChanged += OnChanged;
        try
        {
            AppLanguage.Apply("en");
            AIRadio.Desktop.Models.CharacterProfile.RefreshLocalizedPresets();
            Assert.Contains(nameof(AIRadio.Desktop.Models.CharacterProfile.Description), changed);
            Assert.Contains(nameof(AIRadio.Desktop.Models.CharacterProfile.PersonalityPrompt), changed);
            Assert.DoesNotContain(profile.Description, c => c is >= '\u4e00' and <= '\u9fff');
        }
        finally
        {
            profile.PropertyChanged -= OnChanged;
            AppLanguage.Apply("zh");
            AIRadio.Desktop.Models.CharacterProfile.RefreshLocalizedPresets();
        }

        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            => changed.Add(e.PropertyName);
    }
}
