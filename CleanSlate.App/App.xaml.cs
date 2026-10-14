using System.Windows;
using System.Windows.Threading;
using CleanSlate.Core;

namespace CleanSlate.UI;

public partial class App : Application
{
    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Instance.Error("Необработанная ошибка: " + e.Exception);
        MessageBox.Show("Произошла непредвиденная ошибка. Подробности записаны в журнал.\n\n" + e.Exception.Message,
            "CleanSlate", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
