using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OtogeTrainer.App.Library;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    /// <summary>검색창에서 ESC: 검색어를 지우고 포커스를 창으로 돌린다 (단축키가 다시 먹도록).</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is LibraryViewModel vm) vm.ClearFiltersCommand.Execute(null);
        if (Window.GetWindow(this) is { } window) Keyboard.Focus(window);
        e.Handled = true;
    }

    internal TextBox SearchInput => SearchBox;

    // ── 끌어다 놓기로 옮기기 (D-10) ──────────────────────────────────────────
    //
    // 원본: 영상 카드 · 디렉토리 카드. 대상: 디렉토리 카드 · 경로 조각.
    // 앱 안의 데이터 형식으로만 주고받는다 — 탐색기에서 끌어온 파일(영상 열기)은 창이 따로 처리한다.

    internal const string DragFormat = "OtogeTrainer.LibraryEntry";

    private Point _dragStart;
    private object? _dragCandidate;

    private void OnDragSourceDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext;
    }

    private void OnDragSourceMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || sender is not UIElement source) return;

        var moved = e.GetPosition(this) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var entry = _dragCandidate;
        _dragCandidate = null;
        source.ReleaseMouseCapture();   // 버튼이 잡고 있던 마우스를 놓아야 뗄 때 클릭(열기)으로 처리되지 않는다
        DragDrop.DoDragDrop(source, new DataObject(DragFormat, entry), DragDropEffects.Move);
    }

    private void OnDropTargetDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return;   // 파일 끌어오기는 창에 맡긴다

        var ok = CanDrop(e.Data.GetData(DragFormat), TargetOf(sender));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (sender is Control control)
        {
            if (ok) control.BorderBrush = (Brush)FindResource("Accent");
            else control.ClearValue(Control.BorderBrushProperty);
        }
        e.Handled = true;
    }

    private void OnDropTargetDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Control control) control.ClearValue(Control.BorderBrushProperty);
    }

    private async void OnDropTargetDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return;
        e.Handled = true;
        if (sender is Control control) control.ClearValue(Control.BorderBrushProperty);

        var entry = e.Data.GetData(DragFormat);
        var target = TargetOf(sender);
        if (DataContext is not LibraryViewModel vm || !CanDrop(entry, target)) return;

        switch (entry)
        {
            case VideoCardItem video: await vm.MoveVideoAsync(video.VideoId, target!.Value.Id); break;
            case DirectoryCardItem dir: await vm.MoveDirectoryAsync(dir.Id, target!.Value.Id); break;
        }
    }

    /// <summary>놓을 곳. 값이 없으면 대상이 아니다. <c>Id</c> 가 null 이면 최상위.</summary>
    private static DropTarget? TargetOf(object sender) => (sender as FrameworkElement)?.DataContext switch
    {
        DirectoryCardItem d => new DropTarget(d.Id),
        BreadcrumbItem b => new DropTarget(b.Id),
        _ => null,
    };

    private bool CanDrop(object? entry, DropTarget? target)
    {
        if (target is not { } t || DataContext is not LibraryViewModel vm) return false;
        return entry switch
        {
            VideoCardItem video => video.DirectoryId != t.Id,
            // 자기 자신 · 자기 안 · 이미 있는 곳으로는 못 간다
            DirectoryCardItem dir => (t.Id is null || !vm.Tree.SelfAndDescendants(dir.Id).Contains(t.Id.Value))
                                     && vm.Tree.Find(dir.Id)?.ParentId != t.Id,
            _ => false,
        };
    }
}

/// <summary>놓을 곳. <c>Id</c> 가 null 이면 최상위.</summary>
internal readonly record struct DropTarget(long? Id);
