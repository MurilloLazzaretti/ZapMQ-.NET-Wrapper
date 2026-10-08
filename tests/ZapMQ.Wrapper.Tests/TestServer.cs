using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ZapMQ.Server;

namespace ZapMQ.Wrapper.Tests;

/// <summary>
/// A real ZapMQ server on a local port. It can be stopped and started again on the same port,
/// with or without the v2 protocol, which is how a server being replaced looks to the wrapper.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    private WebApplication? _app;

    public TestServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Http = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}/") };
    }

    public int Port { get; }

    public HttpClient Http { get; }

    public static async Task<TestServer> StartNewAsync(bool v2 = true)
    {
        var server = new TestServer();
        await server.StartAsync(v2);
        return server;
    }

    public async Task StartAsync(bool v2 = true)
    {
        _app = ServerHost.Build([], builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ZapMQ:Port"] = Port.ToString(),
            ["ZapMQ:V2:Enabled"] = v2 ? "true" : "false",
            // Only the messaging port matters here, and many servers run side by side.
            ["ZapMQ:Panel:Enabled"] = "false",
            ["ZapMQ:QueueDefinitionsFile"] = Path.Combine(Path.GetTempPath(), "zapmq-wrapper-tests-queues.json")
        }));
        await _app.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    public async Task<JObject> MetricsAsync() => JObject.Parse(await Http.GetStringAsync("metrics"));

    public async Task<JArray> ConnectionsAsync() => (JArray)(await MetricsAsync())["connections"]!;

    public async Task<JObject?> QueueAsync(string name) =>
        (JObject?)((JArray)(await MetricsAsync())["queues"]!).FirstOrDefault(queue => (string?)queue["name"] == name);

    /// <summary>
    /// Read straight from the broker: the administration routes are on the panel port, behind
    /// a login, and these tests are not about the panel.
    /// </summary>
    public Task<JArray> DeadLettersAsync(string queue)
    {
        var broker = _app!.Services.GetRequiredService<global::ZapMQ.Core.Broker>();
        return Task.FromResult(new JArray(broker.GetDeadLetters(queue).Select(letter => new JObject
        {
            ["id"] = letter.Id,
            ["reason"] = letter.Reason switch
            {
                global::ZapMQ.Core.DeadLetterReason.Expired => "expired",
                global::ZapMQ.Core.DeadLetterReason.NotConsumed => "not-consumed",
                _ => "unconfirmed"
            },
            ["consumer"] = letter.Consumer
        })));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Http.Dispose();
    }
}

internal static class Eventually
{
    /// <summary>
    /// Waits until the condition holds; false if it never did.
    /// </summary>
    public static async Task<bool> TrueAsync(Func<Task<bool>> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(50);
        }
        return await condition();
    }

    public static Task<bool> True(Func<bool> condition, int timeoutMs = 10000) =>
        TrueAsync(() => Task.FromResult(condition()), timeoutMs);
}
