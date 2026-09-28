using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PortLens;

public sealed record PortEntry(string Protocol, string Address, int Port, string Process, string Pid)
{
    public string Endpoint => $"{Address}:{Port}";
    public string Exposure => Address is "*" or "0.0.0.0" or "[::]" or "::"
        ? "Все интерфейсы" : Address is "127.0.0.1" or "[::1]" or "::1" ? "Локальный" : "Конкретный адрес";
}

public static class PortScanner
{
    public static async Task<List<PortEntry>> ScanAsync(CancellationToken token)
    {
        if (OperatingSystem.IsLinux())
        {
            var result = await RunAsync("ss", ["-H", "-lntup"], token);
            if (result.Code != 0) throw new InvalidOperationException("ss: " + result.Error);
            return ParseSs(result.Output);
        }
        if (OperatingSystem.IsMacOS())
        {
            var result = await RunAsync("/usr/sbin/lsof", ["-nP", "-iTCP", "-sTCP:LISTEN", "-iUDP", "-FpcPn"], token);
            // lsof возвращает 1, если подходящих сокетов нет.
            if (result.Code != 0 && (result.Output.Length > 0 || result.Error.Length > 0))
                throw new InvalidOperationException("lsof: " + result.Error);
            return ParseLsof(result.Output);
        }
        throw new PlatformNotSupportedException("Эта версия поддерживает Linux и macOS.");
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string file, string[] args, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(file)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException($"Не найден {file}. На Linux установи пакет iproute2 (или iproute в Fedora)."); }
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    public static List<PortEntry> ParseSs(string output)
    {
        var entries = new List<PortEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = Regex.Split(line.Trim(), @"\s+", RegexOptions.None);
            if (fields.Length < 6 || fields[0] is not ("tcp" or "udp")) continue;
            if (!TryEndpoint(fields[4], out var address, out var port)) continue;
            var owners = Regex.Matches(line, "\\(\"([^\"]+)\",pid=(\\d+)");
            if (owners.Count == 0) entries.Add(new(fields[0].ToUpperInvariant(), address, port, "Недоступен", "—"));
            foreach (Match owner in owners)
                entries.Add(new(fields[0].ToUpperInvariant(), address, port, owner.Groups[1].Value, owner.Groups[2].Value));
        }
        return Sort(entries);
    }

    public static List<PortEntry> ParseLsof(string output)
    {
        var entries = new List<PortEntry>();
        string pid = "—", name = "Недоступен", protocol = "";
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line[1..];
            switch (line[0])
            {
                case 'p': pid = value; name = "Недоступен"; protocol = ""; break;
                case 'c': name = value; break;
                case 'P': protocol = value; break;
                case 'n':
                    // У подключённого UDP-сокета нужен только локальный адрес.
                    var endpoint = value.Split("->")[0].Split(' ')[0];
                    if (protocol is "TCP" or "UDP" && TryEndpoint(endpoint, out var address, out var port))
                        entries.Add(new(protocol, address, port, name, pid));
                    break;
            }
        }
        return Sort(entries);
    }

    private static List<PortEntry> Sort(List<PortEntry> entries) => entries.Distinct()
        .OrderBy(x => x.Port).ThenBy(x => x.Protocol).ThenBy(x => x.Address).ToList();

    private static bool TryEndpoint(string text, out string address, out int port)
    {
        var colon = text.LastIndexOf(':');
        address = colon >= 0 ? text[..colon] : text;
        port = 0;
        return colon >= 0 && int.TryParse(text[(colon + 1)..], out port) && port is >= 0 and <= 65535;
    }
}
