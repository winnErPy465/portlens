using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;

namespace PortLens;

public sealed partial class MainWindow : Window
{
    private readonly TextBox search = new() { PlaceholderText = "Найти порт, процесс, PID или адрес…", MinWidth = 300 };
    private readonly ComboBox protocol = new() { ItemsSource = new[] { "Все протоколы", "TCP", "UDP" }, SelectedIndex = 0, Width = 165 };
    private readonly Button refresh = new() { Content = "Обновить", Padding = new Thickness(22, 10) };
    private readonly CheckBox live = new() { Content = "Живой режим · 5 сек", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button export = new() { Content = "Сохранить CSV", IsEnabled = false };
    private readonly Button copy = new() { Content = "Копировать адрес", IsEnabled = false };
    private readonly TextBlock changes = new() { Text = "Первый снимок ещё не получен", Foreground = Brush.Parse("#80E5C0"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock notice = new() { Foreground = Brush.Parse("#80E5C0"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summary = new() { FontSize = 24, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A7B0C2") };
    private readonly TextBlock details = new() { Text = "Выбери строку, чтобы посмотреть подробности.", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox ports = new() { Background = Brushes.Transparent };
    private readonly CancellationTokenSource lifetime = new();
    private List<PortEntry> entries = [];
    private HashSet<PortEntry> added = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DateTimeOffset? capturedAt;
    private bool scanning;
    private bool updatingRows;

    public MainWindow()
    {
        Title = "PortLens";
        Width = 1280; Height = 860; MinWidth = 1060; MinHeight = 740;
        Background = Brush.Parse("#10141D");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto"), Margin = new Thickness(26) };
        var heading = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 24) };
        heading.Children.Add(new TextBlock { Text = "◉  PortLens", FontSize = 32, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#80E5C0") });
        heading.Children.Add(new TextBlock { Text = "Узнай, что занимает твои порты.", FontSize = 15, Foreground = Brush.Parse("#A7B0C2") });
        Add(root, heading, 0);
        Add(root, summary, 1);
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 20, 0, 20) };
        filters.Children.Add(search); Grid.SetColumn(protocol, 1); filters.Children.Add(protocol); Grid.SetColumn(refresh, 2); filters.Children.Add(refresh);
        Add(root, filters, 2);
        var liveBar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 20, Margin = new Thickness(0, 0, 0, 14) };
        liveBar.Children.Add(live); Grid.SetColumn(changes, 1); liveBar.Children.Add(changes); Grid.SetColumn(export, 2); liveBar.Children.Add(export);
        Add(root, liveBar, 3);
        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 18 };
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Add(table, Row("ПОРТ", "ТИП", "ПРОЦЕСС", "АДРЕС", "PID", true), 0);
        Add(table, ports, 1);
        workspace.Children.Add(table);
        var sidePanel = BuildFeaturePanel();
        Grid.SetColumn(sidePanel, 1); workspace.Children.Add(sidePanel);
        Add(root, workspace, 4);
        notice.Margin = new Thickness(0, 10);
        Add(root, notice, 5);
        Add(root, status, 6);
        Content = root;
        search.TextChanged += (_, _) => ApplyFilter();
        protocol.SelectionChanged += (_, _) => ApplyFilter();
        ports.SelectionChanged += (_, _) => { if (!updatingRows) UpdateDetails(); };
        refresh.Click += async (_, _) => await RefreshAsync();
        copy.Click += async (_, _) => await CopyAsync();
        export.Click += async (_, _) => await ExportAsync();
        timer.Tick += async (_, _) => { if (live.IsChecked == true) await RefreshAsync(); };
        Opened += async (_, _) => { LoadFavorites(); timer.Start(); await RefreshAsync(); };
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); processRequest?.Cancel(); };
    }

    private static void Add(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }

    private static Grid Row(string port, string type, string process, string address, string pid, bool header = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("65,45,*,*,65"), ColumnSpacing = 8, Margin = new Thickness(8, 10) };
        var values = new[] { port, type, process, address, pid };
        for (int i = 0; i < values.Length; i++)
        {
            var text = new TextBlock { Text = values[i], FontSize = header ? 11 : 14, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush.Parse(header ? "#8793A8" : i == 0 ? "#80E5C0" : "#E0E5EF") };
            ToolTip.SetTip(text, values[i]);
            Grid.SetColumn(text, i); row.Children.Add(text);
        }
        return row;
    }

