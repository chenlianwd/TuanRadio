using System.Diagnostics;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// kill-on-close 作业对象语义锚定：句柄关闭（等价于宿主被 TerminateProcess 强杀后
/// 句柄全量回收）必须终止所有已绑定子进程——这是杜绝孤儿 Node 代理的根机制。
/// </summary>
public class KillOnCloseJobTests
{
    [Fact]
    public void ClosingJobHandle_TerminatesAssignedProcess()
    {
        var job = KillOnCloseJob.TryCreate();
        Assert.NotNull(job);

        // ping 60 秒 = 稳定的长驻子进程
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 60 127.0.0.1 > nul",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);
        Assert.True(job!.TryAssign(process));

        job.Dispose();

        Assert.True(process.WaitForExit(5000), "assigned process should die when the job handle closes");
    }

    [Fact]
    public void TryAssign_IsSafeAgainstAlreadyExitedProcess()
    {
        var job = KillOnCloseJob.TryCreate();
        Assert.NotNull(job);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(5000));

        // 已退出进程的绑定竞态不得抛异常（结果成败均可）
        _ = job!.TryAssign(process);
        job.Dispose();
    }
}
