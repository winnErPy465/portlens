using System.Net;
using PortLens;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Ошибка: " + name);
    checks++;
    Console.WriteLine("OK: " + name);
}

PortEntry Entry(string address, int port = 8080, string protocol = "TCP") => new(protocol, address, port, "test", "123");
Check(HttpProbe.SuggestedUrl(Entry("0.0.0.0")) == "http://127.0.0.1:8080/", "IPv4: любой интерфейс → loopback");
Check(HttpProbe.SuggestedUrl(Entry("*")) == "http://127.0.0.1:8080/", "Звёздочка → loopback");
Check(HttpProbe.SuggestedUrl(Entry("[::]")) == "http://[::1]:8080/", "IPv6: любой интерфейс → loopback");
Check(HttpProbe.SuggestedUrl(Entry("0:0:0:0:0:0:0:0")) == "http://[::1]:8080/", "Полный IPv6 нулевой адрес");
Check(HttpProbe.SuggestedUrl(Entry("2001:db8::1")) == "http://[2001:db8::1]:8080/", "IPv6 в квадратных скобках");
Check(HttpProbe.SuggestedUrl(Entry("[::1]", 443)) == "https://[::1]:443/", "HTTPS-подсказка для 443");
Check(HttpProbe.SuggestedUrl(Entry("127.0.0.1", protocol: "UDP")) == "", "Для UDP нет HTTP-подсказки");
Check(HttpProbe.SuggestedUrl(Entry("127.0.0.1", 0)) == "", "Порт 0 не предлагается для соединения");

using (var productionHandler = HttpProbe.CreateHandler())
{
    Check(!productionHandler.AllowAutoRedirect && !productionHandler.UseProxy && !productionHandler.UseCookies,
        "Нет переходов, прокси и cookies");
    Check(!productionHandler.UseDefaultCredentials && productionHandler.Credentials is null && !productionHandler.PreAuthenticate,
        "Учётные данные не отправляются");
    Check(productionHandler.ServerCertificateCustomValidationCallback is null, "Стандартная проверка TLS-сертификатов");
}

var requests = new List<Uri>();
var methodIsGet = false;
using var replyClient = new HttpClient(new StubHandler(async (request, token) =>
{
    methodIsGet = request.Method == HttpMethod.Get && request.Content is null && request.Headers.Authorization is null;
    requests.Add(request.RequestUri!);
    await Task.Delay(25, token);
    return new(HttpStatusCode.OK) { Content = new UnreadableContent() };
})) { Timeout = Timeout.InfiniteTimeSpan };
var ok = await HttpProbe.CheckAsync("  http://127.0.0.1:8080/health?x=1  ", default, replyClient, TimeSpan.FromSeconds(1));
Check(ok.StatusCode == 200 && ok.Message.Contains("200 OK"), "Успешный статус");
Check(ok.ElapsedMilliseconds >= 15, "Измерено время ответа");
Check(methodIsGet && requests.Single().AbsolutePath == "/health", "GET с путём и без авторизации");
Check(ok.Address == "http://127.0.0.1:8080/health?x=1", "Пробелы по краям адреса убраны");
Check(ok.StatusCode == 200, "Тело ответа не читается");

foreach (var invalid in new[] { "", "localhost:8080", "file:///etc/passwd", "ftp://127.0.0.1", "http://", "https://127.0.0.1:70000/",
    "http://user:password@127.0.0.1/", "http://@127.0.0.1/", "http://127.0.0.1:0/", "http://127.0.0.1/a b",
    "http://127.0.0.1/%zz", "http://127.0.0.1\\path", "http://127.0.0.1/\r\nHost:x" })
{
    var before = requests.Count;
    var result = await HttpProbe.CheckAsync(invalid, default, replyClient, TimeSpan.FromSeconds(1));
    Check(result.StatusCode is null && result.Message.Contains("Введи") && requests.Count == before,
        "Некорректный URL отклонён до запроса: " + invalid.Replace("\r", "CR").Replace("\n", "LF"));
}
var ipv6 = await HttpProbe.CheckAsync("http://[::1]:8080/", default, replyClient, TimeSpan.FromSeconds(1));
Check(ipv6.StatusCode == 200 && requests.Last().Host.Contains("::1"), "HTTP-запрос к IPv6-адресу");

using var errorClient = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
    { Timeout = Timeout.InfiniteTimeSpan };
var error = await HttpProbe.CheckAsync("http://127.0.0.1/", default, errorClient, TimeSpan.FromSeconds(1));
Check(error.StatusCode == 503 && error.Message.Contains("503"), "HTTP-ошибка показана как полученный статус");

var redirectCalls = 0;
using var redirectClient = new HttpClient(new StubHandler((_, _) =>
{
    redirectCalls++;
    var response = new HttpResponseMessage(HttpStatusCode.Found);
    response.Headers.Location = new Uri("http://127.0.0.1:9999/elsewhere");
    return Task.FromResult(response);
})) { Timeout = Timeout.InfiniteTimeSpan };
var redirect = await HttpProbe.CheckAsync("http://127.0.0.1/", default, redirectClient, TimeSpan.FromSeconds(1));
Check(redirect.StatusCode == 302 && redirect.Message.Contains("не выполнялся") && redirectCalls == 1,
    "302 показан без повторного запроса");

using var slowClient = new HttpClient(new StubHandler(async (_, token) =>
{
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
    return new(HttpStatusCode.OK);
})) { Timeout = Timeout.InfiniteTimeSpan };
var timedOut = await HttpProbe.CheckAsync("http://127.0.0.1/", default, slowClient, TimeSpan.FromMilliseconds(40));
Check(timedOut.StatusCode is null && timedOut.Message.Contains("Время ожидания") && timedOut.ElapsedMilliseconds >= 20,
    "Тайм-аут возвращает понятный результат");
using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
var cancelled = false;
try { await HttpProbe.CheckAsync("http://127.0.0.1/", cancellation.Token, slowClient, TimeSpan.FromSeconds(1)); }
catch (OperationCanceledException) { cancelled = true; }
Check(cancelled, "Отмена пользователем передаётся вызывающему коду");

using var refusedClient = new HttpClient(new StubHandler((_, _) =>
    throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"))) { Timeout = Timeout.InfiniteTimeSpan };
var refused = await HttpProbe.CheckAsync("http://127.0.0.1/", default, refusedClient, TimeSpan.FromSeconds(1));
Check(refused.StatusCode is null && refused.Message.Contains("Не удалось подключиться"), "Отказ соединения объяснён по-русски");

Console.WriteLine($"Пройдено проверок: {checks}. Сетевые запросы не выполнялись.");

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}

sealed class UnreadableContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new Exception("Тело ответа не должно читаться.");
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}
