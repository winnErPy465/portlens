using System.Globalization;
using System.Text;

namespace PortLens;

public static class SnapshotTools
{
    public static List<PortEntry> Filter(IEnumerable<PortEntry> entries, string query, string? protocol)
    {
        query = query.Trim();
        var selected = protocol?.ToUpperInvariant();
        return entries.Where(entry => (selected is not ("TCP" or "UDP")
                || string.Equals(entry.Protocol, selected, StringComparison.OrdinalIgnoreCase))
            && $"{entry.Port} {entry.Address} {entry.Process} {entry.Pid}"
                .Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static HashSet<PortEntry> Added(IEnumerable<PortEntry> previous, IEnumerable<PortEntry> current)
    {
        // Новый владелец того же порта тоже считается изменением.
        var added = current.ToHashSet();
        added.ExceptWith(previous);
        return added;
    }

    public static string ToCsv(IEnumerable<PortEntry> entries, DateTimeOffset capturedAt)
    {
        var csv = new StringBuilder("captured_at,protocol,address,port,process,pid\r\n");
        var timestamp = capturedAt.ToString("O", CultureInfo.InvariantCulture);
        foreach (var entry in entries)
        {
            csv.AppendJoin(',', timestamp, Escape(entry.Protocol), Escape(entry.Address),
                entry.Port.ToString(CultureInfo.InvariantCulture), Escape(entry.Process), Escape(entry.Pid));
            csv.Append("\r\n");
        }
        return csv.ToString();
    }

    private static string Escape(string value)
    {
        // Апостроф не даёт таблицам принять название процесса за формулу.
        var trimmed = value.TrimStart();
        if ((value.Length > 0 && value[0] is '\t' or '\r' or '\n')
            || (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@'))
            value = "'" + value;

        // Запятые, кавычки и переносы строк должны остаться внутри одной ячейки.
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
