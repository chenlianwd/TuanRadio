using System;
using System.Reactive.Concurrency;
using System.Threading;
using Avalonia.Headless.XUnit;
using AIRadio.Desktop.Models;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// CurrentState 统一电台状态机的 OAPH 接线锚定：源生成器迁移后 ToProperty 返回的
/// helper 必须赋给生成的 _currentStateHelper 字段，否则 getter 永远走 null 回退、
/// 状态冻结在 Idle。同时覆盖 [Reactive] 生成属性（IsPlaying/IsProcessing）的变更通知。
/// </summary>
public class MainWindowViewModelCurrentStateTests
{
    [AvaloniaFact]
    public void CurrentState_DerivesFromSubViewModelFlags()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        try
        {
            Assert.Equal(RadioState.Idle, vm.CurrentState);

            vm.PlayerVM.IsPlaying = true;
            Assert.Equal(RadioState.Playing, vm.CurrentState);

            // Curating 优先级高于 Playing（Error > Speaking > Searching > Curating > Playing > Idle）
            vm.ChatVM.IsProcessing = true;
            Assert.Equal(RadioState.Curating, vm.CurrentState);

            vm.ChatVM.HasFailure = true;
            Assert.Equal(RadioState.Error, vm.CurrentState);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
            vm.Dispose();
        }
    }
}
