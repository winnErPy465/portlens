using PortLens;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Ошибка: " + name);
    Console.WriteLine("OK: " + name);
}

static void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { Console.WriteLine("OK: " + name); return; }
    throw new Exception("Ожидалась ошибка: " + name);
}

var web = new PortEntry("TCP", "127.0.0.1", 8080, "dotnet", "120");
var db = new PortEntry("TCP", "[::1]", 5432, "postgres", "121");
var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
var history = new PortHistory();
history.Update([web, web], now);
Check(history.Events.Count == 0, "Первый снимок не создаёт события");
history.Update([web], now.AddSeconds(1));
Check(history.Events.Count == 0, "Повторы строк не создают события");
history.Update([web, db], now.AddSeconds(2));
Check(history.Events.Count == 1 && history.Events[0] == new PortEvent(now.AddSeconds(2), true, db),
    "Появление порта сохраняется со временем");
history.Update([db], now.AddSeconds(3));
Check(history.Events.Count == 2 && history.Events[0] == new PortEvent(now.AddSeconds(3), false, web),
    "Исчезнувший порт в начале журнала");
history.Clear();
history.Update([db], now.AddSeconds(4));
Check(history.Events.Count == 0, "Очистка журнала сохраняет точку отсчёта");
history.Update([db with { Pid = "122" }], now.AddSeconds(5));
Check(history.Events.Count == 2 && history.Events.Count(e => e.Appeared) == 1,
    "Замена процесса на том же порту видна в истории");
history.Update([], now.AddSeconds(6));
Check(history.Events[0].Entry.Pid == "122" && !history.Events[0].Appeared,
    "Пустой успешный снимок отмечает исчезновение");
for (var i = 0; i < 400; i++) history.Update([web with { Pid = i.ToString() }], now.AddSeconds(10 + i));
Check(history.Events.Count == PortHistory.Limit && history.Events[0].Time == now.AddSeconds(409),
    "Журнал ограничен и сохраняет новые события");

var temporary = Path.Combine(Path.GetTempPath(), "portlens-favorites-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var path = Path.Combine(temporary, "settings", "favorites.json");
    var store = new FavoritesStore(path);
    Check(store.Load().Count == 0 && !Directory.Exists(Path.GetDirectoryName(path)),
        "Первый запуск возвращает пустой список без записи на диск");
    store.Save([new(8080, " tcp ", "  Мой сайт  "), new(8080, "UDP", "Локальная служба")]);
    var loaded = new FavoritesStore(path).Load();
    Check(loaded.SequenceEqual([new FavoritePort(8080, "TCP", "Мой сайт"), new FavoritePort(8080, "UDP", "Локальная служба")]),
        "Избранное переживает перезапуск, нормализует пробелы и разделяет TCP/UDP");
    Check(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "Временные файлы удалены после записи");
    var before = File.ReadAllText(path);
    Reject(() => store.Save([new(8080, "TCP", "Первый"), new(8080, "tcp", "Второй")]),
        "Повтор одного порта и протокола запрещён");
    Reject(() => store.Save([new(0, "TCP", "")]), "Нулевой порт запрещён");
    Reject(() => store.Save([new(65536, "TCP", "")]), "Порт выше 65535 запрещён");
    Reject(() => store.Save([new(80, "HTTP", "")]), "Неизвестный протокол запрещён");
    Reject(() => store.Save([new(80, "TCP", new string('я', 81))]), "Длинная подпись запрещена");
    Reject(() => store.Save([new(80, "TCP", "Сайт\nВторая строка")]), "Переносы в подписи запрещены");
    Check(File.ReadAllText(path) == before, "Ошибки ввода не меняют сохранённое избранное");
    File.WriteAllText(path, "{ испорченный файл");
    Reject(() => store.Load(), "Повреждённый файл сообщает об ошибке");
    Reject(() => store.Save([]), "Сохранение не затирает повреждённый файл");
    Check(File.ReadAllText(path) == "{ испорченный файл", "Повреждённые данные сохранены для восстановления");
    File.WriteAllText(path, "[{\"Port\":80,\"Protocol\":\"TCP\",\"Label\":\"a\"},{\"Port\":80,\"Protocol\":\"TCP\",\"Label\":\"b\"}]");
    Reject(() => store.Load(), "Повторы в загружаемом файле тоже проверяются");
    File.WriteAllText(path, "null");
    Reject(() => store.Load(), "JSON null не считается пустым списком");
    File.WriteAllText(path, "[null]");
    Reject(() => store.Load(), "JSON null внутри списка не считается портом");
    File.WriteAllText(path, "[]");
    store.Save([new(1, "TCP", ""), new(65535, "UDP", "")]);
    Check(store.Load().Count == 2, "Крайние допустимые номера сохраняются");
    store.Save([]);
    Check(store.Load().Count == 0, "Удаление всех избранных сохраняется");
}
finally
{
    // Удаляем только отдельную временную папку, которую создал этот тест.
    Directory.Delete(temporary, recursive: true);
}
Console.WriteLine("Все проверки истории и избранного пройдены.");
