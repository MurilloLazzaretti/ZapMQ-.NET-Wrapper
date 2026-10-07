using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace ZapMQ.Wrapper.Tests;

/// <summary>
/// The wrapper against a real server, in both protocols and across the change from one to the
/// other.
/// </summary>
public class WrapperTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static string Queue() => "q" + Guid.NewGuid().ToString("N");

    private static ZapMQSettings Fast() => new()
    {
        ReconnectMaxMs = 500,
        V2ProbeIntervalMs = 500,
        PollIntervalMs = 100
    };

    private static ZapMQWrapper Wrapper(TestServer server, ZapMQSettings? settings = null) =>
        new("localhost", server.Port, settings ?? Fast());

    private static async Task<ZapMQWrapper> WrapperOn(TestServer server, int protocol, ZapMQSettings? settings = null)
    {
        var wrapper = Wrapper(server, settings);
        Assert.True(await Eventually.True(() => wrapper.Protocol == protocol), $"protocol {wrapper.Protocol}, expected {protocol}");
        return wrapper;
    }

    private static ZapMQHandler Collect(ConcurrentQueue<ZapJSONMessage> into, Func<ZapJSONMessage, object?>? answer = null) =>
        (ZapJSONMessage message, out bool processing) =>
        {
            processing = false;
            into.Enqueue(message);
            return answer?.Invoke(message)!;
        };

    private static Task<bool> Consumers(TestServer server, string queue, int count) =>
        Eventually.TrueAsync(async () => (int?)(await server.QueueAsync(queue))?["consumers"] == count);

    // ---------------------------------------------------------------- v2

    [Fact]
    public async Task Over_v2_a_message_reaches_the_handler_with_the_body_it_always_had()
    {
        await using var server = await TestServer.StartNewAsync();
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            consumer.Bind(queue, Collect(received));

            Assert.True(publisher.SendMessage(queue, new { name = "ação", value = 12.5, path = "D:\\a/b", when = "2026-01-02T03:04:05" }, 5000));

            Assert.True(await Eventually.True(() => received.Count == 1));
            Assert.True(received.TryDequeue(out var message));
            var body = Assert.IsType<JObject>(message.Body);
            Assert.Equal("ação", (string?)body["name"]);
            Assert.Equal(12.5, (double)body["value"]!);
            Assert.Equal("D:\\a/b", (string?)body["path"]);
            Assert.StartsWith("{", message.Id);
            Assert.False(message.RPC);

            // The message is confirmed and leaves nothing behind.
            Assert.True(await Eventually.TrueAsync(async () => (long?)(await server.QueueAsync(queue))?["confirmed"] == 1));
            Assert.Empty(await server.DeadLettersAsync(queue));
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Over_v2_the_server_knows_who_is_connected()
    {
        await using var server = await TestServer.StartNewAsync();
        var wrapper = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            wrapper.Bind(queue, Collect(new ConcurrentQueue<ZapJSONMessage>()));
            Assert.True(await Consumers(server, queue, 1));

            var connection = Assert.Single(await server.ConnectionsAsync());
            Assert.Equal("dotnet/2.0.0", (string?)connection["wrapper"]);
            Assert.Equal(Environment.ProcessId, (int)connection["pid"]!);
            Assert.Equal(Environment.MachineName, (string?)connection["host"]);
            Assert.Equal(queue, (string?)Assert.Single((JArray)connection["queues"]!));
        }
        finally
        {
            wrapper.StopThreads();
        }
        Assert.True(await Eventually.TrueAsync(async () => (await server.ConnectionsAsync()).Count == 0));
    }

    [Fact]
    public async Task Over_v2_an_rpc_answer_returns_to_the_sender()
    {
        await using var server = await TestServer.StartNewAsync();
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            var answered = new TaskCompletionSource<ZapJSONMessage>();
            var expired = 0;
            publisher.OnRPCExpired = _ => Interlocked.Increment(ref expired);
            consumer.Bind(queue, Collect(received, message => new { echo = ((JObject)message.Body)["n"], ok = true }));

            Assert.True(publisher.SendRPCMessage(queue, new { n = 7 }, message => answered.TrySetResult(message), 5000));

            var answer = await answered.Task.WaitAsync(Patience);
            var response = Assert.IsType<JObject>(answer.Response);
            Assert.Equal(7, (int)response["echo"]!);
            Assert.True((bool)response["ok"]!);
            Assert.Equal(7, (int)((JObject)answer.Body)["n"]!);
            Assert.True(answer.RPC);
            Assert.True(Assert.Single(received).RPC);
            Assert.Equal(0, publisher.PendingRPCCount());
            Assert.Equal(0, expired);
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Over_v2_an_rpc_nobody_answers_expires()
    {
        await using var server = await TestServer.StartNewAsync();
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            var expired = new TaskCompletionSource<ZapJSONMessage>();
            var answers = 0;
            publisher.OnRPCExpired = message => expired.TrySetResult(message);

            Assert.True(publisher.SendRPCMessage(queue, new { n = 1 }, _ => Interlocked.Increment(ref answers), 400));
            Assert.Equal(1, publisher.PendingRPCCount());

            var message = await expired.Task.WaitAsync(Patience);
            Assert.StartsWith("{", message.Id);
            Assert.True(message.RPC);
            Assert.Equal(0, publisher.PendingRPCCount());
            Assert.Equal(0, answers);
        }
        finally
        {
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Over_v2_handlers_never_run_side_by_side_and_every_message_runs_once()
    {
        await using var server = await TestServer.StartNewAsync();
        var first = await WrapperOn(server, 2);
        var second = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queueA = Queue();
            var queueB = Queue();
            var seen = new ConcurrentDictionary<int, int>();
            var overlaps = 0;

            ZapMQHandler Handler(int[] running) => (ZapJSONMessage message, out bool processing) =>
            {
                processing = true;
                if (Interlocked.Increment(ref running[0]) > 1)
                    Interlocked.Increment(ref overlaps);
                seen.AddOrUpdate((int)((JObject)message.Body)["n"]!, 1, (_, count) => count + 1);
                Thread.Sleep(5);
                Interlocked.Decrement(ref running[0]);
                processing = false;
                return null!;
            };

            // Each instance consumes two queues with one counter: its own handlers must not overlap.
            var runningFirst = new int[1];
            var runningSecond = new int[1];
            first.Bind(queueA, Handler(runningFirst));
            first.Bind(queueB, Handler(runningFirst));
            second.Bind(queueA, Handler(runningSecond));
            second.Bind(queueB, Handler(runningSecond));

            const int total = 200;
            Parallel.For(0, total, n => Assert.True(publisher.SendMessage(n % 2 == 0 ? queueA : queueB, new { n })));

            Assert.True(await Eventually.True(() => seen.Count == total, 20000), $"{seen.Count} of {total}");
            await Task.Delay(300);
            Assert.All(seen.Values, count => Assert.Equal(1, count));
            Assert.Equal(0, overlaps);
            Assert.Empty(await server.DeadLettersAsync(queueA));
            Assert.Empty(await server.DeadLettersAsync(queueB));
        }
        finally
        {
            first.StopThreads();
            second.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Over_v2_a_handler_that_leaves_processing_on_stops_the_consumption()
    {
        await using var server = await TestServer.StartNewAsync();
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            var received = 0;
            consumer.Bind(queue, (ZapJSONMessage _, out bool processing) =>
            {
                processing = true;
                Interlocked.Increment(ref received);
                return null!;
            });

            Assert.True(publisher.SendMessage(queue, new { n = 1 }));
            Assert.True(publisher.SendMessage(queue, new { n = 2 }));

            // The first is processed and confirmed; the second stays in the queue for someone else.
            Assert.True(await Eventually.TrueAsync(async () =>
                (await server.QueueAsync(queue)) is { } state && (long)state["confirmed"]! == 1 && (int)state["consumers"]! == 0));
            await Task.Delay(500);
            Assert.Equal(1, received);
            Assert.Equal(1, (int)(await server.QueueAsync(queue))!["pending"]!);
            Assert.True(consumer.IsBinded(queue));
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Over_v2_unbind_stops_the_deliveries()
    {
        await using var server = await TestServer.StartNewAsync();
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            consumer.Bind(queue, Collect(received));
            Assert.True(consumer.IsBinded(queue));
            Assert.True(publisher.SendMessage(queue, new { n = 1 }));
            Assert.True(await Eventually.True(() => received.Count == 1));

            consumer.UnBind(queue);
            Assert.False(consumer.IsBinded(queue));
            Assert.True(publisher.SendMessage(queue, new { n = 2 }));

            await Task.Delay(500);
            Assert.Single(received);
            Assert.Equal(1, (int)(await server.QueueAsync(queue))!["pending"]!);

            // Unbound, the queue is no longer its own and it may publish to it.
            Assert.True(consumer.SendMessage(queue, new { n = 3 }));
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task The_rules_of_the_calls_are_the_same()
    {
        await using var server = await TestServer.StartNewAsync();
        var wrapper = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            wrapper.Bind(queue, Collect(new ConcurrentQueue<ZapJSONMessage>()));

            Assert.Equal("You cannot bind an unnamed Queue", Assert.Throws<Exception>(() => wrapper.Bind("", null!)).Message);
            Assert.Equal("Inform the Queue name", Assert.Throws<Exception>(() => wrapper.SendMessage("", new { })).Message);
            Assert.Equal("Inform the Queue name", Assert.Throws<Exception>(() => wrapper.SendRPCMessage("", new { }, _ => { })).Message);
            Assert.Equal("You cannot send message to a Queue self binded", Assert.Throws<Exception>(() => wrapper.SendMessage(queue, new { })).Message);
            Assert.Equal("You cannot send message to a Queue self binded", Assert.Throws<Exception>(() => wrapper.SendRPCMessage(queue, new { }, _ => { })).Message);
            Assert.Equal(60000, wrapper.DefaultRPCTimeout);
            Assert.True(wrapper.DeduplicateMessages);
        }
        finally
        {
            wrapper.StopThreads();
        }
    }

    [Fact]
    public async Task Over_v2_a_message_can_be_sent_from_inside_a_handler()
    {
        await using var server = await TestServer.StartNewAsync();
        var relay = await WrapperOn(server, 2);
        var edge = await WrapperOn(server, 2);
        try
        {
            var from = Queue();
            var to = Queue();
            var arrived = new ConcurrentQueue<ZapJSONMessage>();
            edge.Bind(to, Collect(arrived));
            relay.Bind(from, (ZapJSONMessage message, out bool processing) =>
            {
                processing = false;
                Assert.True(relay.SendMessage(to, message.Body));
                return null!;
            });

            Assert.True(edge.SendMessage(from, new { n = 9 }));

            Assert.True(await Eventually.True(() => arrived.Count == 1));
            Assert.Equal(9, (int)((JObject)arrived.Single().Body)["n"]!);
        }
        finally
        {
            relay.StopThreads();
            edge.StopThreads();
        }
    }

    // ---------------------------------------------------------------- connection

    [Fact]
    public async Task Without_a_server_sending_says_so_at_once()
    {
        var server = new TestServer();
        var wrapper = Wrapper(server);
        try
        {
            var started = DateTime.UtcNow;
            Assert.False(wrapper.SendMessage(Queue(), new { n = 1 }));
            Assert.False(wrapper.SendRPCMessage(Queue(), new { n = 1 }, _ => { }));
            Assert.InRange((DateTime.UtcNow - started).TotalMilliseconds, 0, 2000);
            Assert.Equal(0, wrapper.Protocol);
            Assert.Equal(0, wrapper.PendingRPCCount());
        }
        finally
        {
            wrapper.StopThreads();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_new_instance_consumes_and_sends_before_its_v2_connection_is_ready()
    {
        await using var server = await TestServer.StartNewAsync();
        var publisher = await WrapperOn(server, 2);
        // No time at all for the handshake: this instance never gets to know the server.
        var slow = Fast();
        slow.ConnectTimeoutMs = 0;
        var consumer = Wrapper(server, slow);
        try
        {
            var queue = Queue();
            var back = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            var returned = new ConcurrentQueue<ZapJSONMessage>();
            publisher.Bind(back, Collect(returned));
            consumer.Bind(queue, Collect(received, _ => new { ok = true }));

            var answered = new TaskCompletionSource<ZapJSONMessage>();
            Assert.True(publisher.SendRPCMessage(queue, new { n = 1 }, message => answered.TrySetResult(message), 5000));
            Assert.True((bool)((JObject)(await answered.Task.WaitAsync(Patience)).Response)["ok"]!);

            Assert.True(consumer.SendMessage(back, new { n = 2 }));
            Assert.True(await Eventually.True(() => returned.Count == 1));
            Assert.Equal(0, consumer.Protocol);
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task A_wrapper_started_before_the_server_connects_when_it_comes_up()
    {
        await using var server = new TestServer();
        var consumer = Wrapper(server);
        var publisher = Wrapper(server);
        try
        {
            var queue = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            consumer.Bind(queue, Collect(received));
            await Task.Delay(700);

            await server.StartAsync();

            Assert.True(await Consumers(server, queue, 1));
            Assert.True(publisher.SendMessage(queue, new { n = 1 }));
            Assert.True(await Eventually.True(() => received.Count == 1));
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task After_the_server_restarts_the_wrapper_binds_its_queues_again()
    {
        await using var server = await TestServer.StartNewAsync();
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            consumer.Bind(queue, Collect(received));
            Assert.True(await Consumers(server, queue, 1));

            await server.StopAsync();
            Assert.True(await Eventually.True(() => consumer.Protocol == 0 && publisher.Protocol == 0));
            await server.StartAsync();

            Assert.True(await Consumers(server, queue, 1));
            Assert.True(await Eventually.True(() => publisher.Protocol == 2));
            Assert.True(publisher.SendMessage(queue, new { n = 1 }));
            Assert.True(await Eventually.True(() => received.Count == 1));
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task A_connection_lost_during_the_handler_leaves_the_message_as_unconfirmed_and_never_runs_it_again()
    {
        await using var server = await TestServer.StartNewAsync();
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            var runs = new ConcurrentQueue<int>();
            using var inside = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            consumer.Bind(queue, (ZapJSONMessage message, out bool processing) =>
            {
                processing = false;
                var n = (int)((JObject)message.Body)["n"]!;
                runs.Enqueue(n);
                if (n == 1)
                {
                    inside.Set();
                    release.Wait(Patience);
                }
                return null!;
            });

            Assert.True(publisher.SendMessage(queue, new { n = 1 }));
            Assert.True(inside.Wait(Patience));
            consumer.DropConnection();

            // The server gives the message up; the handler here still runs to its end.
            Assert.True(await Eventually.TrueAsync(async () => (await server.DeadLettersAsync(queue)).Count == 1));
            release.Set();
            Assert.Equal("unconfirmed", (string?)(await server.DeadLettersAsync(queue))[0]["reason"]);

            // The wrapper comes back by itself and goes on with the next messages.
            Assert.True(await Consumers(server, queue, 1));
            Assert.True(publisher.SendMessage(queue, new { n = 2 }));
            Assert.True(await Eventually.True(() => runs.Count == 2));
            await Task.Delay(300);
            Assert.Equal([1, 2], runs.ToArray());
            Assert.Single(await server.DeadLettersAsync(queue));
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task An_rpc_answer_given_while_the_sender_was_reconnecting_still_arrives()
    {
        await using var server = await TestServer.StartNewAsync();
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            using var inside = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var answered = new TaskCompletionSource<ZapJSONMessage>();
            consumer.Bind(queue, (ZapJSONMessage _, out bool processing) =>
            {
                processing = false;
                inside.Set();
                release.Wait(Patience);
                return new { done = true };
            });

            Assert.True(publisher.SendRPCMessage(queue, new { n = 1 }, message => answered.TrySetResult(message), 8000));
            Assert.True(inside.Wait(Patience));
            publisher.DropConnection();
            release.Set();

            var answer = await answered.Task.WaitAsync(Patience);
            Assert.True((bool)((JObject)answer.Response)["done"]!);
            Assert.Equal(0, publisher.PendingRPCCount());
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task Stopping_confirms_the_message_in_hand_and_leaves_no_dead_letter()
    {
        await using var server = await TestServer.StartNewAsync();
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            using var inside = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            consumer.Bind(queue, (ZapJSONMessage _, out bool processing) =>
            {
                processing = false;
                inside.Set();
                release.Wait(Patience);
                return null!;
            });

            Assert.True(publisher.SendMessage(queue, new { n = 1 }));
            Assert.True(inside.Wait(Patience));

            var stopping = Task.Run(consumer.StopThreads);
            await Task.Delay(200);
            release.Set();
            await stopping.WaitAsync(Patience);

            Assert.True(await Eventually.TrueAsync(async () => (await server.ConnectionsAsync()).Count == 1));
            var state = await server.QueueAsync(queue);
            Assert.Equal(1, (long)state!["confirmed"]!);
            Assert.Empty(await server.DeadLettersAsync(queue));
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    // ---------------------------------------------------------------- v1 and the change of protocol

    [Fact]
    public async Task With_a_server_that_only_speaks_v1_everything_works_as_before()
    {
        await using var server = await TestServer.StartNewAsync(v2: false);
        var consumer = await WrapperOn(server, 1);
        var publisher = await WrapperOn(server, 1);
        try
        {
            var queue = Queue();
            var rpcQueue = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            var answered = new TaskCompletionSource<ZapJSONMessage>();
            consumer.Bind(queue, Collect(received));
            consumer.Bind(rpcQueue, Collect(received, _ => new { ok = true }));

            Assert.True(publisher.SendMessage(queue, new { name = "ação", n = 1 }));
            Assert.True(publisher.SendRPCMessage(rpcQueue, new { n = 2 }, message => answered.TrySetResult(message), 5000));

            var answer = await answered.Task.WaitAsync(Patience);
            Assert.True((bool)((JObject)answer.Response)["ok"]!);
            Assert.True(await Eventually.True(() => received.Count == 2));
            Assert.Contains(received, message => (string?)((JObject)message.Body)["name"] == "ação");
            Assert.True(await Eventually.True(() => publisher.PendingRPCCount() == 0));
            Assert.Empty(await server.ConnectionsAsync());
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task When_the_server_is_replaced_by_one_that_speaks_v2_the_wrapper_changes_by_itself()
    {
        await using var server = await TestServer.StartNewAsync(v2: false);
        var consumer = await WrapperOn(server, 1);
        var publisher = await WrapperOn(server, 1);
        try
        {
            var queue = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            consumer.Bind(queue, Collect(received));
            Assert.True(publisher.SendMessage(queue, new { n = 1 }));
            Assert.True(await Eventually.True(() => received.Count == 1));

            await server.StopAsync();
            await server.StartAsync(v2: true);

            Assert.True(await Eventually.True(() => consumer.Protocol == 2 && publisher.Protocol == 2));
            Assert.True(await Consumers(server, queue, 1));
            Assert.True(publisher.SendMessage(queue, new { n = 2 }));
            Assert.True(await Eventually.True(() => received.Count == 2));
            Assert.Equal(2, (await server.ConnectionsAsync()).Count);
            Assert.Equal(1, (long)(await server.QueueAsync(queue))!["confirmed"]!);
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task When_the_server_goes_back_to_v1_the_wrapper_follows()
    {
        await using var server = await TestServer.StartNewAsync(v2: true);
        var consumer = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            var queue = Queue();
            var received = new ConcurrentQueue<ZapJSONMessage>();
            consumer.Bind(queue, Collect(received));
            Assert.True(await Consumers(server, queue, 1));

            await server.StopAsync();
            await server.StartAsync(v2: false);

            Assert.True(await Eventually.True(() => consumer.Protocol == 1 && publisher.Protocol == 1));
            Assert.True(publisher.SendMessage(queue, new { n = 1 }));
            Assert.True(await Eventually.True(() => received.Count == 1));
        }
        finally
        {
            consumer.StopThreads();
            publisher.StopThreads();
        }
    }

    [Fact]
    public async Task One_wrapper_on_v1_and_another_on_v2_share_a_queue_and_each_message_runs_once()
    {
        await using var server = await TestServer.StartNewAsync();
        // An interval this long keeps the first one on v1 for the whole test.
        var old = Fast();
        old.V2ProbeIntervalMs = 600000;
        await server.StopAsync();
        await server.StartAsync(v2: false);
        var onV1 = await WrapperOn(server, 1, old);
        await server.StopAsync();
        await server.StartAsync(v2: true);
        var onV2 = await WrapperOn(server, 2);
        var publisher = await WrapperOn(server, 2);
        try
        {
            Assert.Equal(1, onV1.Protocol);
            var queue = Queue();
            var seen = new ConcurrentDictionary<int, int>();
            var byV1 = 0;
            var byV2 = 0;
            ZapMQHandler Handler(Action count) => (ZapJSONMessage message, out bool processing) =>
            {
                processing = false;
                count();
                seen.AddOrUpdate((int)((JObject)message.Body)["n"]!, 1, (_, times) => times + 1);
                Thread.Sleep(20);
                return null!;
            };
            onV1.Bind(queue, Handler(() => Interlocked.Increment(ref byV1)));
            onV2.Bind(queue, Handler(() => Interlocked.Increment(ref byV2)));
            Assert.True(await Consumers(server, queue, 1));

            const int total = 80;
            Parallel.For(0, total, n => Assert.True(publisher.SendMessage(queue, new { n })));

            Assert.True(await Eventually.True(() => seen.Count == total, 20000), $"{seen.Count} of {total}");
            await Task.Delay(400);
            Assert.All(seen.Values, times => Assert.Equal(1, times));
            Assert.Equal(total, byV1 + byV2);
            Assert.True(byV2 > 0);
        }
        finally
        {
            onV1.StopThreads();
            onV2.StopThreads();
            publisher.StopThreads();
        }
    }
}
