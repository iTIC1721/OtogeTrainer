using System.Windows;
using System.Windows.Controls;

namespace OtogeTrainer.App.Library;

/// <summary>디렉토리 이름 입력. 입력하는 동안 검사해서 문제를 창 안에 바로 보여 준다.</summary>
public partial class NamePromptWindow : Window
{
    private readonly Func<string, string?> _validate;

    /// <param name="validate">문제가 있으면 안내 문구, 없으면 null.</param>
    public NamePromptWindow(string title, string message, string initial, Func<string, string?> validate)
    {
        InitializeComponent();
        _validate = validate;

        Title = $"{title} — OtogeTrainer";
        Heading.Text = title;
        Message.Text = message;
        NameBox.Text = initial;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>확인했으면 입력한 이름, 취소했으면 null.</summary>
    public string? Result { get; private set; }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var problem = _validate(NameBox.Text);
        Error.Text = problem ?? "";
        Error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_validate(NameBox.Text) is not null) return;
        Result = NameBox.Text;
        DialogResult = true;
    }
}
