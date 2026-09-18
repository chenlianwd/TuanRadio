using System.Collections.Concurrent;
using System.Text.Json;
using AIRadio.Desktop.Services;
using Moq;

namespace AIRadio.Desktop.Tests;

/// <summary>画像写盘与清空、退出重叠时的回归测试，全部使用独立临时文件。</summary>
public sealed class ListeningProfilePersistenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"airadio-profile-review-{Guid.NewGuid():N}.json");

    /// <summary>写入旧快照期间新增的事件必须被后续快照保存。</summary>
    [Fact]
    public async Task Flush_EventAddedDuringWrite_IsPersisted()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateBlockedWriter(entered, release);
        await service.LoadAsync(CancellationToken.None);
        service.RecordEvent(Event("旧事件"));
        var flush = service.FlushAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.RecordEvent(Event("新增事件"));
        }
        finally
        {
            release.TrySetResult();
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_path));
        Assert.Equal(2, json.RootElement.GetProperty("TotalEventsIngested").GetInt64());
        Assert.Equal("新增事件", json.RootElement.GetProperty("Events")[1].GetProperty("Title").GetString());
    }

    /// <summary>清空时作废在途旧快照，并保留清空之后采集的新事件。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_DuringWrite_DiscardsOldSnapshot(bool addNewEvent)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateBlockedWriter(entered, release);
        await service.LoadAsync(CancellationToken.None);
        service.RecordEvent(Event("已清空事件"));
        var flush = service.FlushAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.Reset();
            if (addNewEvent)
                service.RecordEvent(Event("清空后事件"));
        }
        finally
        {
            release.TrySetResult();
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(addNewEvent ? 1 : 0, service.GetSnapshot().TotalEventsIngested);
        Assert.Equal(addNewEvent, File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
        if (addNewEvent)
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_path));
            Assert.Equal("清空后事件", json.RootElement.GetProperty("Events")[0].GetProperty("Title").GetString());
        }
    }

    /// <summary>UI 线程同步释放服务时，最终写盘不应等待 UI 队列续体。</summary>
    [Fact]
    public async Task Dispose_WithUiContext_FinishesSaveBeforeReturning()
    {
        using var service = new ListeningProfileService(new Mock<ILLMService>().Object, _path, null, WriteDelayedAsync);
        await service.LoadAsync(CancellationToken.None);
        service.RecordEvent(Event("退出前事件"));
        var previous = SynchronizationContext.Current;
        var context = new QueuedContext();
        bool savedBeforeReturn;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            service.Dispose();
            savedBeforeReturn = File.Exists(_path);
        }
        finally
        {
            // 即使旧实现回归，也排空保存续体，避免测试污染后续用例。
            context.Drain();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await service.FlushAsync(CancellationToken.None);
        Assert.True(savedBeforeReturn, "退出返回前最终快照尚未发布");
        Assert.Equal(0, context.PostCount);
    }

    /// <summary>创建可暂停临时文件写入的服务，精确控制事件与保存的交错。</summary>
    private ListeningProfileService CreateBlockedWriter(TaskCompletionSource entered, TaskCompletionSource release)
        => new(new Mock<ILLMService>().Object, _path, null, async (path, json, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            await File.WriteAllTextAsync(path, json, token).ConfigureAwait(false);
        });

    /// <summary>模拟不依赖 UI 的异步磁盘写入，避免极快磁盘掩盖同步等待死锁。</summary>
    private static async Task WriteDelayedAsync(string path, string json, CancellationToken token)
    {
        await Task.Delay(25, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, json, token).ConfigureAwait(false);
    }

    /// <summary>生成用于区分新旧快照的收听事件。</summary>
    private static ListeningEventData Event(string title)
        => new() { Type = ListeningEventType.Like, Title = title, Artist = "测试歌手" };

    /// <summary>删除本用例创建的精确临时路径。</summary>
    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
    }

    /// <summary>模拟在 Dispose 同步等待期间不处理消息的 UI 线程。</summary>
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public int PostCount { get; private set; }

        /// <summary>记录被错误派发到 UI 的保存续体。</summary>
        public override void Post(SendOrPostCallback callback, object? state)
        {
            PostCount++;
            _queue.Enqueue((callback, state));
        }

        /// <summary>清理失败实现遗留的保存续体。</summary>
        public void Drain()
        {
            while (_queue.TryDequeue(out var item))
                item.Callback(item.State);
        }
    }
}
