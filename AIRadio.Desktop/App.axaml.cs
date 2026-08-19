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
            if (_musicApiServer != null)
                await _musicApiServer.StartAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (_mainVm != null)
                await _mainVm.InitializeAsync(cancellationToken);

            Log.Information("AI Radio initialized successfully");
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

    private void ConfigureServices(IServiceCollection services)
    {
        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        services.AddSingleton(http);
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<ILLMService, LLMService>();
        services.AddSingleton<ITtsService, EdgeTtsService>();
        services.AddSingleton<IMusicSearchService>(sp =>
        {
            var ytdlpPath = YtdlpManager.GetYtdlpPath();
            var ytSource = new YouTubeMusicService(ytdlpPath);
            return new MultiSourceMusicService(sp.GetRequiredService<System.Net.Http.HttpClient>(), ytSource);
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
        services.AddSingleton<ISecureStorage, WindowsSecureStorage>();
        services.AddSingleton<ISttService, WhisperSttService>();
        services.AddSingleton<MainWindowViewModel>();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        Log.Information("AI Radio shutting down...");
        _lifetimeCts.Cancel();
        _mainVm?.Dispose();
        _musicApiServer?.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }
}
