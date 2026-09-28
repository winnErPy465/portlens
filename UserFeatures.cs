using System.Text.Json;

namespace PortLens;

public sealed record PortEvent(DateTimeOffset Time, bool Appeared, PortEntry Entry);

public sealed class PortHistory
{
    public const int Limit = 300;
    private readonly List<PortEvent> _events = [];
    private HashSet<PortEntry>? _previous;

    public IReadOnlyList<PortEvent> Events => _events.AsReadOnly();

    // Первый снимок — точка отсчёта. Ошибочные проверки сюда не передаём.
    public void Update(IEnumerable<PortEntry> entries, DateTimeOffset time)
    {
        var current = entries.ToHashSet();
        if (_previous is not null)
        {
            var changes = current.Except(_previous).Select(entry => new PortEvent(time, true, entry))
                .Concat(_previous.Except(current).Select(entry => new PortEvent(time, false, entry)))
                .OrderBy(change => change.Entry.Port)
                .ThenBy(change => change.Entry.Protocol, StringComparer.Ordinal)
                .ThenBy(change => change.Entry.Address, StringComparer.Ordinal)
                .ThenBy(change => change.Entry.Pid, StringComparer.Ordinal)
                .ThenBy(change => change.Appeared)
                .ToList();
            _events.InsertRange(0, changes);
            if (_events.Count > Limit) _events.RemoveRange(Limit, _events.Count - Limit);
        }
        _previous = current;
    }

    // Очистка журнала не сбрасывает текущий список портов.
    public void Clear() => _events.Clear();
}

public sealed record FavoritePort(int Port, string Protocol, string Label);

public sealed class FavoritesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; }

    public FavoritesStore(string? path = null) => FilePath = Path.GetFullPath(path ?? DefaultPath());

    public List<FavoritePort> Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var favorites = JsonSerializer.Deserialize<List<FavoritePort>>(stream, JsonOptions);
            return Validate(favorites ?? throw new InvalidDataException("Ожидался список избранных портов."));
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Не удалось прочитать избранное: файл {FilePath} повреждён. "
                + "Исправь файл или переименуй его, чтобы создать новый. " + error.Message, error);
        }
    }

    public void Save(IEnumerable<FavoritePort> favorites)
    {
        var normalized = Validate(favorites);
        // Не заменяем повреждённый файл пустым списком после ошибки загрузки.
        _ = Load();
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // Сначала полностью записываем новый файл, затем заменяем старый.
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static List<FavoritePort> Validate(IEnumerable<FavoritePort> favorites)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        var result = new List<FavoritePort>();
        var keys = new HashSet<(int Port, string Protocol)>();
        foreach (var favorite in favorites)
        {
            if (favorite is null || favorite.Port is < 1 or > 65535)
                throw new InvalidDataException("Номер избранного порта должен быть от 1 до 65535.");
            var protocol = favorite.Protocol?.Trim().ToUpperInvariant();
            if (protocol is not ("TCP" or "UDP"))
                throw new InvalidDataException("Для избранного порта выбери TCP или UDP.");
            var label = favorite.Label?.Trim() ?? "";
            if (label.Length > 80 || label.Any(char.IsControl))
                throw new InvalidDataException("Название порта должно быть не длиннее 80 символов и без переносов строк.");
            if (!keys.Add((favorite.Port, protocol)))
                throw new InvalidDataException($"Порт {favorite.Port}/{protocol} уже есть в избранном.");
            result.Add(new FavoritePort(favorite.Port, protocol, label));
        }
        return result;
    }

    private static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "PortLens", "favorites.json");
        if (OperatingSystem.IsLinux())
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config) || !Path.IsPathFullyQualified(config))
                config = Path.Combine(home, ".config");
            return Path.Combine(config, "portlens", "favorites.json");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortLens", "favorites.json");
    }
}
