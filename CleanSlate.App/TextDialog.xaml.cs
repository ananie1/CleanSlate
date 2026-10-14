using System.Windows;

namespace CleanSlate.UI;

/// <summary>Универсальное окно: подтверждение (OK + Отмена) или отчёт (только «Закрыть»). Кнопкой по умолчанию в подтверждении всегда является «Отмена».</summary>
public partial class TextDialog : Window
{
    public TextDialog(string title, string header, string body, string okText, string? cancelText)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        BodyBox.Text = body;
        OkBtn.Content = okText;
        if (cancelText == null)
        {
            CancelBtn.Visibility = Visibility.Collapsed;
            OkBtn.IsDefault = true;
            OkBtn.IsCancel = true;
        }
        else CancelBtn.Content = cancelText;

        if (string.IsNullOrEmpty(body)) { BodyBox.Visibility = Visibility.Collapsed; Height = 230; }
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
