using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZapMQ
{
    // One connection to a 2.x server over the v2 protocol: a WebSocket carrying one JSON object
    // per text frame. It pairs answers with requests and hands over what the server sends on
    // its own. It never reconnects; a lost connection is replaced by a new instance.
    //
    // Nothing here waits on the thread pool. The callers of the wrapper block until the server
    // answers, and an application with many of them at once would leave no pool thread free to
    // wake them up. The connection is read by a thread of its own, which releases the waiting
    // callers directly.
    internal class ZapMQSocket
    {
        public const string WrapperVersion = "dotnet/2.0.0";

        private class PendingRequest
        {
            public readonly ManualResetEventSlim Answered = new ManualResetEventSlim(false);
            public JObject Reply;
            public Action<JObject> OnAccepted;
        }

        private readonly ClientWebSocket Socket = new ClientWebSocket();
        private readonly ZapMQSettings Settings;
        private readonly Action<ZapMQSocket, JObject> OnPush;
        private readonly object SendLock = new object();
        private readonly ConcurrentDictionary<long, PendingRequest> Waiting = new ConcurrentDictionary<long, PendingRequest>();
        private readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        private readonly ManualResetEventSlim ClosedEvent = new ManualResetEventSlim(false);
        private readonly Stopwatch Clock = Stopwatch.StartNew();
        private long NextId;
        private long LastReceivedMs;
        private int Finished;

        private ZapMQSocket(ZapMQSettings settings, Action<ZapMQSocket, JObject> onPush)
        {
            Settings = settings;
            OnPush = onPush;
        }

        public bool IsOpen
        {
            get { return Volatile.Read(ref Finished) == 0; }
        }

        public string ServerVersion { get; private set; }

        public long SilenceMs
        {
            get { return Clock.ElapsedMilliseconds - Interlocked.Read(ref LastReceivedMs); }
        }

        // Opens the connection and greets the server. Null when the server is not there or
        // does not speak the v2 protocol. endpointFound tells the two apart: it is true when
        // the server accepted the WebSocket, even if the greeting did not complete.
        public static ZapMQSocket Open(string host, int port, ZapMQSettings settings, Action<ZapMQSocket, JObject> onPush, out bool endpointFound)
        {
            endpointFound = false;
            ZapMQSocket socket = new ZapMQSocket(settings, onPush);
            try
            {
                using (CancellationTokenSource timeout = new CancellationTokenSource(settings.ConnectTimeoutMs))
                {
                    socket.Socket.ConnectAsync(new Uri("ws://" + host + ":" + port + "/v2"), timeout.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                }
            }
            catch (Exception)
            {
                socket.Socket.Dispose();
                return null;
            }

            endpointFound = true;
            socket.Touch();
            Thread reader = new Thread(socket.Receive);
            reader.IsBackground = true;
            reader.Start();

            JObject hello = new JObject
            {
                ["op"] = "hello",
                ["protocol"] = 2,
                ["client"] = DescribeClient()
            };
            JObject reply = socket.Request(hello, settings.ConnectTimeoutMs, null);
            if (!Accepted(reply))
            {
                socket.Abort();
                return null;
            }
            socket.ServerVersion = (string)reply["server"];
            return socket;
        }

        public static bool Accepted(JObject reply)
        {
            return (reply != null) && (reply["ok"] != null) && (reply["ok"].Type == JTokenType.Boolean) && (bool)reply["ok"];
        }

        public static string ErrorCode(JObject reply)
        {
            if (reply == null)
            {
                return "no-answer";
            }
            JObject error = reply["error"] as JObject;
            return (error == null) ? "" : (string)error["code"];
        }

        // Sends a request and blocks until its answer. Null when the connection ended or the
        // answer did not come in time; in the second case the connection is dropped, because
        // its state is no longer known.
        //
        // onAccepted runs when the server accepts the request, before any later frame is read.
        // Whatever has to be in place when the next frame arrives is done there.
        public JObject Request(JObject frame, Action<JObject> onAccepted = null)
        {
            return Request(frame, Settings.RequestTimeoutMs, onAccepted);
        }

        private JObject Request(JObject frame, int timeoutMs, Action<JObject> onAccepted)
        {
            long id = Interlocked.Increment(ref NextId);
            frame["id"] = id;
            PendingRequest request = new PendingRequest { OnAccepted = onAccepted };
            Waiting[id] = request;
            // Checked after registering: a connection that ended in between has already
            // released everyone it knew about.
            if (!IsOpen)
            {
                Waiting.TryRemove(id, out request);
                return null;
            }
            Post(frame);

            if (!request.Answered.Wait(timeoutMs))
            {
                PendingRequest abandoned;
                if (Waiting.TryRemove(id, out abandoned))
                {
                    Abort();
                    return null;
                }
                // The answer arrived at the same instant and is being handed over.
                request.Answered.Wait();
            }
            return request.Reply;
        }

        // Sends without waiting for an answer.
        public void Post(JObject frame)
        {
            ArraySegment<byte> bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(frame.ToString(Formatting.None)));
            try
            {
                // A WebSocket takes one send at a time.
                lock (SendLock)
                {
                    Socket.SendAsync(bytes, WebSocketMessageType.Text, true, Lifetime.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                }
            }
            catch (Exception)
            {
                Abort();
            }
        }

        // Blocks until the connection is over or the time runs out. True when it is over.
        public bool WaitClosed(int timeoutMs)
        {
            return ClosedEvent.Wait(timeoutMs);
        }

        // Ends the connection in order.
        public void Close(int timeoutMs)
        {
            if (!IsOpen)
            {
                return;
            }
            try
            {
                using (CancellationTokenSource timeout = new CancellationTokenSource(timeoutMs))
                {
                    lock (SendLock)
                    {
                        Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                }
                // The server closes its side in return, which ends the reading.
                ClosedEvent.Wait(timeoutMs);
            }
            catch (Exception)
            {
                //
            }
            Abort();
        }

        // Cuts the connection at once.
        public void Abort()
        {
            try
            {
                Lifetime.Cancel();
                Socket.Abort();
            }
            catch (Exception)
            {
                //
            }
            Finish();
        }

        private static JObject DescribeClient()
        {
            string name = "unknown";
            int pid = 0;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    name = process.ProcessName;
                    pid = process.Id;
                }
            }
            catch (Exception)
            {
                //
            }
            return new JObject
            {
                ["name"] = name,
                ["pid"] = pid,
                ["host"] = Environment.MachineName,
                ["wrapper"] = WrapperVersion
            };
        }

        private void Touch()
        {
            Interlocked.Exchange(ref LastReceivedMs, Clock.ElapsedMilliseconds);
        }

        private void Receive()
        {
            ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[16 * 1024]);
            try
            {
                using (MemoryStream frame = new MemoryStream())
                {
                    while (Socket.State == WebSocketState.Open)
                    {
                        frame.SetLength(0);
                        WebSocketReceiveResult received;
                        do
                        {
                            received = Socket.ReceiveAsync(buffer, Lifetime.Token).ConfigureAwait(false).GetAwaiter().GetResult();
                            if (received.MessageType == WebSocketMessageType.Close)
                            {
                                return;
                            }
                            frame.Write(buffer.Array, 0, received.Count);
                        }
                        while (!received.EndOfMessage);

                        Touch();
                        Dispatch(Parse(frame));
                    }
                }
            }
            catch (Exception)
            {
                // The connection broke; whoever watches it takes it from here.
            }
            finally
            {
                Abort();
            }
        }

        // Text that looks like a date stays text, as it does for a message read from 1.x.
        private static JObject Parse(MemoryStream frame)
        {
            string text = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
            using (JsonTextReader reader = new JsonTextReader(new StringReader(text)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                return JObject.Load(reader);
            }
        }

        private void Dispatch(JObject frame)
        {
            if (frame["op"] != null)
            {
                OnPush(this, frame);
                return;
            }

            JToken re = frame["re"];
            PendingRequest request;
            if ((re != null) && (re.Type == JTokenType.Integer) && Waiting.TryRemove((long)re, out request))
            {
                if ((request.OnAccepted != null) && Accepted(frame))
                {
                    request.OnAccepted(frame);
                }
                request.Reply = frame;
                request.Answered.Set();
            }
        }

        private void Finish()
        {
            if (Interlocked.Exchange(ref Finished, 1) == 1)
            {
                return;
            }
            foreach (long id in Waiting.Keys)
            {
                PendingRequest request;
                if (Waiting.TryRemove(id, out request))
                {
                    request.Answered.Set();
                }
            }
            try
            {
                Socket.Dispose();
            }
            catch (Exception)
            {
                //
            }
            ClosedEvent.Set();
        }
    }
}
