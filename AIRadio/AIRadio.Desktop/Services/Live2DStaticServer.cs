using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public class Live2DStaticServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _contentRoot = string.Empty;
    private int _port = 18080;
    private Task? _serverTask;
    private bool _isRunning;

    public bool IsRunning => _isRunning;
    public int Port => _port;

    public void Start(int port, string contentRoot)
    {
        if (_isRunning)
        {
            Log.Warning("Server is already running");
            return;
        }

        _port = port;
        _contentRoot = contentRoot;

        if (!Directory.Exists(_contentRoot))
        {
            Log.Error("Content root does not exist: {Path}", _contentRoot);
            throw new DirectoryNotFoundException(_contentRoot);
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _isRunning = true;

        _serverTask = Task.Run(() => ListenLoop(_cts.Token));
        Log.Information("Static server started on port {Port}, serving {ContentRoot}", port, contentRoot);
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                Log.Warning("HttpListener error: {Message}", ex.Message);
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in listen loop");
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            // 移除前缀，获取相对路径
            var requestPath = request.Url?.AbsolutePath?.TrimStart('/') ?? "index.html";

            // 安全检查：禁止目录遍历
            if (requestPath.Contains("..") || requestPath.Contains(":"))
            {
                response.StatusCode = 403;
                response.Close();
                return;
            }

            var filePath = Path.Combine(_contentRoot, requestPath);

            // 如果请求目录，返回 index.html
            if (Directory.Exists(filePath))
            {
                filePath = Path.Combine(filePath, "index.html");
            }

            if (File.Exists(filePath))
            {
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                var contentType = GetMimeType(extension);

                response.ContentType = contentType;
                response.StatusCode = 200;

                var fileBytes = File.ReadAllBytes(filePath);
                response.ContentLength64 = fileBytes.Length;
                response.OutputStream.Write(fileBytes, 0, fileBytes.Length);
            }
            else
            {
                response.StatusCode = 404;
                var notFound = System.Text.Encoding.UTF8.GetBytes("404 Not Found");
                response.ContentType = "text/plain";
                response.ContentLength64 = notFound.Length;
                response.OutputStream.Write(notFound, 0, notFound.Length);
            }

            response.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling request");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }

    private static string GetMimeType(string extension) => extension switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream"
    };

    public void Stop()
    {
        if (!_isRunning) return;

        Log.Information("Stopping static server");
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _isRunning = false;

        try { _serverTask?.Wait(1000); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    public string GetBaseUrl() => $"http://localhost:{_port}";
};