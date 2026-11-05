using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public sealed class AppUpdateServiceTests : IDisposable
{
    private readonly string _tempDir;

    public AppUpdateServiceTests()
    {
        AppLanguage.Apply("zh");
        _tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        AppLanguage.Apply("zh");
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Theory]
    [InlineData("v1.1.0", "1.0.0", 1)]
    [InlineData("1.0.0", "v1.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0", "1.0.0-beta.1", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    public void CompareVersionStrings_HandlesTagsAndSemVerCorrectly(string left, string right, int expectedSign)
    {
        var cmp = AppUpdateService.CompareVersionStrings(left, right);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    [Fact]
    public async Task PortableMode_CheckForUpdates_ReturnsNewerReleaseFromGithubApi()
    {
        const string json = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/chenlianwd/TuanRadio/releases/tag/v1.2.0"
        }
        """;
        using var http = CreateMockHttpClient(HttpStatusCode.OK, json);
        var fakeVpk = new FakeVelopackClient { IsInstalled = false };
        var svc = new AppUpdateService(http, fakeVpk, fallbackVersion: "1.0.0");

        var result = await svc.CheckForUpdatesAsync();

        Assert.False(svc.IsInstalled);
        Assert.Equal("1.0.0", svc.CurrentVersion);
        Assert.True(result.HasUpdate);
        Assert.False(result.CanInstallInApp);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.Equal("https://github.com/chenlianwd/TuanRadio/releases/tag/v1.2.0", result.ReleasePageUrl);
    }

    [Fact]
    public async Task PortableMode_CheckForUpdates_UpToDateWhenTagMatchesCurrent()
    {
        const string json = """
        {
          "tag_name": "v1.0.0",
          "html_url": "https://github.com/chenlianwd/TuanRadio/releases/tag/v1.0.0"
        }
        """;
        using var http = CreateMockHttpClient(HttpStatusCode.OK, json);
        var fakeVpk = new FakeVelopackClient { IsInstalled = false };
        var svc = new AppUpdateService(http, fakeVpk, fallbackVersion: "1.0.0");

        var result = await svc.CheckForUpdatesAsync();

        Assert.False(result.HasUpdate);
        Assert.False(result.CanInstallInApp);
        Assert.Equal("1.0.0", result.LatestVersion);
    }

    [Fact]
    public async Task InstalledMode_FullLifecycle_ChecksDownloadsAndRunsCleanupBeforeRestart()
    {
        using var http = CreateMockHttpClient(HttpStatusCode.OK, "{}");
        var fakeUpdateObj = new object();
        var callOrder = new List<string>();
        var fakeVpk = new FakeVelopackClient
        {
            IsInstalled = true,
            CurrentVersion = "1.0.0",
            NextUpdateObject = fakeUpdateObj,
            TargetVersion = "1.3.0",
            OnApplyRestart = _ => callOrder.Add("restart")
        };
        var svc = new AppUpdateService(
            http,
            fakeVpk,
            beforeRestart: () => callOrder.Add("cleanup"));

        var check = await svc.CheckForUpdatesAsync();
        Assert.True(check.HasUpdate);
        Assert.True(check.CanInstallInApp);
        Assert.Equal("1.3.0", check.LatestVersion);

        var reportedProgress = new List<int>();
        await svc.DownloadUpdatesAsync(check, p => reportedProgress.Add(p));
        Assert.Equal(new[] { 25, 100 }, reportedProgress);

        svc.ApplyUpdatesAndRestart(check);
        Assert.Equal(new[] { "cleanup", "restart" }, callOrder);
    }

    [Fact]
    public async Task SettingsViewModel_InstalledUpdateFlow_TransitionsAndLocalizes()
    {
        var mockUpdate = new Mock<IAppUpdateService>();
        mockUpdate.SetupGet(x => x.IsInstalled).Returns(true);
        mockUpdate.SetupGet(x => x.CurrentVersion).Returns("1.0.0");

        var updateResult = new AppUpdateCheckResult(
            HasUpdate: true,
            LatestVersion: "1.1.0",
            ReleasePageUrl: "https://github.com/chenlianwd/TuanRadio/releases/tag/v1.1.0",
            CanInstallInApp: true,
            InternalUpdateInfo: new object());

        mockUpdate.Setup(x => x.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult);
        mockUpdate.Setup(x => x.DownloadUpdatesAsync(
                updateResult,
                It.IsAny<Action<int>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<AppUpdateCheckResult, Action<int>?, CancellationToken>((_, progress, _) =>
            {
                progress?.Invoke(50);
                progress?.Invoke(100);
            })
            .Returns(Task.CompletedTask);

        var restartCalled = false;
        mockUpdate.Setup(x => x.ApplyUpdatesAndRestart(updateResult))
            .Callback(() => restartCalled = true);

        using var vm = CreateSettingsVm(mockUpdate.Object);

        Assert.Equal(UpdateActionPhase.Check, vm.UpdatePhase);
        Assert.Contains("v1.0.0（安装版）", vm.AppVersionText);
        Assert.Equal("检查更新", vm.UpdateActionButtonText);

        // Step 1: Check -> transitions to Download
        await vm.UpdateActionCommand.Execute().FirstAsync();
        Assert.Equal(UpdateActionPhase.Download, vm.UpdatePhase);
        Assert.Equal("下载并更新", vm.UpdateActionButtonText);
        Assert.Contains("v1.1.0", vm.UpdateStatusText);

        // Language switch dynamically updates both status and button text
        AppLanguage.Apply("en");
        Assert.Contains("v1.0.0 (Installed)", vm.AppVersionText);
        Assert.Equal("Download & update", vm.UpdateActionButtonText);
        Assert.Contains("New version v1.1.0", vm.UpdateStatusText);
        AppLanguage.Apply("zh");

        // Step 2: Download -> transitions to RestartToApply
        await vm.UpdateActionCommand.Execute().FirstAsync();
        Assert.Equal(UpdateActionPhase.RestartToApply, vm.UpdatePhase);
        Assert.Equal("立即重启并更新", vm.UpdateActionButtonText);
        Assert.Contains("已下载完成", vm.UpdateStatusText);

        // Step 3: RestartToApply -> invokes ApplyUpdatesAndRestart
        await vm.UpdateActionCommand.Execute().FirstAsync();
        Assert.True(restartCalled);
    }

    [Fact]
    public async Task SettingsViewModel_PortableUpdateFlow_OpensReleasePageOnSecondClick()
    {
        var mockUpdate = new Mock<IAppUpdateService>();
        mockUpdate.SetupGet(x => x.IsInstalled).Returns(false);
        mockUpdate.SetupGet(x => x.CurrentVersion).Returns("1.0.0");

        const string releaseUrl = "https://github.com/chenlianwd/TuanRadio/releases/tag/v1.1.0";
        mockUpdate.Setup(x => x.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUpdateCheckResult(
                HasUpdate: true,
                LatestVersion: "1.1.0",
                ReleasePageUrl: releaseUrl,
                CanInstallInApp: false));

        string? openedUrl = null;
        using var vm = CreateSettingsVm(mockUpdate.Object, url => openedUrl = url);

        Assert.Contains("v1.0.0（免安装版）", vm.AppVersionText);

        // Step 1: Check -> transitions to OpenReleasePage
        await vm.UpdateActionCommand.Execute().FirstAsync();
        Assert.Equal(UpdateActionPhase.OpenReleasePage, vm.UpdatePhase);
        Assert.Equal("前往下载页", vm.UpdateActionButtonText);

        // Step 2: Click OpenReleasePage -> invokes browser callback
        await vm.UpdateActionCommand.Execute().FirstAsync();
        Assert.Equal(releaseUrl, openedUrl);
    }

    private SettingsViewModel CreateSettingsVm(IAppUpdateService updateService, Action<string>? openUrl = null)
    {
        var llm = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        return new SettingsViewModel(
            llm.Object,
            storage.Object,
            settingsPath,
            appUpdateService: updateService,
            openExternalUrl: openUrl ?? (_ => { }));
    }

    private static HttpClient CreateMockHttpClient(HttpStatusCode status, string body)
    {
        return new HttpClient(new StubHttpMessageHandler(status, body));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    private sealed class FakeVelopackClient : IVelopackUpdateClient
    {
        public bool IsInstalled { get; set; }
        public string? CurrentVersion { get; set; }
        public object? NextUpdateObject { get; set; }
        public string? TargetVersion { get; set; }
        public Action<object>? OnApplyRestart { get; set; }

        public Task<object?> CheckForUpdatesAsync() => Task.FromResult(NextUpdateObject);

        public string? GetTargetVersion(object updateInfo) => TargetVersion;

        public Task DownloadUpdatesAsync(object updateInfo, Action<int>? progress)
        {
            progress?.Invoke(25);
            progress?.Invoke(100);
            return Task.CompletedTask;
        }

        public void ApplyUpdatesAndRestart(object updateInfo)
        {
            OnApplyRestart?.Invoke(updateInfo);
        }
    }
}
