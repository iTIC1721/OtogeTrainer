using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OtogeTrainer.Core.Library;

namespace OtogeTrainer.App.Library;

/// <summary>옮길 곳 고르기. "라이브러리(최상위)" 와 모든 디렉토리를 트리 순서로 펼쳐 보여 준다.</summary>
public partial class DirectoryPickerWindow : Window
{
    /// <param name="current">지금 위치. 표시만 하고 고를 수는 있다 (고르면 아무 일도 없음).</param>
    /// <param name="excluded">고를 수 없는 디렉토리 (디렉토리를 옮길 때 자기 자신과 그 아래).</param>
    public DirectoryPickerWindow(
        string title, string message, DirectoryTree tree, long? current, IReadOnlySet<long>? excluded = null)
    {
        InitializeComponent();
        Title = $"{title} — OtogeTrainer";
        Heading.Text = title;
        Message.Text = message;

        var choices = new List<DirectoryChoice> { new(null, "라이브러리 (최상위)", -1, true, current is null) };
        foreach (var (dir, depth) in tree.Flatten())
            choices.Add(new DirectoryChoice(dir.Id, dir.Name, depth, excluded?.Contains(dir.Id) != true, dir.Id == current));
        Choices.ItemsSource = choices;
        Choices.SelectedItem = choices.FirstOrDefault(c => c.IsCurrent && c.IsSelectable);

        Loaded += (_, _) =>
        {
            Choices.Focus();
            if (Choices.SelectedItem is not null) Choices.ScrollIntoView(Choices.SelectedItem);
        };
        OnSelectionChanged(this, null!);
    }

    /// <summary>고른 곳. 취소했으면 창의 DialogResult 가 false.</summary>
    public long? SelectedDirectoryId { get; private set; }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => OkButton.IsEnabled = Choices.SelectedItem is DirectoryChoice { IsSelectable: true };

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OnOk(sender, e);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Choices.SelectedItem is not DirectoryChoice { IsSelectable: true } choice) return;
        SelectedDirectoryId = choice.Id;
        DialogResult = true;
    }
}

public sealed record DirectoryChoice(long? Id, string Name, int Depth, bool IsSelectable, bool IsCurrent)
{
    public string Icon => Id is null ? "🏠" : "📁";
    public Thickness Indent => new((Depth + 1) * 20, 0, 0, 0);
    public string Note => IsCurrent ? "지금 위치" : IsSelectable ? "" : "옮길 수 없음";
}
