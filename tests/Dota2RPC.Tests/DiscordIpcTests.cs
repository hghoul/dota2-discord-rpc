using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Dota2RPC.Core;
using Dota2RPC.Discord;
using Xunit;

namespace Dota2RPC.Tests;

/// <summary>Поддельный сервер IPC с тем же протоколом кадров, что у Discord.</summary>
internal sealed class FakeDiscordServer : IAsyncDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly Task _loop;
    public readonly List<JsonNode?> Frames = [];
    public string Prefix { get; }
    public Func<JsonNode?, JsonNode?>? Respond { get; set; }
    public int? CloseOnHandshakeCode { get; set; }

    public FakeDiscordServer(string? prefix = null)
    {
        Prefix = prefix ?? $"dota2rpc-test-{Guid.NewGuid():N}-";
        var first = CreatePipe(); // канал существует уже к моменту возврата из конструктора
        _loop = Task.Run(() => RunAsync(first));
    }

    NamedPipeServerStream CreatePipe() =>
        new(Prefix + "0", PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    async Task RunAsync(NamedPipeServerStream first)
    {
        var next = first;
        while (!_cts.IsCancellationRequested)
        {
            var pipe = next ?? CreatePipe();
            next = null;
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token);
                _ = Task.Run(() => ServeAsync(pipe));
            }
            catch
            {
                await pipe.DisposeAsync();
                return;
            }
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var header = new byte[8];
                await pipe.ReadExactlyAsync(header, _cts.Token);
                var op = BinaryPrimitives.ReadInt32LittleEndian(header);
                var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
                await pipe.ReadExactlyAsync(body, _cts.Token);
                var payload = JsonNode.Parse(body);
                lock (Frames) Frames.Add(payload);

                if (op == 0)
                {
                    if (CloseOnHandshakeCode is { } code)
                    {
                        await Write(pipe, 2, new JsonObject { ["code"] = code, ["message"] = "Invalid Client ID" });
                        return;
                    }
                    await Write(pipe, 1, new JsonObject
                    {
                        ["cmd"] = "DISPATCH",
                        ["evt"] = "READY",
                        ["data"] = new JsonObject { ["v"] = 1, ["user"] = new JsonObject { ["username"] = "tester", ["global_name"] = "Tester" } },
                    });
                }
                else if (op == 1)
                {
                    var reply = Respond?.Invoke(payload) ?? new JsonObject
                    {
                        ["cmd"] = "SET_ACTIVITY",
                        ["data"] = payload?["args"]?["activity"]?.DeepClone(),
                        ["evt"] = null,
                        ["nonce"] = (string?)payload?["nonce"],
                    };
                    await Write(pipe, 1, reply);
                }
            }
        }
        catch
        {
            // клиент отключился
        }
    }

    static async Task Write(Stream s, int op, JsonNode node)
    {
        var body = Encoding.UTF8.GetBytes(node.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await s.WriteAsync(frame);
        await s.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
}

[Collection("pipe-prefix")]
public class DiscordIpcTests
{
    const string ClientId = "123456789012345678";

    [Fact]
    public async Task Handshake_set_and_clear_activity()
    {
        await using var fake = new FakeDiscordServer();
        DiscordIpcClient.PipePrefix = fake.Prefix;
        try
        {
            await using var client = await DiscordIpcClient.ConnectAsync(ClientId, CancellationToken.None);
            Assert.Equal("Tester", client.UserName);

            var activity = new PresenceActivity { Details = "Pudge · Ур. 18", State = "KDA 7 / 2 / 11 · 23 мин", StartUnix = 1792540000 };
            var ok = await client.SetActivityAsync(activity.ToJson(), CancellationToken.None);
            Assert.True(ok.Ok, ok.Message);

            var cleared = await client.SetActivityAsync(null, CancellationToken.None);
            Assert.True(cleared.Ok, cleared.Message);

            lock (fake.Frames)
            {
                Assert.Equal(ClientId, (string?)fake.Frames[0]?["client_id"]);
                Assert.Equal(1, (int?)fake.Frames[0]?["v"]);
                var set = fake.Frames[1]!;
                Assert.Equal("SET_ACTIVITY", (string?)set["cmd"]);
                Assert.Equal(Environment.ProcessId, (int?)set["args"]?["pid"]);
                Assert.Equal("Pudge · Ур. 18", (string?)set["args"]?["activity"]?["details"]);
                Assert.Equal(1792540000L, (long?)set["args"]?["activity"]?["timestamps"]?["start"]);
                var clear = fake.Frames[2]!.AsObject();
                Assert.True(clear["args"]!.AsObject().ContainsKey("activity"));
                Assert.Null(clear["args"]?["activity"]);
            }
        }
        finally
        {
            DiscordIpcClient.PipePrefix = "discord-ipc-";
        }
    }

    [Fact]
    public async Task Invalid_client_id_is_reported()
    {
        await using var fake = new FakeDiscordServer { CloseOnHandshakeCode = 4000 };
        DiscordIpcClient.PipePrefix = fake.Prefix;
        try
        {
            var ex = await Assert.ThrowsAsync<DiscordRpcException>(() => DiscordIpcClient.ConnectAsync(ClientId, CancellationToken.None));
            Assert.Equal(4000, ex.Code);
            Assert.Contains("Application ID", ex.FriendlyMessage);
        }
        finally
        {
            DiscordIpcClient.PipePrefix = "discord-ipc-";
        }
    }

    [Fact]
    public async Task Error_response_is_not_treated_as_success()
    {
        await using var fake = new FakeDiscordServer
        {
            Respond = p => new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["evt"] = "ERROR",
                ["data"] = new JsonObject { ["code"] = 4000, ["message"] = "child \"activity\" fails" },
                ["nonce"] = (string?)p?["nonce"],
            },
        };
        DiscordIpcClient.PipePrefix = fake.Prefix;
        try
        {
            await using var client = await DiscordIpcClient.ConnectAsync(ClientId, CancellationToken.None);
            var result = await client.SetActivityAsync(new PresenceActivity { Details = "x" }.ToJson(), CancellationToken.None);
            Assert.False(result.Ok);
            Assert.False(result.ConnectionLost);
            Assert.Contains("activity", result.Message);
        }
        finally
        {
            DiscordIpcClient.PipePrefix = "discord-ipc-";
        }
    }

    [Fact]
    public async Task No_discord_gives_clear_message()
    {
        DiscordIpcClient.PipePrefix = $"dota2rpc-absent-{Guid.NewGuid():N}-";
        try
        {
            var ex = await Assert.ThrowsAsync<DiscordUnavailableException>(() => DiscordIpcClient.ConnectAsync(ClientId, CancellationToken.None));
            Assert.Contains("Discord не запущен", ex.Message);
        }
        finally
        {
            DiscordIpcClient.PipePrefix = "discord-ipc-";
        }
    }

    [Fact]
    public async Task Manager_reconnects_after_server_restart_and_test_reports_success()
    {
        var fake = new FakeDiscordServer();
        DiscordIpcClient.PipePrefix = fake.Prefix;
        using var manager = new DiscordPresenceManager();
        try
        {
            manager.Configure(ClientId);
            manager.Start();
            var result = await manager.TestAsync(AppController.CreateTestActivity());
            Assert.True(result.Ok, result.Message);
            Assert.Equal(DiscordState.Connected, manager.Status.State);
            Assert.Equal("Tester", manager.Status.UserName);

            // «Выход из Discord»: канал исчезает — менеджер должен заметить это сразу, а не через минуты.
            var prefix = fake.Prefix;
            await fake.DisposeAsync();
            var lost = await WaitFor(() => manager.Status.State != DiscordState.Connected ? manager.Status.State : (DiscordState?)null, TimeSpan.FromSeconds(5));
            Assert.NotNull(lost);

            // «Discord снова запущен»: менеджер переподключается сам.
            fake = new FakeDiscordServer(prefix);
            manager.SetActive(true);
            var back = await WaitFor(() => manager.Status.State == DiscordState.Connected ? manager.Status.State : (DiscordState?)null, TimeSpan.FromSeconds(20));
            Assert.Equal(DiscordState.Connected, back);
        }
        finally
        {
            manager.Dispose();
            await fake.DisposeAsync();
            DiscordIpcClient.PipePrefix = "discord-ipc-";
        }
    }

    static async Task<T?> WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : struct
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>
    /// Живая проверка с настоящим Application ID (только если задана переменная DOTA2RPC_LIVE_CLIENT_ID):
    /// Discord должен подтвердить проверочный статус, после чего статус очищается.
    /// </summary>
    [Fact]
    public async Task Live_discord_accepts_test_activity()
    {
        var clientId = Environment.GetEnvironmentVariable("DOTA2RPC_LIVE_CLIENT_ID");
        if (string.IsNullOrEmpty(clientId)) return;

        DiscordIpcClient.PipePrefix = "discord-ipc-";
        await using var client = await DiscordIpcClient.ConnectAsync(clientId, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        await HeroAnimations.LoadAsync(); // с вшитым адресом — проверяем и анимированную иконку
        var activity = AppController.CreateTestActivity();
        var shown = await client.SetActivityAsync(activity.ToJson(), CancellationToken.None);
        Assert.True(shown.Ok, shown.Message);

        // Картинки по внешним URL и время в секундах Discord тоже принял — иначе вернул бы ERROR.
        await Task.Delay(TimeSpan.FromSeconds(8));
        var cleared = await client.SetActivityAsync(null, CancellationToken.None);
        Assert.True(cleared.Ok, cleared.Message);
    }

    /// <summary>
    /// Проверка протокола на настоящем Discord этого компьютера (если запущен):
    /// заведомо несуществующий Application ID должен быть отклонён понятной ошибкой, а не зависанием.
    /// </summary>
    [Fact]
    public async Task Real_discord_rejects_unknown_application_id()
    {
        DiscordIpcClient.PipePrefix = "discord-ipc-";
        if (!Directory.EnumerateFiles(@"\\.\pipe\").Any(p => p.Contains("discord-ipc-", StringComparison.OrdinalIgnoreCase)))
            return; // Discord не запущен — пропускаем

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ex = await Record.ExceptionAsync(() => DiscordIpcClient.ConnectAsync("100000000000000001", cts.Token));
        Assert.NotNull(ex);
        Assert.True(ex is DiscordRpcException, $"{ex.GetType().Name}: {ex.Message} / {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
    }
}
