using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Serilog;

namespace AIRadio.Desktop;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    private MusicApiServer? _musicApiServer;
    private MusicApiServer? _kugouApiServer;
    private MainWindowViewModel? _mainVm;
    private Task? _initializationTask;
    private readonly CancellationTokenSource _lifetimeCts = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void RegisterServices()
    {
        base.RegisterServices();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // MUST set before any ReactiveUI ViewModel is created
        RxApp.MainThreadScheduler = AvaloniaScheduler.Instance;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AIRadio");
            Directory.CreateDirectory(appDataPath);

            var logPath = Path.Combine(appDataPath, "logs", "airadio-.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("AI Radio starting...");

            // 界面文案宿主必须先于任何视图挂载；语言随后由设置加载时 Apply
            AppLanguage.Attach(this);

            // 全局兜底：未观察的任务异常标记已观察避免进程被撕掉，未处理域异常落日志便于事后排查
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()),
                    "Unhandled AppDomain exception");

            try
            {
                var services = new ServiceCollection();
                ConfigureServices(services);
                _serviceProvider = services.BuildServiceProvider();

                var mainWindow = new Views.MainWindow();
                _mainVm = _serviceProvider.GetRequiredService<MainWindowViewModel>();
                mainWindow.DataContext = _mainVm;

                desktop.MainWindow = mainWindow;
                desktop.ShutdownRequested += OnShutdownRequested;
                _musicApiServer = new MusicApiServer();
                _kugouApiServer = new MusicApiServer(
                    port: 37251,
                    serverDirName: "server-kugou",
                    healthQuery: "/search?keywords=test&pagesize=1",
                    healthResponseValidator: LooksLikeKugouSearchResponse,
                    logTag: "KugouApi",
                    // 酷狗未登录时 /search 返回 502 + 合法 JSON（error_code:152），
                    // 不能用 2xx 判活，只能按响应体形状识别
                    requireSuccessStatusCode: false);
                _initializationTask = StartMusicAndInitializeAsync(_lifetimeCts.Token);

                Log.Information("AI Radio shell started successfully");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed to start AI Radio");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async System.Threading.Tasks.Task StartMusicAndInitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 先恢复已保存的音源登录态，再启动代理：设置页扫码与音源请求共用同一份内存副本
            if (_serviceProvider != null)
                await _serviceProvider.GetRequiredService<MusicAccountStore>().LoadAsync();

            // 本地状态（设置/歌单/主题/简洁模式）恢复不依赖网络，先于音乐代理执行：
            // 设置页在窗口可交互时即显示真实配置，而不是等待 Node 代理就绪期间的空默认值。
            // 本地文件异常只降级为默认值，不得中断后续音乐代理与会话开场
            if (_mainVm != null)
            {
                try
                {
                    await _mainVm.LoadLocalStateAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to restore local state, continuing with music server startup");
                }
            }

            // 两个本地代理彼此独立并行启动：网易健康检查可能因外网响应较慢，
            // 不应把酷狗歌单请求留在端口尚未监听的窗口中。歌单服务自身仍有
            // 有限重试，覆盖 Node 进程刚创建但尚未开始监听的极短竞态。
            var musicServerTask = _musicApiServer?.StartAsync(cancellationToken)
                                  ?? Task.CompletedTask;
            var kugouServerTask = StartKugouApiAsync(cancellationToken);
            await Task.WhenAll(musicServerTask, kugouServerTask);

            cancellationToken.ThrowIfCancellationRequested();
            // 会话开场（欢迎语/开播推荐/账号昵称）依赖音源代理就绪，放在代理之后
            if (_mainVm != null)
                await _mainVm.StartSessionAsync(cancellationToken);

            Log.Information("AI Radio initialized successfully");

            // yt-dlp 首次下载约 20s，若发生在搜索兜底时会吃掉 YouTube 源的 30s 预算导致必然超时；
            // 启动后台预热，失败只记日志，真正用到 YouTube 时仍会按需重试
            try
            {
                await YtdlpManager.EnsureInstalledAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "yt-dlp prewarm failed");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("AI Radio initialization cancelled during shutdown");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize AI Radio services");
        }
    }

    private async Task StartKugouApiAsync(CancellationToken cancellationToken)
    {
        if (_kugouApiServer == null)
            return;

        try
        {
            await _kugouApiServer.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 酷狗代理失败不阻断主流程：未登录时该源本就不可用，歌单 UI 会给出重试提示。
            Log.Warning(ex, "Kugou API server startup failed");
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        services.AddSingleton(http);
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<ILLMService, LLMService>();
        services.AddSingleton<ITtsService, EdgeTtsService>();
        services.AddSingleton<ISecureStorage, WindowsSecureStorage>();
        services.AddSingleton<MusicAccountStore>(sp =>
            new MusicAccountStore(sp.GetRequiredService<ISecureStorage>()));
        services.AddSingleton(sp =>
            new KugouVerificationService(sp.GetRequiredService<System.Net.Http.HttpClient>()));
        services.AddSingleton<IMusicSearchService>(sp =>
        {
            var accounts = sp.GetRequiredService<MusicAccountStore>();
            var ytdlpPath = YtdlpManager.GetYtdlpPath();
            var ytSource = new YouTubeMusicService(ytdlpPath, accounts);
            return new MultiSourceMusicService(
                sp.GetRequiredService<System.Net.Http.HttpClient>(),
                accounts,
                sp.GetRequiredService<KugouVerificationService>(),
                ytSource);
        });
        services.AddSingleton<IDJService>(sp =>
            new DJService(
                sp.GetRequiredService<ILLMService>(),
                sp.GetRequiredService<ITtsService>(),
                sp.GetRequiredService<IMusicSearchService>()));
        services.AddSingleton<IRecommendationService>(sp =>
            new RecommendationService(
                sp.GetRequiredService<ILLMService>(),
                sp.GetRequiredService<IMusicSearchService>()));
        services.AddSingleton<ISttService, WhisperSttService>();
        // 真实用户数据路径只在这里落定；测试构造 MainWindowViewModel 必须显式传临时路径（编译期强制）
        services.AddSingleton(sp => new MainWindowViewModel(
            sp.GetRequiredService<IAudioService>(),
            sp.GetRequiredService<IDJService>(),
            sp.GetRequiredService<ILLMService>(),
            sp.GetRequiredService<ISecureStorage>(),
            sp.GetRequiredService<IMusicSearchService>(),
            sp.GetRequiredService<ISttService>(),
            PlaylistViewModel.DefaultPlaylistFile,
            SettingsViewModel.DefaultSettingsFile,
            accountStore: sp.GetRequiredService<MusicAccountStore>(),
            httpClient: sp.GetRequiredService<System.Net.Http.HttpClient>(),
            kugouVerification: sp.GetRequiredService<KugouVerificationService>()));
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        Log.Information("AI Radio shutting down...");
        _lifetimeCts.Cancel();
        _mainVm?.Dispose();
        _kugouApiServer?.Dispose();
        _musicApiServer?.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }

    /// <summary>酷狗代理健康检查：与网易不同，其响应形状以数字 status 字段标识。</summary>
    private static bool LooksLikeKugouSearchResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024)
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("status", out var status) &&
                   status.ValueKind == System.Text.Json.JsonValueKind.Number;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