    private void ApplyFilter()
    {
        var selection = (ports.SelectedItem as ListBoxItem)?.Tag as PortEntry;
        var visible = VisibleEntries();
        summary.Text = $"Сокетов в списке: {visible.Count}  /  Портов всего: {entries.Select(e => e.Port).Distinct().Count()}";
        var rows = visible.Select(e => new ListBoxItem {
            Tag = e,
            Content = Row((added.Contains(e) ? "+ " : "") + e.Port, e.Protocol, e.Process, e.Address, e.Pid),
            Background = added.Contains(e) ? Brush.Parse("#193A32") : Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        }).ToList();
        updatingRows = true;
        try
        {
            ports.ItemsSource = rows;
            // При обновлении сохраняем выбранный сокет, если он всё ещё есть.
            ports.SelectedItem = rows.Find(row => Equals(row.Tag, selection));
        }
        finally { updatingRows = false; }
        UpdateDetails();
        export.IsEnabled = capturedAt.HasValue;
    }

    private List<PortEntry> VisibleEntries() => SnapshotTools.Filter(entries, search.Text ?? "", protocol.SelectedItem as string);

    private void UpdateDetails()
    {
        var entry = (ports.SelectedItem as ListBoxItem)?.Tag as PortEntry;
        copy.IsEnabled = entry != null;
        details.Text = entry is null
            ? VisibleEntries().Count == 0 ? "Ничего не найдено. Попробуй обновить список или изменить фильтр." : "Выбери строку, чтобы посмотреть подробности."
            : $"{entry.Process}  •  PID {entry.Pid}\n{entry.Protocol}  {entry.Endpoint}  •  {entry.Exposure}\nПривязка к адресу не означает, что порт доступен через брандмауэр.";
        UpdateSelectedFeatures(entry);
    }

    private async Task CopyAsync()
    {
        if (ports.SelectedItem is not ListBoxItem { Tag: PortEntry entry }) return;
        try
        {
            if (Clipboard is null) throw new InvalidOperationException("Буфер обмена недоступен.");
            await Clipboard.SetTextAsync(entry.Endpoint);
            notice.Text = $"Скопировано: {entry.Endpoint}";
        }
        catch (Exception error) { notice.Text = "Не удалось скопировать: " + error.Message; }
    }

    private async Task ExportAsync()
    {
        if (capturedAt is not { } time) return;
        // Фиксируем список до открытия диалога: живой режим может обновить окно.
        var snapshot = VisibleEntries();
        try
        {
            if (!StorageProvider.CanSave) throw new InvalidOperationException("Диалог сохранения недоступен.");
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Сохранить текущий список портов", SuggestedFileName = $"portlens-{time:yyyyMMdd-HHmmss}.csv",
                DefaultExtension = "csv", ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
            });
            if (file is null) return;
            await using (var stream = await file.OpenWriteAsync())
            {
                if (stream.CanSeek) stream.SetLength(0);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                await writer.WriteAsync(SnapshotTools.ToCsv(snapshot, time));
            }
            notice.Text = $"Сохранено строк: {snapshot.Count} · {file.Name}";
        }
        catch (Exception error) { notice.Text = "Не удалось сохранить CSV: " + error.Message; }
    }

    private async Task RefreshAsync()
    {
        if (scanning || lifetime.IsCancellationRequested) return;
        scanning = true; refresh.IsEnabled = false;
        status.Text = "Читаю локальные сокеты…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var next = await PortScanner.ScanAsync(timeout.Token);
            added = capturedAt.HasValue ? SnapshotTools.Added(entries, next) : [];
            var removed = capturedAt.HasValue ? SnapshotTools.Added(next, entries).Count : 0;
            changes.Text = capturedAt.HasValue ? $"Появилось: {added.Count} · Исчезло: {removed}" : "Первый снимок · ждём изменений";
            entries = next;
            capturedAt = DateTimeOffset.Now;
            history.Update(entries, capturedAt.Value);
            RenderHistory();
            RenderFavorites();
            ApplyFilter();
            status.Text = $"Обновлено в {DateTime.Now:HH:mm:ss} · TCP: слушающие / UDP: локальные сокеты\nБез прав администратора часть процессов и сокетов может быть недоступна.";
        }
        catch (Exception error)
        {
            status.Text = error is OperationCanceledException ? "Обновление отменено или заняло больше 15 секунд." : "Не удалось обновить: " + error.Message;
            status.Text += "\nЕсли в списке есть данные, это предыдущий снимок.";
            changes.Text = "Снимок не обновлён";
        }
        finally { scanning = false; refresh.IsEnabled = true; }
    }
}
