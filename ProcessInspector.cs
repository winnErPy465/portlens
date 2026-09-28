using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PortLens;

public sealed record ProcessDetails(int Pid, string Name, string ExecutablePath,
    string CommandLine, long? MemoryBytes, DateTimeOffset? StartedAt, bool CanStop, string StopReason)
{
    // Метка запуска помогает отличить старый процесс от нового с тем же PID.
    internal string IdentityKey { get; init; } = "";
    internal uint? OwnerId { get; init; }
}

public static class ProcessInspector
{
    private const string Unavailable = "Недоступно";
    private const int SigTerm = 15;

    public static async Task<ProcessDetails> InspectAsync(int pid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureSupported();
        if (pid <= 0) throw new InvalidOperationException("У этого сокета нет доступного PID.");

        try
        {
            using var process = Process.GetProcessById(pid);
            var before = ReadIdentity(pid);
            var name = TryRead(() => process.ProcessName) ?? Unavailable;
            var memory = TryReadValue(() => process.WorkingSet64);
            var started = TryReadValue(() => new DateTimeOffset(process.StartTime).ToUniversalTime());
            var command = OperatingSystem.IsLinux()
                ? ReadLinuxCommand(pid)
                : await ReadMacCommandAsync(pid, token);
            var after = ReadIdentity(pid);
            if (process.HasExited) throw new InvalidOperationException("Процесс уже завершился. Обнови список портов.");

            var reason = StopRestriction(pid, after, Native.GetEffectiveUid());
            if (before != after) reason = "Процесс изменился во время проверки. Открой карточку ещё раз.";
            if (reason.Length == 0 && OperatingSystem.IsLinux())
            {
                // На Linux отправляем сигнал через дескриптор, который не зависит от повторного использования PID.
                var fd = OpenPidFd(pid);
                if (fd < 0) reason = "Безопасная остановка недоступна: нужен Linux с поддержкой pidfd (ядро 5.3+).";
                else Native.Close(fd);
            }
            return new(pid, name, after.Path.Length > 0 ? after.Path : Unavailable,
                command, memory, started, reason.Length == 0, reason)
            { IdentityKey = after.Key, OwnerId = after.EffectiveUid };
        }
        catch (ArgumentException)
        { throw new InvalidOperationException("Процесс уже завершился. Обнови список портов."); }
        catch (Win32Exception)
        { throw new InvalidOperationException("Не удалось прочитать процесс: он завершился или доступ ограничен."); }
    }

    public static async Task<string> StopAsync(ProcessDetails expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureSupported();
        if (!expected.CanStop || expected.IdentityKey.Length == 0)
            throw new InvalidOperationException(expected.StopReason.Length > 0
                ? expected.StopReason : "Сначала открой свежую карточку процесса.");

        var fd = -1;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                fd = OpenPidFd(expected.Pid);
                if (fd < 0) throw new InvalidOperationException("Не удалось безопасно открыть процесс. Обнови его карточку.");
            }

            // После подтверждения снова проверяем владельца, путь и время запуска.
            var current = ReadIdentity(expected.Pid);
            var reason = StopRestriction(expected.Pid, current, Native.GetEffectiveUid());
            if (reason.Length > 0) throw new InvalidOperationException(reason);
            if (current.Key != expected.IdentityKey || current.EffectiveUid != expected.OwnerId
                || current.Path != expected.ExecutablePath)
                throw new InvalidOperationException("Процесс изменился после открытия карточки. Проверь его ещё раз.");
            token.ThrowIfCancellationRequested();

