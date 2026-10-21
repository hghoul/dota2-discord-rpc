using System.Net;
using System.Net.Sockets;
using System.Text;
using Dota2RPC.Gsi;
using Xunit;

namespace Dota2RPC.Tests;

public class GsiServerTests
{
    const string Token = "0123456789abcdef0123456789abcdef";

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Accepts_valid_post_and_rejects_wrong_token()
    {
        using var server = new GsiServer(() => Token);
        var received = new List<GsiSnapshot>();
        server.SnapshotReceived += s => { lock (received) received.Add(s); };
        var port = FreePort();
        server.Start(port);
        Assert.True(server.IsListening);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"http://127.0.0.1:{port}/";

        // Несколько запросов подряд по одному keep-alive соединению (как делает Dota)
        for (var i = 0; i < 3; i++)
        {
            var response = await http.PostAsync(url, new StringContent(TestEnv.Fixture("ingame.json"), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var bad = TestEnv.Fixture("ingame.json").Replace(Token, "ffffffffffffffffffffffffffffffff");
        var rejected = await http.PostAsync(url, new StringContent(bad, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        var invalid = await http.PostAsync(url, new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var get = await http.GetStringAsync(url);
        Assert.Contains("GSI endpoint", get);

        lock (received)
        {
            Assert.Equal(3, received.Count);
            Assert.All(received, s => Assert.Equal("npc_dota_hero_pudge", s.HeroName));
        }
    }

    [Fact]
    public async Task Slow_or_broken_clients_do_not_block_server()
    {
        using var server = new GsiServer(() => Token);
        var port = FreePort();
        server.Start(port);

        // Клиент, который открыл соединение и молчит, и клиент с обрезанным телом.
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, port);
        using var broken = new TcpClient();
        await broken.ConnectAsync(IPAddress.Loopback, port);
        var partial = Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nContent-Length: 1000\r\n\r\n{\"a\":");
        await broken.GetStream().WriteAsync(partial);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.PostAsync($"http://127.0.0.1:{port}/", new StringContent(TestEnv.Fixture("ingame.json")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Busy_port_is_reported_with_clear_message()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Server.ExclusiveAddressUse = true;
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            using var server = new GsiServer(() => Token);
            server.Start(port);
            Assert.False(server.IsListening);
            Assert.Contains($"Порт {port} занят", server.LastError);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task Rejects_oversized_body()
    {
        using var server = new GsiServer(() => Token);
        var port = FreePort();
        server.Start(port);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nContent-Length: 999999999\r\n\r\n"));
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await stream.ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read); // сервер закрыл соединение, не пытаясь читать гигабайт
    }
}
