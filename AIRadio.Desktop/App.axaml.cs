using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using System;
using System.IO;

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
                _ = StartMusicAndInitializeAsync();

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

    private async System.Threading.Tasks.Task StartMusicAndInitializeAsync()
    {
        try
        {
            if (_musicApiServer != null)
                await _musicApiServer.StartAsync();

            if (_mainVm != null)
                await _mainVm.InitializeAsync();

            Log.Information("AI Radio initialized successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize AI Radio services");
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<IMinimaxService, MinimaxService>();
        services.AddSingleton<IMusicSearchService>(sp =>
            new MultiSourceMusicService(sp.GetRequiredService<System.Net.Http.HttpClient>()));
        services.AddSingleton<IDJService>(sp =>
            new DJService(sp.GetRequiredService<IMinimaxService>(), sp.GetRequiredService<IMusicSearchService>()));
        services.AddSingleton<IRecommendationService, RecommendationService>();
        services.AddSingleton<ISecureStorage, WindowsSecureStorage>();
        services.AddSingleton<ISttService, WhisperSttService>();
        services.AddSingleton<MainWindowViewModel>();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        Log.Information("AI Radio shutting down...");
        _mainVm?.Dispose();
        _musicApiServer?.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }
}
