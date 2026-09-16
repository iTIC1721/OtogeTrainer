using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
}
