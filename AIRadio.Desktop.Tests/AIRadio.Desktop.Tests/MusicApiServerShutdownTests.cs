using System.Diagnostics;
using System.Reflection;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 关闭路径不再被 Kill(entireProcessTree) 同步阻塞的行为锚定：调试器附加的进程树上
/// 该调用实测阻塞 8~12 秒（2026-10-30 诊断，实机埋点 kill=7949ms/12302ms），两个
/// 代理串行曾把音频停止推迟到关窗后约 20 秒。Dispose 必须先关 job 句柄（内核级
/// 终止）、把 Kill 降级为后台兜底；Stop() 保持同步语义供启动失败回收路径使用。
/// </summary>
public class MusicApiServerShutdownTests
{
    private static Process StartStubProcess() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 60 127.0.0.1 > nul",
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

    private static void InjectProcess(MusicApiServer server, Process? process, KillOnCloseJob? job)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(MusicApiServer).GetField("_process", flags)!.SetValue(server, process);
        typeof(MusicApiServer).GetField("_childJob", flags)!.SetValue(server, job);
    }

    private static MusicApiServer CreateServer() => new(port: 37299, serverDirName: "no-such-dir", logTag: "TestApi");

    private static bool WaitForProcessExit(int pid, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static void CleanupProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cleanup kill failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    public void Dispose_ReturnsImmediately_AndJobCloseTerminatesProcess()
    {
        using var server = CreateServer();
        var job = KillOnCloseJob.TryCreate();
        Assert.NotNull(job);
        var process = StartStubProcess();
        // pid 须在注入前读取：Dispose/Stop 路径内部会释放 Process 对象，之后再访问属性会抛
        int pid = process.Id;
        try
        {
            Assert.True(job!.TryAssign(process));
            InjectProcess(server, process, job);

            var sw = Stopwatch.StartNew();
            server.Dispose();
            sw.Stop();

            // 非调试环境整树 Kill 仅 20~30ms；上限只拦"回归到秒级同步阻塞"
            // （调试器附加机器上旧实现为 8~12 秒）。job 句柄关闭是内核即时操作。
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"Dispose blocked for {sw.ElapsedMilliseconds}ms; kill must not run on the shutdown thread");
            Assert.True(WaitForProcessExit(pid, 5000),
                "process should terminate via job close (or the background kill fallback)");
        }
        finally
        {
            CleanupProcess(process);
            job?.Dispose();
        }
    }

    [Fact]
    public void Dispose_WithoutJob_StillTerminatesProcessViaBackgroundKill()
    {
        // job 创建失败的降级场景：唯一的终止手段是后台 Kill 兜底
        using var server = CreateServer();
        var process = StartStubProcess();
        int pid = process.Id;
        try
        {
            InjectProcess(server, process, job: null);

            server.Dispose();

            Assert.True(WaitForProcessExit(pid, 5000),
                "background kill fallback should terminate the process even without a job");
        }
        finally
        {
            CleanupProcess(process);
        }
    }

    [Fact]
    public void Stop_RemainsSynchronousForStartupRecovery()
    {
        // StartAsync 失败回收依赖同步语义：Stop 返回即进程已死（含 ≤1s 的退出收敛等待）
        using var server = CreateServer();
        var process = StartStubProcess();
        int pid = process.Id;
        try
        {
            InjectProcess(server, process, job: null);

            server.Stop();

            Assert.True(WaitForProcessExit(pid, 3000),
                "Stop must synchronously terminate the process for existing callers");
        }
        finally
        {
            CleanupProcess(process);
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var server = CreateServer();
        server.Dispose();
        server.Dispose();
    }
}
