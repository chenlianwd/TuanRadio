using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services.WebRemote;

/// <summary>
/// 极轻量嵌入式 HTTP/1.1 + WebSocket 局域网遥控服务端（基于 TcpListener + WebSocket.CreateFromStream）。
/// 免管理员 URLACL 授权、零额外依赖、常量时间令牌认证与每客户端并发上限保护。
/// </summary>
public sealed class WebRemoteService : IWebRemoteService
{
    private const int MaxClients = 8;
    private const int MaxHttpHeaderBytes = 8 * 1024;
    private const int MaxHttpBodyBytes = 32 * 1024;
    private const int MaxWsFrameBytes = 16 * 1024;
    private const string WsMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private sealed class WsClient : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        public Stream TransportStream { get; }
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private int _disposed;

        public WsClient(WebSocket socket, Stream transportStream)
        {
            Socket = socket;
            TransportStream = transportStream;
        }

        public async Task<bool> SendJsonAsync(byte[] utf8Payload, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0 || Socket.State != WebSocketState.Open) return false;
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(2000);
            var acquired = false;
            try
            {
                await _sendGate.WaitAsync(sendCts.Token);
                acquired = true;
                if (Volatile.Read(ref _disposed) != 0 || Socket.State != WebSocketState.Open) return false;
                await Socket.SendAsync(utf8Payload, WebSocketMessageType.Text, true, sendCts.Token);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (acquired)
                {
                    try { _sendGate.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Socket.Dispose(); } catch { }
            try { TransportStream.Dispose(); } catch { }
            _sendGate.Dispose();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _broadcastGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, WsClient> _clients = new();
    private readonly object _broadcastLock = new();
    private int _broadcastDirty;

    private Func<WebRemoteSnapshot> _snapshotProvider = () => new WebRemoteSnapshot();
    private Func<WebRemoteCommand, Task<WebRemoteCommandResult>> _commandHandler =
        _ => Task.FromResult(WebRemoteCommandResult.Fail("unavailable"));

    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _acceptLoopTask;
    private IPAddress _activeBindAddress = IPAddress.Any;
    private Timer? _debounceTimer;
    private long _lastBroadcastTickMs;
    private bool _disposed;

    private volatile string _accessToken = string.Empty;
    private volatile bool _isRunning;
    private volatile int _port;
    private volatile string _primaryUrl = string.Empty;
    private volatile string _lastError = string.Empty;
    private IReadOnlyList<string> _accessUrls = Array.Empty<string>();

    public bool IsRunning => _isRunning;
    public int Port => _port;
    public int ConnectedClientCount => _clients.Count;
    public string PrimaryUrl => _primaryUrl;
    public IReadOnlyList<string> AccessUrls => _accessUrls;
    public string AccessToken => _accessToken;
    public string LastError => _lastError;

    public event Action? StatusChanged;

    public void ConfigureHandlers(
        Func<WebRemoteSnapshot> snapshotProvider,
        Func<WebRemoteCommand, Task<WebRemoteCommandResult>> commandHandler)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
    }

    public async Task<bool> StartAsync(
        int preferredPort,
        string token,
        IPAddress? bindAddress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalizedToken = string.IsNullOrWhiteSpace(token)
            ? LanAddressResolver.GenerateAccessToken()
            : token.Trim();
        var targetBind = bindAddress ?? IPAddress.Any;

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) return false;

            // 若已在同绑定/同端口运行，仅刷新令牌与访问地址即可
            if (_isRunning &&
                _listener != null &&
                _activeBindAddress.Equals(targetBind) &&
                (preferredPort == 0 || preferredPort == _port))
            {
                if (!string.Equals(_accessToken, normalizedToken, StringComparison.Ordinal))
                {
                    _accessToken = normalizedToken;
                    await DisconnectAllClientsAsync(4001, "token_rotated");
                }
                UpdateUrls(targetBind, _port, normalizedToken);
                _lastError = string.Empty;
                RaiseStatusChanged();
                return true;
            }

            await StopCoreAsync();

            var startPort = preferredPort <= 0 ? 0 : Math.Clamp(preferredPort, 1024, 65535);
            var attempts = startPort == 0 ? 1 : 6;
            TcpListener? boundListener = null;
            Exception? lastBindEx = null;

            for (var i = 0; i < attempts; i++)
            {
                var candidate = startPort == 0 ? 0 : startPort + i;
                if (candidate > 65535) break;

                var candidateListener = new TcpListener(targetBind, candidate);
                try
                {
                    candidateListener.Start(16);
                    boundListener = candidateListener;
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    lastBindEx = ex;
                    try { candidateListener.Stop(); } catch { }
                }
                catch (Exception ex)
                {
                    lastBindEx = ex;
                    try { candidateListener.Stop(); } catch { }
                    break;
                }
            }

            if (boundListener == null)
            {
                _isRunning = false;
                _port = 0;
                _primaryUrl = string.Empty;
                _accessUrls = Array.Empty<string>();
                _lastError = lastBindEx?.Message ?? "Failed to bind port";
                Log.Warning(lastBindEx, "WebRemote failed to start on preferred port {Port}", preferredPort);
                RaiseStatusChanged();
                return false;
            }

            var actualPort = ((IPEndPoint)boundListener.LocalEndpoint).Port;
            _listener = boundListener;
            _activeBindAddress = targetBind;
            _accessToken = normalizedToken;
            _port = actualPort;
            _isRunning = true;
            _lastError = string.Empty;
            UpdateUrls(targetBind, actualPort, normalizedToken);

            _runCts = new CancellationTokenSource();
            var loopToken = _runCts.Token;
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(boundListener, loopToken), CancellationToken.None);

