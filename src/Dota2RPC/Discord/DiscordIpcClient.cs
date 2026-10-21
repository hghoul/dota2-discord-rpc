using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Dota2RPC.Core;

namespace Dota2RPC.Discord;

/// <summary>Discord недоступен: клиент не запущен или IPC-канал не отвечает.</summary>
internal sealed class DiscordUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Discord отклонил подключение или команду (например, неверный Application ID).</summary>
internal sealed class DiscordRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    public string FriendlyMessage => Code switch
    {
        4000 => "Discord отклонил Application ID. Проверьте Client ID в Discord Developer Portal.",
        4002 => "Discord временно ограничил частоту запросов. Повтор через минуту.",
        4004 => "Неподдерживаемая версия RPC-протокола Discord.",
        _ => $"Discord вернул ошибку {Code}: {Message}",
    };
}

internal readonly record struct RpcResult(bool Ok, string Message, bool ConnectionLost)
{
    public static RpcResult Success(string message = "OK") => new(true, message, false);
    public static RpcResult Failed(string message, bool connectionLost) => new(false, message, connectionLost);
}

/// <summary>
/// Клиент локального IPC-протокола Discord (именованный канал \\.\pipe\discord-ipc-N).
/// Формат кадра: int32 opcode (LE), int32 длина (LE), JSON в UTF-8.
/// Это тот же механизм, что использует официальная библиотека discord-rpc и Discord Game SDK.
/// </summary>
internal sealed class DiscordIpcClient : IAsyncDisposable
{
    enum Op
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }

    const int MaxFrame = 1024 * 1024;

    /// <summary>Префикс имени канала; в тестах заменяется, чтобы не трогать настоящий Discord.</summary>
    /// Переменная DOTA2RPC_DISCORD_PIPE_PREFIX позволяет подключить программу к тестовому серверу (скриншоты, отладка).
    internal static string PipePrefix { get; set; } =
        Environment.GetEnvironmentVariable("DOTA2RPC_DISCORD_PIPE_PREFIX") is { Length: > 0 } prefix ? prefix : "discord-ipc-";

    readonly NamedPipeClientStream _pipe;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    readonly CancellationTokenSource _cts = new();
    Task? _reader;
    volatile bool _connected;

    DiscordIpcClient(NamedPipeClientStream pipe, string pipeName)
    {
        _pipe = pipe;
        PipeName = pipeName;
    }

    public string PipeName { get; }
    public string? UserName { get; private set; }
    public string? CloseReason { get; private set; }
    public bool IsConnected => _connected && _pipe.IsConnected;

    /// <summary>Discord закрыл соединение (выход из Discord, перезапуск и т.п.).</summary>
    public event Action? Closed;

    public static async Task<DiscordIpcClient> ConnectAsync(string clientId, CancellationToken ct)
    {
        var candidates = FindPipes();
        if (candidates.Count == 0)
            throw new DiscordUnavailableException("Discord не запущен (IPC-канал не найден). Запустите приложение Discord для ПК.");

        Exception? last = null;
        foreach (var name in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                }

                var client = new DiscordIpcClient(pipe, name);
                try
                {
                    await client.HandshakeAsync(clientId, ct).ConfigureAwait(false);
                    return client;
                }
                catch
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (DiscordRpcException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }

        throw new DiscordUnavailableException("Discord не отвечает на подключение. Перезапустите Discord.", last);
    }

    static List<string> FindPipes()
    {
        var all = Enumerable.Range(0, 10).Select(i => $"{PipePrefix}{i}").ToList();
        try
        {
            var existing = new HashSet<string>(
                Directory.EnumerateFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>(),
                StringComparer.OrdinalIgnoreCase);
            return all.Where(existing.Contains).ToList();
        }
        catch
        {
            return all; // перечисление каналов недоступно — пробуем все по очереди
        }
    }

    async Task HandshakeAsync(string clientId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        await WriteFrameAsync(Op.Handshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, timeout.Token).ConfigureAwait(false);

        var (op, payload) = await ReadFrameAsync(timeout.Token).ConfigureAwait(false);
        if (op == Op.Close)
        {
            var code = (int?)payload?["code"] ?? 0;
            var message = (string?)payload?["message"] ?? "соединение закрыто";
            throw new DiscordRpcException(code, message);
        }

        var evt = (string?)payload?["evt"];
        if (op == Op.Frame && evt == "READY")
        {
            var user = payload?["data"]?["user"];
            UserName = (string?)user?["global_name"] is { Length: > 0 } g ? g : (string?)user?["username"];
            _connected = true;
            _reader = Task.Run(() => ReadLoopAsync(_cts.Token));
            Log.Info($"Discord: подключено через {PipeName}, пользователь: {UserName ?? "?"}");
            return;
        }

        if (evt == "ERROR")
            throw new DiscordRpcException((int?)payload?["data"]?["code"] ?? 0, (string?)payload?["data"]?["message"] ?? "ошибка");

        throw new IOException($"Неожиданный ответ Discord при рукопожатии: op={op}, evt={evt}");
    }

    async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (op, payload) = await ReadFrameAsync(ct).ConfigureAwait(false);
                switch (op)
                {
                    case Op.Ping:
                        await WriteFrameAsync(Op.Pong, payload, ct).ConfigureAwait(false);
                        break;
                    case Op.Close:
                        CloseReason = (string?)payload?["message"] ?? "Discord закрыл соединение";
                        Log.Warn($"Discord: соединение закрыто: {CloseReason}");
                        return;
                    case Op.Frame:
                        if ((string?)payload?["nonce"] is { } nonce && _pending.TryRemove(nonce, out var tcs))
                            tcs.TrySetResult(payload);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CloseReason ??= ex.Message;
            if (!ct.IsCancellationRequested) Log.Warn($"Discord: чтение прервано: {ex.Message}");
        }
        finally
        {
            _connected = false;
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(new IOException("Соединение с Discord потеряно"));
            if (!ct.IsCancellationRequested)
            {
                try { Closed?.Invoke(); } catch { /* обработчик не должен ронять чтение */ }
            }
        }
    }

    /// <summary>Устанавливает (или очищает при activity == null) статус. Возвращает подтверждённый Discord результат.</summary>
    public async Task<RpcResult> SetActivityAsync(JsonObject? activity, CancellationToken ct)
    {
        if (!IsConnected) return RpcResult.Failed("Нет соединения с Discord", connectionLost: true);

        var nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            var payload = new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JsonObject
                {
                    ["pid"] = Environment.ProcessId,
                    ["activity"] = activity,
                },
                ["nonce"] = nonce,
            };

            using (var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await WriteFrameAsync(Op.Frame, payload, writeTimeout.Token).ConfigureAwait(false);
            }

            var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            if ((string?)response?["evt"] == "ERROR")
            {
                var message = (string?)response?["data"]?["message"] ?? "неизвестная ошибка";
                return RpcResult.Failed($"Discord отклонил статус: {message}", connectionLost: false);
            }
            return RpcResult.Success();
        }
        catch (TimeoutException)
        {
            _connected = false;
            return RpcResult.Failed("Discord не ответил вовремя", connectionLost: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _connected = false;
            return RpcResult.Failed("Запись в канал Discord зависла", connectionLost: true);
        }
        catch (IOException ex)
        {
            _connected = false;
            return RpcResult.Failed("Соединение с Discord потеряно: " + ex.Message, connectionLost: true);
        }
        catch (ObjectDisposedException)
        {
            _connected = false;
            return RpcResult.Failed("Соединение с Discord закрыто", connectionLost: true);
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    async Task WriteFrameAsync(Op op, JsonNode? payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload?.ToJsonString() ?? "{}");
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), (int)op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(frame, ct).ConfigureAwait(false);
            await _pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task<(Op Op, JsonNode? Payload)> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await _pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var op = (Op)BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > MaxFrame) throw new IOException($"Некорректная длина кадра Discord: {length}");

        var body = new byte[length];
        await _pipe.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        JsonNode? payload = null;
        if (length > 0)
        {
            try { payload = JsonNode.Parse(body); }
            catch (Exception ex) { throw new IOException("Discord прислал некорректный JSON: " + ex.Message); }
        }
        return (op, payload);
    }

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        try { _cts.Cancel(); } catch { /* уже освобождён */ }
        try { await _pipe.DisposeAsync().ConfigureAwait(false); } catch { /* игнорируем */ }
        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* игнорируем */ }
        }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
