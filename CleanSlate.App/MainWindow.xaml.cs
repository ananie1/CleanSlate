using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CleanSlate.Core;

namespace CleanSlate.UI;

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly ObservableCollection<LogEntry> _logEntries = new();
    readonly LeftoverSet _programSet = new();
    readonly LeftoverSet _manualSet = new();
    List<InstalledProgram> _all = new();
    bool _busy, _loadingSettings;

    InstalledProgram? Current => ProgramList.SelectedItem as InstalledProgram;

    public MainWindow()
    {
        InitializeComponent();
        ProgramLeftovers.DataContext = _programSet;
        ManualLeftovers.DataContext = _manualSet;
        LogList.ItemsSource = _logEntries;
        AppLog.Instance.EntryAdded += e => Dispatcher.BeginInvoke(() => _logEntries.Add(e));

        _loadingSettings = true;
        SetProgramData.IsChecked = _settings.ScanProgramData;
        SetInstallDir.IsChecked = _settings.ScanInstallDir;
        SetQuiet.IsChecked = _settings.QuietUninstall;
        _loadingSettings = false;
        LogPathText.Text = "Файл журнала: " + AppLog.Instance.LogDirectory;

        AppLog.Instance.Info("CleanSlate запущен");
        Loaded += async (_, _) => await ReloadProgramsAsync();
    }

    // ---------- навигация и настройки ----------
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PagePrograms == null || PageManual == null || PageLog == null || PageSettings == null) return;
        var tag = (string)((RadioButton)sender).Tag;
        PagePrograms.Visibility = tag == "Programs" ? Visibility.Visible : Visibility.Collapsed;
        PageManual.Visibility = tag == "Manual" ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = tag == "Log" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.ScanProgramData = SetProgramData.IsChecked == true;
        _settings.ScanInstallDir = SetInstallDir.IsChecked == true;
        _settings.QuietUninstall = SetQuiet.IsChecked == true;
        _settings.Save();
    }

    void OpenLogs_Click(object s, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.Instance.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.Instance.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Tell("Журнал", "Не удалось открыть папку журналов", ex.Message); }
    }

    void ClearLogView_Click(object s, RoutedEventArgs e) => _logEntries.Clear();   // файл журнала не трогаем

    // ---------- список программ ----------
    async Task ReloadProgramsAsync()
    {
        CountText.Text = "Загрузка списка…";
        try { _all = await Task.Run(() => InstalledProgramsService.Load(AppLog.Instance)); }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Ошибка чтения списка программ: " + ex.Message);
            Tell("Ошибка", "Не удалось получить список программ", ex.Message);
        }
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var list = _all.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        ProgramList.ItemsSource = list;
        CountText.Text = $"Показано: {list.Count} из {_all.Count}";
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplyFilter(); }
    async void Refresh_Click(object s, RoutedEventArgs e) => await ReloadProgramsAsync();

    async void ProgramList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = Current;
        BtnUninstall.IsEnabled = BtnCleanProgram.IsEnabled = p != null;
        _programSet.Cts?.Cancel();
        _programSet.Replace(Array.Empty<Leftover>());

        if (p == null)
        {
            DetailName.Text = "Выберите программу слева";
            DetailPublisher.Text = DetailVersion.Text = DetailPath.Text = DetailUninstall.Text = "";
            _programSet.IsBusy = false; _programSet.Status = "";
            return;
        }
        DetailName.Text = p.Name;
        DetailPublisher.Text = p.Publisher.Length > 0 ? p.Publisher : "не указан";
        DetailVersion.Text = p.Version.Length > 0 ? p.Version : "—";
        DetailPath.Text = p.InstallLocation.Length > 0 ? p.InstallLocation : "не указан";
        DetailUninstall.Text = UninstallerService.GetCommand(p, false).Length > 0 ? "доступен" : "не найден";

        await ScanAsync(_programSet, ProgramQuery.FromProgram(p));
    }

    // ---------- поиск ----------
    (LeftoverScanner scanner, SafetyGuard guard) CreateEngine()
    {
        var roots = ScanRoots.Build(_settings.ScanProgramData);
        var guard = SafetyGuard.CreateDefault(roots.Select(r => r.Path));
        return (new LeftoverScanner(roots, guard, AppLog.Instance, _settings.ScanInstallDir), guard);
    }

    async Task ScanAsync(LeftoverSet set, ProgramQuery query)
    {
        set.Cts?.Cancel();
        var cts = set.Cts = new CancellationTokenSource();
        set.IsBusy = true;
        set.Status = "Поиск связанных каталогов…";
        set.Replace(Array.Empty<Leftover>());
        try
        {
            var (scanner, _) = CreateEngine();
            var found = await scanner.ScanAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            set.Replace(found);
            set.Status = found.Count == 0
                ? "Дополнительные каталоги не найдены."
                : "Отмечены только подтверждённые каталоги. Не подтверждённые проверьте вручную: совпадения имени недостаточно.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Ошибка поиска: " + ex);
            set.Status = "Ошибка поиска: " + ex.Message;
        }
        finally { if (ReferenceEquals(set.Cts, cts)) set.IsBusy = false; }
    }

    // ---------- удаление программы ----------
    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var p = Current;
        if (p == null || _busy) return;

        var cmd = UninstallerService.GetCommand(p, _settings.QuietUninstall);
        if (cmd.Length == 0)
        {
            Tell("Деинсталлятор не найден", $"Для «{p.Name}» не зарегистрирован штатный деинсталлятор.",
                 "Если программа уже удалена вручную, найдите её данные в разделе «Остатки».");
            return;
        }
        if (!Ask("Запуск деинсталлятора", $"Запустить штатный деинсталлятор «{p.Name}»?",
                 $"Команда:\n{cmd}\n\nПосле его завершения вам будет предложено найти оставшиеся данные. Ничего не удаляется без отдельного подтверждения.", "Запустить"))
        {
            AppLog.Instance.Info($"Удаление «{p.Name}» отменено пользователем");
            return;
        }

        SetBusy(true);
        UninstallOutcome outcome;
        try { outcome = await UninstallerService.RunAsync(p, _settings.QuietUninstall, AppLog.Instance); }
        finally { SetBusy(false); }

        await ReloadProgramsAsync();

        if (outcome.Started && !outcome.StillRegistered)
        {
            if (Ask("Программа удалена", $"«{p.Name}» удалена. Найти оставшиеся данные?",
                    "Будет выполнен только поиск. Удаление найденного потребует отдельного подтверждения.", "Найти остатки"))
            {
                ManualName.Text = p.Name; ManualPublisher.Text = p.Publisher; ManualPath.Text = p.InstallLocation;
                NavManual.IsChecked = true;
                await ScanAsync(_manualSet, ProgramQuery.FromProgram(p, trusted: true));
            }
        }
        else Tell("Результат", outcome.Message);
    }

    // ---------- очистка ----------
    async void CleanProgram_Click(object s, RoutedEventArgs e) => await CleanAsync(_programSet, programStillInstalled: true);
    async void ManualScan_Click(object s, RoutedEventArgs e)
    {
        var name = ManualName.Text.Trim();
        if (NameMatcher.Norm(name).Length < 3) { Tell("Остатки", "Введите название программы (не короче 3 символов)."); return; }
        await ScanAsync(_manualSet, new ProgramQuery(name, ManualPublisher.Text.Trim(), ManualPath.Text.Trim(), false));
    }
    async void ManualClean_Click(object s, RoutedEventArgs e) => await CleanAsync(_manualSet, programStillInstalled: false);

    async Task CleanAsync(LeftoverSet set, bool programStillInstalled)
    {
        if (_busy) return;
        var selected = set.Selected.ToList();
        if (selected.Count == 0) { Tell("Ничего не выбрано", "Отметьте каталоги, которые нужно удалить."); return; }

        long total = selected.Sum(i => i.SizeBytes);
        int unverified = selected.Count(i => i.Confidence == Confidence.Unverified);
        var header = new StringBuilder($"Будет безвозвратно удалено каталогов: {selected.Count}. Общий объём: {Format.Size(total)}.\nФайлы удаляются минуя Корзину.");
        if (unverified > 0) header.Append($"\nВНИМАНИЕ: {unverified} каталог(ов) не подтверждены как принадлежащие программе — внимательно проверьте пути.");
        if (programStillInstalled) header.Append("\nПрограмма всё ещё установлена: удаление её данных может нарушить работу.");

        var body = new StringBuilder();
        foreach (var i in selected)
            body.AppendLine($"{(i.Confidence == Confidence.Verified ? "[подтверждено]    " : "[НЕ ПОДТВЕРЖДЕНО]")} {i.SizeText,-14} {i.Path}");

        if (!Ask("Подтверждение удаления", header.ToString(), body.ToString(), "Удалить"))
        {
            AppLog.Instance.Info("Очистка отменена пользователем");
            return;
        }

        var (_, guard) = CreateEngine();
        var cleaner = new Cleaner(guard, AppLog.Instance);
        var progress = new Progress<string>(m => set.Status = m);

        SetBusy(true); set.IsBusy = true;
        CleanupResult result;
        try { result = await Task.Run(() => cleaner.Clean(selected, CancellationToken.None, progress)); }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Сбой очистки: " + ex);
            Tell("Ошибка", "Очистка завершилась сбоем", ex.Message);
            return;
        }
        finally { SetBusy(false); set.IsBusy = false; }

        var done = result.Entries.Where(x => x.Status is CleanStatus.Deleted or CleanStatus.AlreadyGone).Select(x => x.Item).ToHashSet();
        set.RemoveWhere(i => done.Contains(i));
        set.Status = "Очистка завершена. Результат — в окне отчёта и в разделе «Журнал».";

        bool anyProblem = result.Entries.Any(x => x.Status is CleanStatus.Failed or CleanStatus.DeletedPartially or CleanStatus.Skipped);
        Tell("Результат очистки", anyProblem ? "Очистка завершена с ошибками" : "Очистка завершена", result.ToReport());
    }

    // ---------- помощники ----------
    void SetBusy(bool busy) { _busy = busy; PagePrograms.IsEnabled = !busy; PageManual.IsEnabled = !busy; }

    bool Ask(string title, string header, string body, string ok) =>
        new TextDialog(title, header, body, ok, "Отмена") { Owner = this }.ShowDialog() == true;

    void Tell(string title, string header, string body = "") =>
        new TextDialog(title, header, body, "Закрыть", null) { Owner = this }.ShowDialog();
}
