using System.Windows;
using System.Windows.Controls;
using CleanSlate.Core;

namespace CleanSlate.UI;

public partial class LeftoverListControl : UserControl
{
    public LeftoverListControl() => InitializeComponent();

    LeftoverSet? Set => DataContext as LeftoverSet;
    void SelectVerified_Click(object s, RoutedEventArgs e) => Set?.SelectVerifiedOnly();
    void SelectAll_Click(object s, RoutedEventArgs e) => Set?.SelectAll();
    void SelectNone_Click(object s, RoutedEventArgs e) => Set?.SelectNone();
}
