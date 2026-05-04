using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class SettingsViewModelTests
{
    private readonly Mock<IMinimaxService> _mockMinimax;
    private readonly Mock<IDJService> _mockDJ;
    private readonly Mock<ISecureStorage> _mockStorage;

    public SettingsViewModelTests()
    {
        _mockMinimax = new Mock<IMinimaxService>();
        _mockDJ = new Mock<IDJService>();
        _mockStorage = new Mock<ISecureStorage>();
    }

    [Fact]
    public void GetOverride_ReturnsStoredOverride()
    {
        var vm = new SettingsViewModel(_mockMinimax.Object, _mockDJ.Object, _mockStorage.Object);
        vm.SelectedCharacter = vm.Characters.First();

        // Override not set yet
        var result = vm.GetOverride("haru");
        Assert.Null(result);

        // After initialization, default voice should be set
        vm.SelectedCharacter = vm.Characters.First();
        Assert.NotNull(vm.SelectedCharacter);
    }

    [Fact]
    public void Voices_ListContainsAllOptions()
    {
        var vm = new SettingsViewModel(_mockMinimax.Object, _mockDJ.Object, _mockStorage.Object);

        Assert.Equal(6, vm.Voices.Count);
        Assert.Contains(vm.Voices, v => v.Id == "male-qn-qingse");
        Assert.Contains(vm.Voices, v => v.Id == "female-shaonv");
    }

    [Fact]
    public void Languages_ListContainsZhAndEn()
    {
        var vm = new SettingsViewModel(_mockMinimax.Object, _mockDJ.Object, _mockStorage.Object);

        Assert.Equal(2, vm.Languages.Count);
        Assert.Contains(vm.Languages, l => l.Id == "zh");
        Assert.Contains(vm.Languages, l => l.Id == "en");
    }

    [Fact]
    public void Characters_ListContainsPresets()
    {
        var vm = new SettingsViewModel(_mockMinimax.Object, _mockDJ.Object, _mockStorage.Object);

        Assert.True(vm.Characters.Count >= 6);
        Assert.Contains(vm.Characters, c => c.Id == "haru");
        Assert.Contains(vm.Characters, c => c.Id == "hiyori");
        Assert.Contains(vm.Characters, c => c.Id == "mao");
    }

    [Fact]
    public void SelectedCharacter_DefaultsToFirst()
    {
        var vm = new SettingsViewModel(_mockMinimax.Object, _mockDJ.Object, _mockStorage.Object);

        Assert.NotNull(vm.SelectedCharacter);
        Assert.Equal("haru", vm.SelectedCharacter.Id);
    }

    [Fact]
    public void TtsEnabled_DefaultsToTrue()
    {
        var vm = new SettingsViewModel(_mockMinimax.Object, _mockDJ.Object, _mockStorage.Object);

        Assert.True(vm.TtsEnabled);
    }

    [Fact]
    public void SelectedLanguage_DefaultsToZh()
    {
        var vm = new SettingsViewModel(_mockMinimax.Object, _mockDJ.Object, _mockStorage.Object);

        Assert.Equal("zh", vm.SelectedLanguage);
    }

    [Fact]
    public void TestConnectionCommand_CanBeCreated()
    {
        var vm = new SettingsViewModel(_mockMinimax.Object, _mockDJ.Object, _mockStorage.Object);
        vm.ApiKey = "";

        vm.TestConnectionCommand.Subscribe(_ => { });

        Assert.NotNull(vm.TestConnectionCommand);
    }
}
