using PortLens;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Ошибка: " + name);
    Console.WriteLine("OK: " + name);
}

var linux = PortScanner.ParseSs("""
tcp LISTEN 0 128 127.0.0.1:8080 0.0.0.0:* users:(("dotnet",pid=123,fd=7))
udp UNCONN 0 0 [::]:5353 [::]:*
tcp LISTEN 0 128 0.0.0.0:443 0.0.0.0:* users:(("nginx",pid=5,fd=1),("nginx",pid=6,fd=1))
malformed
""");
Check(linux.Count == 4, "Linux: несколько владельцев и неизвестный процесс");
Check(linux.Single(e => e.Port == 8080).Pid == "123", "Linux: PID");
Check(linux.Single(e => e.Port == 5353).Address == "[::]", "Linux: IPv6");
var mac = PortScanner.ParseLsof("p45\ncExample App\nPTCP\nn[::1]:8000\nPUDP\nn127.0.0.1:5000->1.2.3.4:53\n");
Check(mac.Count == 2 && mac[0].Port == 5000, "macOS: локальный UDP-порт");
Check(mac[1].Process == "Example App" && mac[1].Address == "[::1]", "macOS: имя с пробелом и IPv6");
Check(PortScanner.ParseSs("").Count == 0 && PortScanner.ParseLsof("").Count == 0, "Пустые списки");

var entries = new[]
{
    new PortEntry("TCP", "127.0.0.1", 8080, "Web App", "123"),
    new PortEntry("UDP", "[::]", 5353, "Resolver", "456"),
    new PortEntry("TCP", "0.0.0.0", 443, "nginx", "789")
};
Check(SnapshotTools.Filter(entries, "  WEB  ", null).SequenceEqual([entries[0]]),
    "Поиск: пробелы и регистр не мешают найти процесс");
Check(SnapshotTools.Filter(entries, "8080", "UDP").Count == 0,
    "Поиск и протокол применяются вместе");
Check(SnapshotTools.Filter(entries, "", "tcp").SequenceEqual([entries[0], entries[2]]),
    "Фильтр TCP сохраняет порядок строк");
Check(SnapshotTools.Filter(entries, "[::]", null).SequenceEqual([entries[1]])
    && SnapshotTools.Filter(entries, "789", null).SequenceEqual([entries[2]]),
    "Поиск по адресу и PID");
Check(SnapshotTools.Filter(entries, "   ", "Все протоколы").Count == 3,
    "Пустой поиск показывает весь список");

var newOwner = entries[0] with { Pid = "124" };
Check(SnapshotTools.Added(entries, [entries[1], newOwner, newOwner]).SetEquals([newOwner]),
    "Изменения: новый PID обнаружен, повторы и исчезнувшие порты исключены");
Check(SnapshotTools.Added(entries, entries.Reverse()).Count == 0,
    "Перестановка строк не считается изменением");
Check(SnapshotTools.Added([], entries).Count == 3 && SnapshotTools.Added(entries, []).Count == 0,
    "Изменения: пустой предыдущий и текущий список");

var capturedAt = new DateTimeOffset(2026, 9, 18, 12, 34, 56, TimeSpan.FromHours(2));
const string csvHeader = "captured_at,protocol,address,port,process,pid\r\n";
Check(SnapshotTools.ToCsv([], capturedAt) == csvHeader, "CSV: пустой список содержит заголовок");
var csv = SnapshotTools.ToCsv([entries[0], entries[1]], capturedAt);
Check(csv == csvHeader
    + "2026-09-18T12:34:56.0000000+02:00,TCP,127.0.0.1,8080,Web App,123\r\n"
    + "2026-09-18T12:34:56.0000000+02:00,UDP,[::],5353,Resolver,456\r\n",
    "CSV: время снимка, порядок полей и переносы CRLF");
var quoted = entries[0] with { Process = "Сервис, \"тест\"\nвторая строка" };
Check(SnapshotTools.ToCsv([quoted], capturedAt).Contains("\"Сервис, \"\"тест\"\"\nвторая строка\""),
    "CSV: запятые, кавычки, переносы и кириллица");
foreach (var prefix in new[] { "=", "+", "-", "@", "  =", "\t", "\r", "\n" })
{
    var suspicious = entries[0] with { Process = prefix + "command", Address = prefix + "address" };
    var protectedCsv = SnapshotTools.ToCsv([suspicious], capturedAt);
    Check(protectedCsv.Contains("'" + prefix + "command") && protectedCsv.Contains("'" + prefix + "address"),
        $"CSV: защита от формул ({prefix.Replace("\t", "TAB").Replace("\r", "CR").Replace("\n", "LF")})");
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var live = await PortScanner.ScanAsync(timeout.Token);
Console.WriteLine($"Живая проверка: {live.Count} сокетов.");