            // На macOS нет pidfd: остаётся короткое окно между этой проверкой и отправкой сигнала.
            // SIGTERM просит программу завершиться; принудительную остановку не используем.
            var result = OperatingSystem.IsLinux()
                ? Native.PidFdSendSignal(424, fd, SigTerm, IntPtr.Zero, 0)
                : Native.Signal(expected.Pid, SigTerm);
            if (result != 0)
                throw new InvalidOperationException(Marshal.GetLastPInvokeError() == 3
                    ? "Процесс уже завершился. Обнови список портов."
                    : "Система не разрешила остановить процесс.");
        }
        finally
        {
            if (fd >= 0) Native.Close(fd);
        }

        // Проверяем результат без повторного сигнала: программа может сохранять данные или отклонить SIGTERM.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var current = ReadIdentity(expected.Pid);
            if ((current.Key.Length > 0 && current.Key != expected.IdentityKey) || current.Exited
                || HasProcessExited(expected.Pid))
                return "Процесс завершился.";
            // Сигнал уже отправлен, поэтому отмена ожидания не должна выглядеть как отмена действия.
            if (token.IsCancellationRequested) break;
            await Task.Delay(150);
        }
        return "Запрос на завершение отправлен (SIGTERM). Процесс пока работает; принудительной остановки нет.";
    }

    internal static string StopRestriction(int pid, Identity identity, uint ownUid)
    {
        if (pid <= 1) return "Системные процессы с PID 0 и 1 нельзя останавливать.";
        if (pid == Environment.ProcessId) return "PortLens не останавливает сам себя.";
        if (identity.Exited) return "Процесс уже завершился. Обнови список портов.";
        if (identity.EffectiveUid is null || identity.RealUid is null || identity.SavedUid is null)
            return "Не удалось проверить владельца процесса.";
        if (ownUid == 0 || identity.EffectiveUid == 0 || identity.RealUid == 0 || identity.SavedUid == 0)
            return "Остановка процессов root отключена. Запускай PortLens от обычного пользователя.";
        if (identity.EffectiveUid != ownUid || identity.RealUid != ownUid || identity.SavedUid != ownUid)
            return "Можно остановить только обычный процесс своего пользователя.";
        if (identity.Key.Length == 0 || identity.Path.Length == 0)
            return "Не удалось надёжно проверить путь или время запуска процесса.";
        return "";
    }

    internal sealed record Identity(string Key, uint? EffectiveUid, uint? RealUid,
        uint? SavedUid, string Path, bool Exited);

    private static Identity ReadIdentity(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var status = File.ReadAllText($"/proc/{pid}/status");
                var path = TryRead(() => new FileInfo($"/proc/{pid}/exe").LinkTarget) ?? "";
                return ParseLinuxIdentity(stat, status, path);
            }

            if (Native.ProcPidInfo(pid, 3, 0, out var info, Marshal.SizeOf<MacBsdInfo>())
                != Marshal.SizeOf<MacBsdInfo>() || info.Pid != pid)
                return new("", null, null, null, "", false);
            var buffer = new byte[4096];
            var size = Native.ProcPidPath(pid, buffer, (uint)buffer.Length);
            var zero = Array.IndexOf(buffer, (byte)0);
            var pathMac = size > 0 ? Encoding.UTF8.GetString(buffer, 0, zero >= 0 ? zero : size) : "";
            return new($"{info.StartSeconds}:{info.StartMicroseconds}", info.Uid, info.RealUid,
                info.SavedUid, pathMac, info.Status == 5 || (info.Flags & 4) != 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { return new("", null, null, null, "", false); }
    }

    internal static Identity ParseLinuxIdentity(string stat, string status, string path)
    {
        // Имя в скобках может содержать пробелы и скобки, поэтому обычный Split здесь не подходит.
        var closing = stat.LastIndexOf(')');
        var fields = closing >= 0 ? stat[(closing + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [];
        var uidLine = status.Split('\n').FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal));
        var uids = uidLine?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        uint? real = null, effective = null, saved = null;
        if (uids is { Length: >= 4 }
            && uint.TryParse(uids[1], out var r) && uint.TryParse(uids[2], out var e) && uint.TryParse(uids[3], out var s))
        { real = r; effective = e; saved = s; }
        var marker = fields.Length > 19 && ulong.TryParse(fields[19], out _) ? fields[19] : "";
        return new(marker, effective, real, saved, path, fields.Length > 0 && fields[0] is "Z" or "X" or "x");
    }

    private static string ReadLinuxCommand(int pid)
    {
        var command = TryRead(() => File.ReadAllText($"/proc/{pid}/cmdline"));
        if (string.IsNullOrEmpty(command)) return Unavailable;
        return string.Join(" ", command.TrimEnd('\0').Split('\0').Select(argument =>
            argument.Any(char.IsWhiteSpace) || argument.Contains('"')
                ? "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : argument));
    }

    private static async Task<string> ReadMacCommandAsync(int pid, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new ProcessStartInfo("/bin/ps")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in new[] { "-ww", "-p", pid.ToString(CultureInfo.InvariantCulture), "-o", "command=" })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            var text = (await output).Trim();
            return process.ExitCode == 0 && text.Length > 0 ? text : Unavailable;
        }
        catch (OperationCanceledException)
        {
            // Завершаем только собственный короткоживущий помощник ps.
            if (!process.HasExited) Native.Signal(process.Id, SigTerm);
            token.ThrowIfCancellationRequested();
            return Unavailable;
        }
        catch (Win32Exception) { return Unavailable; }
    }

    private static int OpenPidFd(int pid)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return -1;
        // Номера системных вызовов Linux одинаковы для поддерживаемых x64 и ARM64.
        return (int)Native.PidFdOpen(434, pid, 0);
    }

    private static bool HasProcessExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
        catch (Win32Exception) { return false; }
    }

    private static T? TryReadValue<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static string? TryRead(Func<string?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { return null; }
    }

    private static void EnsureSupported()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Карточки процессов поддерживаются на Linux и macOS.");
    }

    // Расположение полей соответствует struct proc_bsdinfo из macOS SDK (sys/proc_info.h).
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct MacBsdInfo
    {
        [FieldOffset(0)] public uint Flags;
        [FieldOffset(4)] public uint Status;
        [FieldOffset(12)] public uint Pid;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public uint RealUid;
        [FieldOffset(36)] public uint SavedUid;
        [FieldOffset(120)] public ulong StartSeconds;
        [FieldOffset(128)] public ulong StartMicroseconds;
    }

    private static class Native
    {
        [DllImport("libc", EntryPoint = "geteuid")] internal static extern uint GetEffectiveUid();
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)] internal static extern int Signal(int pid, int signal);
        [DllImport("libc", EntryPoint = "close")] internal static extern int Close(int fd);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        internal static extern long PidFdOpen(long number, int pid, uint flags);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        internal static extern long PidFdSendSignal(long number, int fd, int signal, IntPtr info, uint flags);
        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo", SetLastError = true)]
        internal static extern int ProcPidInfo(int pid, int flavor, ulong arg, out MacBsdInfo buffer, int size);
        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidpath", SetLastError = true)]
        internal static extern int ProcPidPath(int pid, [Out] byte[] buffer, uint size);
    }
}