            Log.Information("WebRemote started on {UrlCount} address(es), port {Port}", _accessUrls.Count, actualPort);
            RaiseStatusChanged();
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_disposed) return;
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
            RaiseStatusChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RotateTokenAsync(string newToken)
    {
        if (_disposed) return;
        var normalized = string.IsNullOrWhiteSpace(newToken)
            ? LanAddressResolver.GenerateAccessToken()
            : newToken.Trim();

        await _lifecycleGate.WaitAsync();
        try
        {
            _accessToken = normalized;
            if (_isRunning)
            {
                UpdateUrls(_activeBindAddress, _port, normalized);
                await DisconnectAllClientsAsync(4001, "token_rotated");
            }
            RaiseStatusChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void NotifyStateChanged(bool immediate = false)
    {
        if (!_isRunning || _clients.IsEmpty || _disposed) return;

        Interlocked.Exchange(ref _broadcastDirty, 1);
        var flushNow = false;

        lock (_broadcastLock)
        {
            var nowMs = Environment.TickCount64;
            var elapsed = nowMs - Interlocked.Read(ref _lastBroadcastTickMs);
            if (immediate || elapsed >= 100)
            {
                Interlocked.Exchange(ref _lastBroadcastTickMs, nowMs);
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                flushNow = true;
            }
            else
            {
                _debounceTimer ??= new Timer(_ =>
                {
                    lock (_broadcastLock)
                    {
                        _debounceTimer?.Dispose();
                        _debounceTimer = null;
                        Interlocked.Exchange(ref _lastBroadcastTickMs, Environment.TickCount64);
                    }
                    _ = DrainBroadcastsAsync();
                }, null, 100, Timeout.Infinite);
            }
        }

        if (flushNow)
            _ = DrainBroadcastsAsync();
    }

    private async Task DrainBroadcastsAsync()
    {
        if (!_isRunning || _clients.IsEmpty || _disposed) return;
        try
        {
            await _broadcastGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            while (!_disposed && _isRunning && !_clients.IsEmpty &&
                   Interlocked.Exchange(ref _broadcastDirty, 0) == 1)
            {
                await BroadcastSnapshotOnceAsync();
            }
        }
        finally
        {
            try { _broadcastGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task BroadcastSnapshotOnceAsync()
    {
        if (!_isRunning || _clients.IsEmpty || _disposed) return;
        Interlocked.Exchange(ref _lastBroadcastTickMs, Environment.TickCount64);

        byte[] payload;
        try
        {
            var snapshot = _snapshotProvider();
            payload = SerializeWsStateEnvelope(snapshot);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WebRemote snapshot serialization failed");
            return;
        }

        var ct = _runCts?.Token ?? CancellationToken.None;
        foreach (var kv in _clients)
        {
            if (!await kv.Value.SendJsonAsync(payload, ct))
            {
                if (_clients.TryRemove(kv.Key, out var removed))
                {
                    removed.Dispose();
                    RaiseStatusChanged();
                }
            }
        }
    }

    private async Task StopCoreAsync()
    {
        lock (_broadcastLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        _isRunning = false;
        _port = 0;
        _primaryUrl = string.Empty;
        _accessUrls = Array.Empty<string>();

        try { _runCts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        await DisconnectAllClientsAsync((int)WebSocketCloseStatus.NormalClosure, "server_stopped");

        if (_acceptLoopTask != null)
        {
            try { await _acceptLoopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            _acceptLoopTask = null;
        }

        try { _runCts?.Dispose(); } catch { }
        _runCts = null;
        _listener = null;
    }

    private async Task DisconnectAllClientsAsync(int closeCode, string reason)
    {
        var list = _clients.Values.ToArray();
        _clients.Clear();
        foreach (var client in list)
        {
            try
            {
                if (client.Socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(400);
                    await client.Socket.CloseOutputAsync((WebSocketCloseStatus)closeCode, reason, cts.Token);
                }
            }
            catch { }
            finally
            {
                client.Dispose();
            }
        }
    }

    private void UpdateUrls(IPAddress bindAddress, int port, string token)
    {
        var encoded = Uri.EscapeDataString(token);
        if (IPAddress.IsLoopback(bindAddress))
        {
            var loopUrl = $"http://127.0.0.1:{port}/?token={encoded}";
            _accessUrls = [loopUrl];
            _primaryUrl = loopUrl;
            return;
        }

        var ips = LanAddressResolver.GetPreferredLanIpv4Addresses();
        var urls = ips.Select(ip => $"http://{ip}:{port}/?token={encoded}").ToArray();
        _accessUrls = urls;
        _primaryUrl = urls[0];
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "WebRemote accept failed");
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(tcp, ct), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        var transferOwnershipToWs = false;
        NetworkStream? stream = null;
        try
        {
            tcp.NoDelay = true;
            stream = tcp.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(5000);

            var parsed = await ReadHttpRequestAsync(stream, readCts.Token);
            if (parsed == null)
                return;

            var (method, path, queryToken, headers, bodyBytes) = parsed.Value;

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                (path == "/" || path == "/index.html"))
            {
                await WriteHttpResponseAsync(
                    stream,
                    200,
                    "OK",
                    "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(WebRemoteHtml.Page),
                    ct);
                return;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                path == "/favicon.svg")
            {
                await WriteHttpResponseAsync(
                    stream,
                    200,
                    "OK",
                    "image/svg+xml; charset=utf-8",
                    Encoding.UTF8.GetBytes(WebRemoteHtml.FaviconSvg),
                    ct);
                return;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                path == "/ws")
            {
                if (!headers.TryGetValue("Upgrade", out var upgrade) ||
                    !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase) ||
                    !headers.TryGetValue("Sec-WebSocket-Key", out var wsKey) ||
                    string.IsNullOrWhiteSpace(wsKey))
                {
                    await WriteJsonResponseAsync(stream, 400, "Bad Request",
                        new { ok = false, error = "bad_websocket_request" }, ct);
                    return;
                }

                if (_clients.Count >= MaxClients)
                {
                    await WriteJsonResponseAsync(stream, 503, "Service Unavailable",
                        new { ok = false, error = "too_many_clients" }, ct);
                    return;
                }

                var isAuthed = IsAuthorized(headers, queryToken);
                await CompleteWebSocketHandshakeAsync(stream, wsKey, ct);
                stream.ReadTimeout = Timeout.Infinite;
                stream.WriteTimeout = 5000;

                using var ws = WebSocket.CreateFromStream(
                    stream,
                    new WebSocketCreationOptions
                    {
                        IsServer = true,
                        KeepAliveInterval = TimeSpan.FromSeconds(20)
                    });

                if (!isAuthed)
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(1000);
                        await ws.CloseAsync((WebSocketCloseStatus)4001, "unauthorized", closeCts.Token);
                    }
                    catch { }
                    return;
                }

                transferOwnershipToWs = true;
                await RunWebSocketSessionAsync(tcp, stream, ws, ct);
                return;
            }

            if (!IsAuthorized(headers, queryToken))
            {
                await WriteJsonResponseAsync(stream, 401, "Unauthorized",
                    new { ok = false, error = "unauthorized" }, ct);
                return;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                path == "/api/state")
            {
                var snapshot = _snapshotProvider();
                var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
                await WriteHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, ct);
                return;
            }

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                path == "/api/command")
            {
                WebRemoteCommand? cmd;
                try
                {
                    cmd = JsonSerializer.Deserialize<WebRemoteCommand>(bodyBytes, JsonOptions);
                }
                catch
                {
                    await WriteJsonResponseAsync(stream, 400, "Bad Request",
                        new { ok = false, error = "invalid_json" }, ct);
                    return;
                }

                if (cmd == null || string.IsNullOrWhiteSpace(cmd.Action))
                {
                    await WriteJsonResponseAsync(stream, 400, "Bad Request",
                        new { ok = false, error = "missing_action" }, ct);
                    return;
                }

                var result = await _commandHandler(cmd);
                var status = result.Ok ? 200 : 400;
                var reason = result.Ok ? "OK" : "Bad Request";
                await WriteJsonResponseAsync(stream, status, reason, result, ct);
                return;
            }

            await WriteJsonResponseAsync(stream, 404, "Not Found",
                new { ok = false, error = "not_found" }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WebRemote connection handler error");
        }
        finally
        {
            if (!transferOwnershipToWs)
            {
                try { stream?.Dispose(); } catch { }
                try { tcp.Dispose(); } catch { }
            }
        }
    }

    private async Task RunWebSocketSessionAsync(TcpClient tcp, NetworkStream stream, WebSocket ws, CancellationToken ct)
    {
        var client = new WsClient(ws, stream);
        if (!_clients.TryAdd(client.Id, client))
        {
            client.Dispose();
            tcp.Dispose();
            return;
        }
        RaiseStatusChanged();

        try
        {
            // 连接建立即推首帧全量状态
            var initialPayload = SerializeWsStateEnvelope(_snapshotProvider());
            await client.SendJsonAsync(initialPayload, ct);

            var buffer = new byte[4096];
            using var msgBuffer = new MemoryStream();

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                    catch { }
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                if (msgBuffer.Length + result.Count > MaxWsFrameBytes)
                {
                    try
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too_big", CancellationToken.None);
                    }
                    catch { }
                    break;
                }

                msgBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var frameBytes = msgBuffer.ToArray();
                msgBuffer.SetLength(0);

                try
                {
                    var cmd = JsonSerializer.Deserialize<WebRemoteCommand>(frameBytes, JsonOptions);
                    if (cmd != null && !string.IsNullOrWhiteSpace(cmd.Action))
                    {
                        var cmdResult = await _commandHandler(cmd);
                        var ackBytes = JsonSerializer.SerializeToUtf8Bytes(
                            new { type = "ack", action = cmd.Action, ok = cmdResult.Ok, message = cmdResult.Message },
                            JsonOptions);
                        await client.SendJsonAsync(ackBytes, ct);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "WebRemote WS frame handling error");
                }
            }
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Dispose();
            try { tcp.Dispose(); } catch { }
            RaiseStatusChanged();
        }
    }

    private bool IsAuthorized(IReadOnlyDictionary<string, string> headers, string? queryToken)
    {
        var expected = _accessToken;
        if (string.IsNullOrEmpty(expected))
            return false;

        string? candidate = null;
        if (headers.TryGetValue("X-Remote-Token", out var headerToken) && !string.IsNullOrWhiteSpace(headerToken))
        {
            candidate = headerToken.Trim();
        }
        else if (headers.TryGetValue("Authorization", out var authHeader) &&
                 authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            candidate = authHeader[7..].Trim();
        }
        else if (!string.IsNullOrWhiteSpace(queryToken))
        {
            candidate = queryToken.Trim();
        }

        if (string.IsNullOrEmpty(candidate))
            return false;

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task<(string Method, string Path, string? QueryToken, Dictionary<string, string> Headers, byte[] Body)?> ReadHttpRequestAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        var headerBuf = new MemoryStream();
        var temp = new byte[2048];
        var headerEndIndex = -1;

        while (headerEndIndex < 0)
        {
            var read = await stream.ReadAsync(temp.AsMemory(0, temp.Length), ct);
            if (read <= 0)
                return null;

            headerBuf.Write(temp, 0, read);
            if (headerBuf.Length > MaxHttpHeaderBytes)
                return null;

            var span = headerBuf.GetBuffer().AsSpan(0, (int)headerBuf.Length);
            headerEndIndex = span.IndexOf("\r\n\r\n"u8);
        }

        var rawBytes = headerBuf.GetBuffer();
        var totalRead = (int)headerBuf.Length;
        var headerText = Encoding.ASCII.GetString(rawBytes, 0, headerEndIndex);
        var bodyStart = headerEndIndex + 4;
        var leftoverLen = totalRead - bodyStart;

        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
            return null;

        var requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2)
            return null;

        var method = requestParts[0];
        var rawTarget = requestParts[1];
        var qIdx = rawTarget.IndexOf('?');
        var path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
        var queryToken = qIdx >= 0 ? ExtractTokenFromQuery(rawTarget[(qIdx + 1)..]) : null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            var name = lines[i][..colon].Trim();
            var val = lines[i][(colon + 1)..].Trim();
            headers[name] = val;
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var clText))
        {
            if (!int.TryParse(clText, out contentLength) || contentLength < 0 || contentLength > MaxHttpBodyBytes)
            {
                await WriteJsonResponseAsync(stream, 413, "Payload Too Large",
                    new { ok = false, error = "body_too_large" }, ct);
                return null;
            }
        }

        var body = Array.Empty<byte>();
        if (contentLength > 0)
        {
            body = new byte[contentLength];
            var copied = Math.Min(leftoverLen, contentLength);
            if (copied > 0)
                Buffer.BlockCopy(rawBytes, bodyStart, body, 0, copied);

            while (copied < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(copied, contentLength - copied), ct);
                if (read <= 0)
                    return null;
                copied += read;
            }
        }

        return (method, path, queryToken, headers, body);
    }

    private static string? ExtractTokenFromQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            try
            {
                var key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
                if (string.Equals(key, "token", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
        return null;
    }

    private static async Task CompleteWebSocketHandshakeAsync(NetworkStream stream, string secWebSocketKey, CancellationToken ct)
    {
        var acceptHash = SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey.Trim() + WsMagicGuid));
        var accept = Convert.ToBase64String(acceptHash);
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "Cache-Control: no-store\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static Task WriteJsonResponseAsync(NetworkStream stream, int statusCode, string reason, object payload, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return WriteHttpResponseAsync(stream, statusCode, reason, "application/json; charset=utf-8", body, ct);
    }

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string contentType,
        byte[] body,
        CancellationToken ct)
    {
        var header =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "X-Frame-Options: DENY\r\n" +
            "Referrer-Policy: no-referrer\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static byte[] SerializeWsStateEnvelope(WebRemoteSnapshot snapshot)
        => JsonSerializer.SerializeToUtf8Bytes(new { type = "state", data = snapshot }, JsonOptions);

    private void RaiseStatusChanged()
    {
        try { StatusChanged?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_broadcastLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        _isRunning = false;
        try { _runCts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();

        try { _runCts?.Dispose(); } catch { }
        _runCts = null;
        _listener = null;
        _broadcastGate.Dispose();
        _lifecycleGate.Dispose();
    }
}
