using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
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
    public async Task PortableMode_CheckForUpdates_ParsesDirectVelopackFeedWithoutHittingRestApi()
    {
        const string feedJson = """
        {
          "Assets": [
            {
              "PackageId": "TuanRadio",
              "Version": "1.2.0",
              "Type": "Full",
              "FileName": "TuanRadio-1.2.0-full.nupkg"
            }
          ]
        }
        """;
        using var http = CreateMockHttpClient(HttpStatusCode.OK, feedJson);
        var fakeVpk = new FakeVelopackClient { IsInstalled = false };
        var svc = new AppUpdateService(http, fakeVpk, fallbackVersion: "1.0.0");

        var result = await svc.CheckForUpdatesAsync();

        Assert.True(result.HasUpdate);
        Assert.False(result.CanInstallInApp);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.Equal("https://github.com/chenlianwd/TuanRadio/releases/tag/v1.2.0", result.ReleasePageUrl);
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

    [Fact]
    public async Task SettingsViewModel_DownloadProgress_UpdatesPercentAndResetsAfterCompletionOrFailure()
    {
        var mockUpdate = new Mock<IAppUpdateService>();
        mockUpdate.SetupGet(x => x.IsInstalled).Returns(true);
        mockUpdate.SetupGet(x => x.CurrentVersion).Returns("1.0.3");

        var updateResult = new AppUpdateCheckResult(
            HasUpdate: true,
            LatestVersion: "1.1.0",
            ReleasePageUrl: "https://github.com/chenlianwd/TuanRadio/releases/tag/v1.1.0",
            CanInstallInApp: true,
            InternalUpdateInfo: new object());

        mockUpdate.Setup(x => x.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult);

        Action<int>? capturedProgress = null;
        var downloadGate = new TaskCompletionSource();
        mockUpdate.Setup(x => x.DownloadUpdatesAsync(
                updateResult,
                It.IsAny<Action<int>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<AppUpdateCheckResult, Action<int>?, CancellationToken>((_, progress, _) =>
            {
                capturedProgress = progress;
            })
            .Returns(() => downloadGate.Task);

        using var vm = CreateSettingsVm(mockUpdate.Object);
        Assert.False(vm.IsUpdateDownloading);
        Assert.Equal(0, vm.UpdateDownloadPercent);

        await vm.UpdateActionCommand.Execute().FirstAsync();
        Assert.Equal(UpdateActionPhase.Download, vm.UpdatePhase);
        Assert.False(vm.IsUpdateDownloading);

        var downloadExec = vm.UpdateActionCommand.Execute().FirstAsync().ToTask();
        Assert.True(vm.IsUpdateDownloading);
        Assert.Equal(0, vm.UpdateDownloadPercent);
        Assert.Equal("正在下载 0%…", vm.UpdateActionButtonText);

        capturedProgress?.Invoke(42);
        Assert.True(vm.IsUpdateDownloading);
        Assert.Equal(42, vm.UpdateDownloadPercent);
        Assert.Equal("正在下载 42%…", vm.UpdateActionButtonText);

        AppLanguage.Apply("en");
        Assert.True(vm.IsUpdateDownloading);
        Assert.Equal(42, vm.UpdateDownloadPercent);
        Assert.Equal("Downloading 42%…", vm.UpdateActionButtonText);
        AppLanguage.Apply("zh");

        capturedProgress?.Invoke(-5);
        Assert.Equal(0, vm.UpdateDownloadPercent);
        capturedProgress?.Invoke(135);
        Assert.Equal(100, vm.UpdateDownloadPercent);

        downloadGate.SetResult();
        await downloadExec;
        Assert.Equal(UpdateActionPhase.RestartToApply, vm.UpdatePhase);
        Assert.False(vm.IsUpdateDownloading);
        Assert.Equal(0, vm.UpdateDownloadPercent);

        // 失败路径同样应退出下载态并清零进度
        var failGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mockUpdate.Setup(x => x.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult);
        mockUpdate.Setup(x => x.DownloadUpdatesAsync(
                updateResult,
                It.IsAny<Action<int>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<AppUpdateCheckResult, Action<int>?, CancellationToken>((_, progress, _) =>
            {
                capturedProgress = progress;
            })
            .Returns(() => failGate.Task);

        using var vmFail = CreateSettingsVm(mockUpdate.Object);
        await vmFail.UpdateActionCommand.Execute().FirstAsync();
        var failExec = vmFail.UpdateActionCommand.Execute().FirstAsync().ToTask();
        capturedProgress?.Invoke(65);
        Assert.True(vmFail.IsUpdateDownloading);
        Assert.Equal(65, vmFail.UpdateDownloadPercent);

        failGate.SetException(new HttpRequestException("network error"));
        await failExec;
        Assert.Equal(UpdateActionPhase.Download, vmFail.UpdatePhase);
        Assert.False(vmFail.IsUpdateDownloading);
        Assert.Equal(0, vmFail.UpdateDownloadPercent);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettingsView_UpdateActionButton_RendersPillProgressBarAcrossThemes(bool isDark)
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = AvaloniaScheduler.Instance;
        var app = Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var theme = new FluentTheme();
        app.Styles.Add(theme);
        app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        var mockUpdate = new Mock<IAppUpdateService>();
        mockUpdate.SetupGet(x => x.IsInstalled).Returns(true);
        mockUpdate.SetupGet(x => x.CurrentVersion).Returns("1.0.3");
        var updateResult = new AppUpdateCheckResult(
            HasUpdate: true,
            LatestVersion: "1.1.0",
            ReleasePageUrl: "https://github.com/chenlianwd/TuanRadio/releases/tag/v1.1.0",
            CanInstallInApp: true,
            InternalUpdateInfo: new object());
        mockUpdate.Setup(x => x.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(updateResult);

        Action<int>? capturedProgress = null;
        var downloadGate = new TaskCompletionSource();
        mockUpdate.Setup(x => x.DownloadUpdatesAsync(
                updateResult,
                It.IsAny<Action<int>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<AppUpdateCheckResult, Action<int>?, CancellationToken>((_, progress, _) =>
            {
                capturedProgress = progress;
            })
            .Returns(() => downloadGate.Task);

        using var vm = CreateSettingsVm(mockUpdate.Object);
        vm.TabIndex = 3;

        Window? window = null;
        try
        {
            var colors = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://AIRadio/Themes/Colors.axaml"));
            var settingsView = new SettingsView { DataContext = vm };
            window = new Window
            {
                Width = 760,
                Height = 760,
                Content = settingsView,
                RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            window.Resources.MergedDictionaries.Add(colors);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = settingsView.FindControl<Button>("UpdateActionButton")
                ?? throw new InvalidOperationException("UpdateActionButton not found");
            var progressBar = settingsView.FindControl<ProgressBar>("UpdateDownloadProgressBar")
                ?? throw new InvalidOperationException("UpdateDownloadProgressBar not found");
            var textBlock = settingsView.FindControl<TextBlock>("UpdateActionButtonTextBlock")
                ?? throw new InvalidOperationException("UpdateActionButtonTextBlock not found");

            Assert.True(button.IsEffectivelyEnabled);
            Assert.False(progressBar.IsVisible);
            Assert.DoesNotContain("downloading", button.Classes);

            await vm.UpdateActionCommand.Execute().FirstAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEffectivelyEnabled);

            ((System.Windows.Input.ICommand)vm.UpdateActionCommand).Execute(null);
            Dispatcher.UIThread.RunJobs();
            capturedProgress?.Invoke(60);
            Dispatcher.UIThread.RunJobs();

            Assert.False(button.IsEffectivelyEnabled);
            Assert.Contains(":disabled", button.Classes);
            Assert.Contains("downloading", button.Classes);
            Assert.True(progressBar.IsVisible);
            Assert.Equal(60, progressBar.Value);
            Assert.Equal("正在下载 60%…", textBlock.Text);

            var track = progressBar.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Track");
            var indicator = progressBar.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Indicator");
            Assert.True(track.Bounds.Width >= 100, $"Expected track width >= 100, got {track.Bounds.Width}");
            Assert.Equal(track.Bounds.Width * 0.60, indicator.Bounds.Width, 1.0);

            var variant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
            Color GetColor(string key)
            {
                Assert.True(colors.TryGetResource(key, variant, out var obj) && obj is Color, $"Missing {key}");
                return (Color)obj!;
            }
            var expectedBg = GetColor("C_11121620");
            var expectedBorder = GetColor("C_6656F5C4");
            var expectedFg = GetColor("C_FFEDEDF5");
            var expectedFill = GetColor("C_3356F5C4");

            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
            Assert.Equal(expectedBg.ToUInt32(), ((ISolidColorBrush)presenter.Background!).Color.ToUInt32());
            Assert.Equal(expectedBorder.ToUInt32(), ((ISolidColorBrush)presenter.BorderBrush!).Color.ToUInt32());
            Assert.Equal(expectedFg.ToUInt32(), ((ISolidColorBrush)textBlock.Foreground!).Color.ToUInt32());
            Assert.Equal(expectedFill.ToUInt32(), ((ISolidColorBrush)indicator.Background!).Color.ToUInt32());

            downloadGate.SetResult();
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.IsUpdateBusy);
            Assert.False(vm.IsUpdateDownloading);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.DoesNotContain(":disabled", button.Classes);
            Assert.DoesNotContain("downloading", button.Classes);
            Assert.False(progressBar.IsVisible);
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            app.RequestedThemeVariant = originalTheme;
            RxApp.MainThreadScheduler = originalScheduler;
        }
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
