using System.Net;
using System.Net.Sockets;
using System.Text;
using Dota2RPC.Core;

namespace Dota2RPC.Gsi;

/// <summary>
/// Минимальный HTTP-сервер на 127.0.0.1 для приёма POST-запросов Game State Integration.
/// Используется TcpListener, а не HttpListener: последний требует прав администратора (URL ACL) для 127.0.0.1.
/// Каждое соединение обслуживается с таймаутами и ограничением размера, поэтому зависший клиент не блокирует сервер.
/// </summary>
internal sealed class GsiServer : IDisposable
{
    const int MaxHeaderBytes = 16 * 1024;
    const int MaxBodyBytes = 4 * 1024 * 1024;
    static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(60);

    readonly Func<string> _expectedToken;
    readonly object _gate = new();
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    System.Threading.Timer? _retryTimer;
    int _activeConnections;
    bool _warnedNoAuth;

    public GsiServer(Func<string> expectedToken) => _expectedToken = expectedToken;

    public event Action<GsiSnapshot>? SnapshotReceived;

    public int Port { get; private set; }
    public bool IsListening { get; private set; }
    public string? LastError { get; private set; }
    public long RejectedRequests { get; private set; }

    public void Start(int port)
    {
        lock (_gate)
        {
            StopCore();
            Port = port;
            TryStartCore();
        }
    }

    void TryStartCore()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(backlog: 32);
        }
        catch (SocketException ex)
        {
            listener.Server.Dispose();
            IsListening = false;
            LastError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Порт {Port} занят другой программой. Выберите другой порт на вкладке «Интеграция»."
                : $"Не удалось открыть порт {Port}: {ex.Message}";
            Log.Error("GSI: " + LastError);
            // Пробуем снова: порт мог освободиться (например, после перезапуска программы).
            _retryTimer = new System.Threading.Timer(_ =>
            {
                lock (_gate)
                {
                    if (!IsListening && _listener is null) TryStartCore();
                }
            }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
            return;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        IsListening = true;
        LastError = null;
        Log.Info($"GSI: сервер слушает http://127.0.0.1:{Port}/");
        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    void StopCore()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        try { _listener?.Stop(); } catch { /* уже остановлен */ }
        _listener = null;
        IsListening = false;
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Warn($"GSI: ошибка приёма соединения: {ex.Message}");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (Interlocked.Increment(ref _activeConnections) > 64)
            {
                // Защита от лавины соединений: Dota использует одно-два соединения.
                Interlocked.Decrement(ref _activeConnections);
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(client, ct).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _activeConnections); }
            }, CancellationToken.None);
        }
    }

    async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new HttpRequestReader(stream);
                for (var i = 0; !ct.IsCancellationRequested; i++)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(i == 0 ? FirstRequestTimeout : KeepAliveTimeout);

                    var request = await reader.ReadAsync(MaxHeaderBytes, MaxBodyBytes, timeout.Token).ConfigureAwait(false);
                    if (request is null) break;

                    var (status, text) = Process(request);
                    var keepAlive = request.KeepAlive;
                    var body = Encoding.UTF8.GetBytes(text);
                    var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 405 ? "Method Not Allowed" : "Bad Request")}\r\n" +
                               "Content-Type: text/plain; charset=utf-8\r\n" +
                               $"Content-Length: {body.Length}\r\n" +
                               $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                    await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
                    await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
                    if (!keepAlive) break;
                }
            }
            catch (OperationCanceledException) { /* таймаут или остановка */ }
            catch (IOException) { /* клиент закрыл соединение */ }
            catch (InvalidDataException ex)
            {
                Log.Warn($"GSI: некорректный HTTP-запрос: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error("GSI: ошибка обработки соединения", ex);
            }
        }
    }

    (int Status, string Text) Process(HttpRequest request)
    {
        if (request.Method == "GET")
            return (200, $"{AppInfo.ProductName} {AppInfo.Version}: GSI endpoint is running");
        if (request.Method != "POST")
            return (405, "POST only");

        GsiSnapshot snapshot;
        try
        {
            snapshot = GsiParser.Parse(Encoding.UTF8.GetString(request.Body), DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            RejectedRequests++;
            Log.Warn($"GSI: не удалось разобрать JSON: {ex.Message}");
            return (400, "invalid json");
        }

        var expected = _expectedToken();
        if (snapshot.HasAuth)
        {
            if (!string.Equals(snapshot.AuthToken, expected, StringComparison.Ordinal))
            {
                RejectedRequests++;
                if (RejectedRequests is 1 or 100)
                    Log.Warn("GSI: получен запрос с неверным токеном — переустановите конфигурацию GSI на вкладке «Интеграция».");
                return (401, "bad token");
            }
        }
        else if (!_warnedNoAuth)
        {
            _warnedNoAuth = true;
            Log.Warn("GSI: запрос без блока auth — вероятно, используется старый конфиг. Рекомендуется переустановить конфигурацию GSI.");
        }

        try
        {
            SnapshotReceived?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("GSI: ошибка обработчика состояния", ex);
        }
        return (200, "OK");
    }

    public void Dispose() => Stop();
}

