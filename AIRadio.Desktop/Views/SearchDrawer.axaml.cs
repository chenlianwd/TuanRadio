using System;
using System.Reactive;
using System.Reactive.Linq;
using AIRadio.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;

namespace AIRadio.Desktop.Views;

/// <summary>独立搜索浮层（与曲库抽屉解耦，专职在线曲库搜索与结果交互）。</summary>
public partial class SearchDrawer : UserControl
{
    private IDisposable? _searchOpenSub;

    public SearchDrawer()
    {
        InitializeComponent();
        PlaylistDrawer.AttachRowContextMenu(SearchResultsList, this, PlaylistDrawer.PlaylistListKind.Search);

        DataContextChanged += (_, _) => BindOpenFocusSubscription();
        AttachedToVisualTree += (_, _) => BindOpenFocusSubscription();
        DetachedFromVisualTree += (_, _) =>
        {
            _searchOpenSub?.Dispose();
            _searchOpenSub = null;
        };
    }

    private void BindOpenFocusSubscription()
    {
        _searchOpenSub?.Dispose();
        _searchOpenSub = null;

        if (!this.IsAttachedToVisualTree() || DataContext is not MainWindowViewModel vm)
            return;

        _searchOpenSub = vm.WhenAnyValue(x => x.IsSearchOpen)
            .Where(open => open)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => FocusSearchInput());
    }

    private void FocusSearchInput()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (SearchInput.IsEffectivelyVisible)
            {
                SearchInput.Focus();
                SearchInput.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
        {
            e.Handled = true;
            if (!vm.PlaylistVM.IsSearching)
                vm.PlaylistVM.SearchCommand.Execute(Unit.Default).Subscribe();
        }
    }
}
