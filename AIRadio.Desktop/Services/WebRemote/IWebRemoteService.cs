using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services.WebRemote;

public interface IWebRemoteService : IDisposable
{
    bool IsRunning { get; }
    int Port { get; }
    int ConnectedClientCount { get; }
    string PrimaryUrl { get; }
    IReadOnlyList<string> AccessUrls { get; }
    string AccessToken { get; }
    string LastError { get; }

    event Action? StatusChanged;

    void ConfigureHandlers(
        Func<WebRemoteSnapshot> snapshotProvider,
        Func<WebRemoteCommand, Task<WebRemoteCommandResult>> commandHandler);

    Task<bool> StartAsync(
        int preferredPort,
        string token,
        IPAddress? bindAddress = null,
        CancellationToken cancellationToken = default);

    Task StopAsync();

    Task RotateTokenAsync(string newToken);

    void NotifyStateChanged(bool immediate = false);
}
