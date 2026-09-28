using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PortLens;

public sealed partial class MainWindow
{
    private readonly PortHistory history = new();
    private readonly FavoritesStore favoritesStore = new();
    private List<FavoritePort> favorites = [];
    private readonly StackPanel favoritesList = new() { Spacing = 10 };
    private readonly StackPanel historyList = new() { Spacing = 10 };
    private readonly TextBlock processInfo = Label("Выбери процесс в списке.");
    private readonly TextBox executable = ReadOnlyField();
    private readonly TextBox commandLine = ReadOnlyField();
    private readonly Button stop = new() { Content = "Остановить процесс…", IsEnabled = false };
    private readonly TextBlock stopReason = Label("");
    private readonly TextBox httpAddress = new() { PlaceholderText = "http://127.0.0.1:8080/" };
    private readonly Button httpCheck = new() { Content = "Проверить HTTP" };
    private readonly TextBlock httpResult = Label("GET-запрос к указанному адресу, ожидание до 5 секунд.");
    private readonly TextBox favoriteNumber = new() { PlaceholderText = "Порт", Width = 95 };
    private readonly ComboBox favoriteProtocol = new() { ItemsSource = new[] { "TCP", "UDP" }, SelectedIndex = 0, Width = 95 };
    private readonly TextBox favoriteLabel = new() { PlaceholderText = "Название: сайт, база, API…", MaxLength = 80 };
    private readonly Button saveFavorite = new() { Content = "Сохранить в избранное" };
    private readonly Button useSelected = new() { Content = "Взять выбранный порт", IsEnabled = false };
    private readonly TextBlock favoritesMessage = Label("");
    private CancellationTokenSource? processRequest;
    private ProcessDetails? inspected;
    private PortEntry? displayedEntry;
    private bool favoritesLoaded;
    private bool stopping;

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A7B0C2") };
    private static TextBox ReadOnlyField() => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 90 };
    private static ScrollViewer Scroll(Control child) => new() { Content = child, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    private Control BuildFeaturePanel()
    {
        var processPanel = new StackPanel { Spacing = 10, Margin = new Thickness(14) };
        processPanel.Children.Add(details);
        processPanel.Children.Add(copy);
        processPanel.Children.Add(new Separator());
        processPanel.Children.Add(processInfo);
        processPanel.Children.Add(Label("Путь к программе"));
        processPanel.Children.Add(executable);
        processPanel.Children.Add(Label("Команда запуска · может содержать личные параметры"));
        processPanel.Children.Add(commandLine);
        processPanel.Children.Add(stop);
        processPanel.Children.Add(stopReason);
        processPanel.Children.Add(new Separator());
        processPanel.Children.Add(new TextBlock { Text = "Проверка веб-сервиса", FontSize = 17, FontWeight = FontWeight.SemiBold });
        processPanel.Children.Add(httpAddress);
        processPanel.Children.Add(httpCheck);
        processPanel.Children.Add(httpResult);
        stop.Click += async (_, _) => await StopSelectedAsync();
        httpCheck.Click += async (_, _) => await CheckHttpAsync();

        var favoritesPanel = new StackPanel { Spacing = 12, Margin = new Thickness(14) };
        favoritesPanel.Children.Add(Label("Закрепи нужные порты. Статус показывает только данные последнего успешного снимка."));
        var favoriteFields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        favoriteFields.Children.Add(favoriteNumber); favoriteFields.Children.Add(favoriteProtocol);
        favoritesPanel.Children.Add(favoriteFields);
        favoritesPanel.Children.Add(favoriteLabel);
        favoritesPanel.Children.Add(useSelected);
        favoritesPanel.Children.Add(saveFavorite);
        favoritesPanel.Children.Add(favoritesMessage);
        favoritesPanel.Children.Add(favoritesList);
        saveFavorite.Click += (_, _) => SaveFavorite();
        useSelected.Click += (_, _) =>
        {
            if (displayedEntry is not { } entry) return;
            favoriteNumber.Text = entry.Port.ToString();
            favoriteProtocol.SelectedItem = entry.Protocol;
            favoriteLabel.Text = entry.Process == "Недоступен" ? "" : entry.Process;
        };

        var historyPanel = new StackPanel { Spacing = 12, Margin = new Thickness(14) };
        historyPanel.Children.Add(Label("Последние 300 событий за время работы окна. Время — момент обнаружения при обновлении. Первый снимок — исходное состояние."));
        var clear = new Button { Content = "Очистить историю" };
        clear.Click += (_, _) => { history.Clear(); RenderHistory(); };
        historyPanel.Children.Add(clear); historyPanel.Children.Add(historyList);
        RenderHistory();
        return new Border {
            Background = Brush.Parse("#1B2230"), CornerRadius = new CornerRadius(12),
            Child = new TabControl { ItemsSource = new[] {
                new TabItem { Header = "Процесс", Content = Scroll(processPanel) },
                new TabItem { Header = "Избранное", Content = Scroll(favoritesPanel) },
                new TabItem { Header = "История", Content = Scroll(historyPanel) }
            } }
        };
    }

    private void UpdateSelectedFeatures(PortEntry? entry)
    {
        useSelected.IsEnabled = entry != null && favoritesLoaded;
        if (Equals(displayedEntry, entry) && entry != null)
        {
            // Обновляем память и доступность процесса вместе со снимком портов.
            _ = InspectSelectedAsync(entry);
            return;
        }
        displayedEntry = entry;
        processRequest?.Cancel();
        inspected = null; stop.IsEnabled = false;
        executable.Text = ""; commandLine.Text = "";
        stopReason.Text = "";
        httpAddress.Text = entry is null ? "" : HttpProbe.SuggestedUrl(entry);
        if (entry == null) { processInfo.Text = "Выбери процесс в списке."; return; }
        _ = InspectSelectedAsync(entry);
    }

    private async Task InspectSelectedAsync(PortEntry entry)
    {
        processRequest?.Cancel();
        processRequest?.Dispose();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        processRequest = request;
        inspected = null; stop.IsEnabled = false;
        if (!int.TryParse(entry.Pid, out var pid))
        {
            processInfo.Text = "ОС не предоставила PID этого сокета.";
            return;
        }
        processInfo.Text = "Читаю сведения о процессе…";
        try
        {
            var info = await ProcessInspector.InspectAsync(pid, request.Token);
            if (request.IsCancellationRequested || !Equals(displayedEntry, entry)) return;
            inspected = info;
            var memory = info.MemoryBytes is { } bytes ? $"{bytes / 1024d / 1024d:F1} МБ" : "недоступна";
            processInfo.Text = $"{info.Name} · PID {info.Pid}\nПамять: {memory}\nЗапущен: {info.StartedAt?.ToLocalTime().ToString("dd.MM HH:mm:ss") ?? "неизвестно"}";
            executable.Text = info.ExecutablePath;
            commandLine.Text = info.CommandLine;
            stopReason.Text = info.CanStop ? "Мягкая остановка (SIGTERM), только после подтверждения." : info.StopReason;
            stop.IsEnabled = info.CanStop && !stopping;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (request.IsCancellationRequested) return;
            executable.Text = ""; commandLine.Text = "";
            processInfo.Text = "Сведения недоступны: " + error.Message;
            stopReason.Text = "Процесс мог завершиться или ОС ограничила доступ.";
        }
    }

    private async Task CheckHttpAsync()
    {
        var address = httpAddress.Text ?? "";
        httpCheck.IsEnabled = false;
        httpResult.Text = "Отправляю запрос…";
        try
        {
            var result = await HttpProbe.CheckAsync(address, lifetime.Token);
            httpResult.Text = $"{result.Address}\n{result.Message} · {result.ElapsedMilliseconds} мс";
        }
        catch (OperationCanceledException) { httpResult.Text = "Проверка отменена."; }
        catch (Exception error) { httpResult.Text = "Ошибка: " + error.Message; }
        finally { httpCheck.IsEnabled = true; }
    }

    private async Task StopSelectedAsync()
    {
        if (inspected is not { CanStop: true } target || stopping) return;
        stopping = true; stop.IsEnabled = false;
        try
        {
            if (!await ConfirmStopAsync(target)) return;
            notice.Text = await ProcessInspector.StopAsync(target, lifetime.Token);
            await RefreshAsync();
        }
        catch (OperationCanceledException) { notice.Text = "Остановка отменена."; }
        catch (Exception error) { notice.Text = "Не удалось остановить: " + error.Message; }
        finally { stopping = false; stop.IsEnabled = inspected?.CanStop == true; }
    }

    private Task<bool> ConfirmStopAsync(ProcessDetails target)
    {
        var dialog = new Window { Title = "Остановить процесс?", Width = 500, Height = 330, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = $"{target.Name} · PID {target.Pid}", FontSize = 21, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Label("Процесс получит SIGTERM. Его работа и соединения прервутся, несохранённые данные могут потеряться. Это может затронуть несколько портов."));
        body.Children.Add(Label("Перед отправкой сигнала PortLens повторно проверит владельца и время запуска процесса."));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        var confirm = new Button { Content = "Остановить", Foreground = Brush.Parse("#FFA8A8") };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(cancel); actions.Children.Add(confirm); body.Children.Add(actions);
        dialog.Content = Scroll(body);
        return dialog.ShowDialog<bool>(this);
    }

    private void LoadFavorites()
    {
        try { favorites = favoritesStore.Load(); favoritesLoaded = true; RenderFavorites(); }
        catch (Exception error)
        {
            favoritesLoaded = false; saveFavorite.IsEnabled = false;
            favoritesMessage.Text = "Не удалось прочитать избранное: " + error.Message;
        }
    }

    private void SaveFavorite()
    {
        if (!favoritesLoaded) return;
        if (!int.TryParse(favoriteNumber.Text, out var port) || port is < 1 or > 65535)
        { favoritesMessage.Text = "Введи номер порта от 1 до 65535."; return; }
        var protocolName = favoriteProtocol.SelectedItem as string ?? "TCP";
        var item = new FavoritePort(port, protocolName, favoriteLabel.Text?.Trim() ?? "");
        var next = favorites.Where(f => f.Port != port || f.Protocol != protocolName).ToList();
        next.Add(item);
        SaveFavorites(next);
    }

    private void SaveFavorites(List<FavoritePort> next)
    {
        try
        {
            favoritesStore.Save(next);
            favorites = next;
            favoritesMessage.Text = "Избранное сохранено.";
            RenderFavorites();
        }
        catch (Exception error) { favoritesMessage.Text = "Не удалось сохранить: " + error.Message; }
    }

    private void RenderFavorites()
    {
        favoritesList.Children.Clear();
        if (favorites.Count == 0) favoritesList.Children.Add(Label("Пока нет избранных портов."));
        foreach (var favorite in favorites.OrderBy(f => f.Port).ThenBy(f => f.Protocol))
        {
            var matches = entries.Where(e => e.Port == favorite.Port && e.Protocol == favorite.Protocol).ToList();
            var card = new StackPanel { Spacing = 8 };
            card.Children.Add(new TextBlock { Text = $"{favorite.Label} · {favorite.Port}/{favorite.Protocol}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(Label(!capturedAt.HasValue ? "Статус ещё не получен" : matches.Count == 0
                ? "Не найден в снимке (это не гарантия свободного порта)"
                : "Используется: " + string.Join(", ", matches.Select(e => e.Process).Distinct())));
            if (capturedAt is { } time) card.Children.Add(Label($"Снимок: {time:HH:mm:ss}"));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var find = new Button { Content = "Показать" };
            var remove = new Button { Content = "Убрать" };
            find.Click += (_, _) => { search.Text = favorite.Port.ToString(); protocol.SelectedItem = favorite.Protocol; };
            remove.Click += (_, _) => SaveFavorites(favorites.Where(f => f != favorite).ToList());
            actions.Children.Add(find); actions.Children.Add(remove); card.Children.Add(actions);
            favoritesList.Children.Add(new Border { BorderBrush = Brush.Parse("#354052"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = card });
        }
    }

    private void RenderHistory()
    {
        historyList.Children.Clear();
        if (history.Events.Count == 0) historyList.Children.Add(Label("Изменений пока нет. Включи живой режим или обнови список вручную."));
        foreach (var item in history.Events)
        {
            var entry = item.Entry;
            historyList.Children.Add(new TextBlock {
                Text = $"{item.Time:HH:mm:ss}  {(item.Appeared ? "+ Появился" : "− Исчез")}  {entry.Protocol} {entry.Endpoint}\n{entry.Process} · PID {entry.Pid}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush.Parse(item.Appeared ? "#80E5C0" : "#FFBC96")
            });
        }
    }
}
