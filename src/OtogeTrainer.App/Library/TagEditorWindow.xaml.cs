using System.Windows;
using System.Windows.Controls;
using OtogeTrainer.Core.Library;

namespace OtogeTrainer.App.Library;

/// <summary>영상 한 편의 태그를 쉼표 구분 텍스트로 고친다. (FR-107)</summary>
public partial class TagEditorWindow : Window
{
    public TagEditorWindow(string videoTitle, IReadOnlyList<string> current, IReadOnlyList<string> existing)
    {
        InitializeComponent();

        VideoTitle.Text = videoTitle;
        TagsBox.Text = current.Count == 0 ? "" : string.Join(", ", current) + ", ";

        var suggestions = existing.Where(t => !current.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        ExistingTags.ItemsSource = suggestions;
        ExistingLabel.Visibility = ExistingTags.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            TagsBox.Focus();
            TagsBox.CaretIndex = TagsBox.Text.Length;
        };
    }

    /// <summary>저장했으면 정리된 태그 목록, 취소했으면 null.</summary>
    public IReadOnlyList<string>? Result { get; private set; }

    private void OnExistingTag(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string tag } button) return;

        var text = TagsBox.Text.TrimEnd();
        if (text.Length > 0 && !text.EndsWith(',')) text += ",";
        TagsBox.Text = (text.Length > 0 ? text + " " : "") + tag + ", ";
        button.Visibility = Visibility.Collapsed;

        TagsBox.Focus();
        TagsBox.CaretIndex = TagsBox.Text.Length;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = TagRules.Parse(TagsBox.Text);
        DialogResult = true;
    }
}
