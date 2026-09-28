using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PortLens;

public sealed record HttpProbeResult(string Address, int? StatusCode, string Message, long ElapsedMilliseconds);

public static class HttpProbe
{
    private static readonly HttpClient Client = new(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    public static Task<HttpProbeResult> CheckAsync(string address, CancellationToken token) =>
        CheckAsync(address, token, Client, TimeSpan.FromSeconds(5));

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null,
        PreAuthenticate = false
    };

    internal static async Task<HttpProbeResult> CheckAsync(string address, CancellationToken token,
        HttpClient client, TimeSpan timeout)
    {
        token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var text = address?.Trim() ?? "";
        if (!TryAddress(text, out var uri))
            return new(text, null, "Введи полный HTTP/HTTPS-адрес без логина и пароля, например http://127.0.0.1:8080/.",
                watch.ElapsedMilliseconds);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        try
        {
            // Ждём только заголовки: большой ответ или поток не задержит проверку.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;
            var message = $"HTTP {code} {response.ReasonPhrase}".TrimEnd();
            if (code is >= 300 and < 400) message += " — переход по ссылке не выполнялся";
            return new(text, code, message, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(text, null, deadline.IsCancellationRequested
                ? "Время ожидания ответа истекло."
                : "Проверка прервана.", watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException error)
        {
            var message = error.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Не удалось найти указанный адрес.",
                HttpRequestError.SecureConnectionError => "Не удалось установить безопасное HTTPS-соединение. Проверь сертификат и адрес.",
                HttpRequestError.ConnectionError => "Не удалось подключиться. Проверь адрес и запущен ли сервис.",
                HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError => "Сервис вернул некорректный HTTP-ответ.",
                _ => "Не удалось получить HTTP-ответ. Возможно, на этом порту работает другой протокол."
            };
            return new(text, null, message, watch.ElapsedMilliseconds);
        }
    }

    public static string SuggestedUrl(PortEntry entry)
    {
        if (!entry.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) || entry.Port is < 1 or > 65535)
            return "";

        var host = entry.Address.Trim().Trim('[', ']');
        if (host is "*" or "0.0.0.0") host = "127.0.0.1";
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.Equals(IPAddress.IPv6Any)) ip = IPAddress.IPv6Loopback;
            host = ip.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{ip.ToString().Replace("%", "%25")}]" : ip.ToString();
        }
        else if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return "";

        // Номер порта — только подсказка. Пользователь может изменить адрес и протокол.
        return $"{(entry.Port == 443 ? "https" : "http")}://{host}:{entry.Port}/";
    }

    private static bool TryAddress(string text, out Uri? uri)
    {
        uri = null;
        if (text.Any(char.IsWhiteSpace) || text.Contains('\\')
            || !Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || !parsed.IsWellFormedOriginalString()
            || parsed.HostNameType == UriHostNameType.Unknown
            || parsed.Port is < 1 or > 65535
            || parsed.UserInfo.Length > 0) return false;

        // Даже пустой логин (http://@host) не должен попадать в запрос.
        var authorityStart = text.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return false;
        var authority = text[(authorityStart + 3)..].Split('/', '?', '#')[0];
        if (authority.Contains('@')) return false;
        uri = parsed;
        return true;
    }
}