internal sealed record HttpRequest(string Method, string Path, bool KeepAlive, byte[] Body);

/// <summary>Читает HTTP/1.1 запросы из потока с поддержкой keep-alive и Content-Length.</summary>
internal sealed class HttpRequestReader(Stream stream)
{
    readonly byte[] _buffer = new byte[64 * 1024];
    int _start;
    int _end;

    public async Task<HttpRequest?> ReadAsync(int maxHeader, int maxBody, CancellationToken ct)
    {
        // 1. Заголовки до \r\n\r\n
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd()) < 0)
        {
            if (_end - _start >= maxHeader) throw new InvalidDataException("слишком длинные заголовки");
            if (!await FillAsync(ct).ConfigureAwait(false))
            {
                if (_end == _start) return null; // соединение закрыто между запросами
                throw new IOException("соединение закрыто посреди заголовков");
            }
        }

        var headerText = Encoding.ASCII.GetString(_buffer, _start, headerEnd - _start);
        _start = headerEnd + 4;

        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new InvalidDataException("неверная строка запроса");

        var method = requestLine[0].ToUpperInvariant();
        var http10 = requestLine[2] == "HTTP/1.0";
        long contentLength = 0;
        bool? keepAliveHeader = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, out contentLength) || contentLength < 0)
                    throw new InvalidDataException("неверный Content-Length");
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                     value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("chunked-передача не поддерживается");
            }
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Contains("close", StringComparison.OrdinalIgnoreCase)) keepAliveHeader = false;
                else if (value.Contains("keep-alive", StringComparison.OrdinalIgnoreCase)) keepAliveHeader = true;
            }
        }

        if (contentLength > maxBody) throw new InvalidDataException($"тело запроса слишком большое ({contentLength} байт)");

        // 2. Тело
        var body = new byte[contentLength];
        var copied = 0;
        while (copied < contentLength)
        {
            if (_end == _start && !await FillAsync(ct).ConfigureAwait(false))
                throw new IOException("соединение закрыто посреди тела запроса");
            var n = (int)Math.Min(contentLength - copied, _end - _start);
            Buffer.BlockCopy(_buffer, _start, body, copied, n);
            _start += n;
            copied += n;
        }

        return new HttpRequest(method, requestLine[1], keepAliveHeader ?? !http10, body);
    }

    int IndexOfHeaderEnd()
    {
        var span = _buffer.AsSpan(_start, _end - _start);
        return span.IndexOf("\r\n\r\n"u8) is var i and >= 0 ? _start + i : -1;
    }

    async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length) throw new InvalidDataException("переполнение буфера");
        var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (read == 0) return false;
        _end += read;
        return true;
    }
}
