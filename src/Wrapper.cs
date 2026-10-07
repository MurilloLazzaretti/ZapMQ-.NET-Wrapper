using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZapMQ
{
    // The wrapper speaks two protocols and chooses by itself:
    //
    //   v2  A permanent connection to a 2.x server. Messages are pushed as they arrive and
    //       confirmed when the handler returns.
    //   v1  The original polling over HTTP.
    //
    // It works over v1 from the first instant, exactly as 1.x did, and moves to v2 as soon as
    // a connection is ready. Whenever there is no v2 connection it is back on v1. An instance
    // is therefore never idle waiting for a handshake, and server and wrapper can be updated
    // in any order.
    public class ZapMQWrapper
    {
        private const int ProtocolNone = 0;
        private const int ProtocolV1 = 1;
        private const int ProtocolV2 = 2;

        private static readonly HttpClient ProbeClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private class PendingRPC
        {
            public string Id;
            public string QueueName;
            public ZapMQHandlerRPC Handler;
            public ZapJSONMessage Message;
            public EventRPCExpired Expired;
            public int TimeoutMs;
            public Stopwatch Lifetime;

            // A timeout that is not positive waits forever.
            public bool IsExpired
            {
                get { return (TimeoutMs > 0) && (Lifetime.ElapsedMilliseconds > TimeoutMs); }
            }

            public int RemainingMs
            {
                get { return (TimeoutMs > 0) ? (int)Math.Max(1, TimeoutMs - Lifetime.ElapsedMilliseconds) : TimeoutMs; }
            }
        }

        private class Delivery
        {
            public ZapMQSocket Socket;
            public ZapMQQueue Queue;
            public ZapJSONMessage Message;
        }

        private ZapMQ Core { get; set; }
        private List<ZapMQRPCThread> ThreadsRPC { get; set; }
        public EventRPCExpired OnRPCExpired { get; set; }
        // Tempo de espera aplicado as mensagens RPC enviadas sem TTL. Sem ele a
        // espera ficaria viva indefinidamente quando a resposta nunca chega. Use
        // TTL negativo no SendRPCMessage para esperar para sempre.
        public int DefaultRPCTimeout { get; set; }
        private ZapMQMessageClaims Claims { get; set; }
        // So tem efeito com servidor 1.x, que pode entregar a mesma mensagem a
        // dois consumidores da mesma fila (ver ZapMQMessageClaims). Com servidor
        // 2.x a entrega unica e garantida pelo servidor.
        public bool DeduplicateMessages
        {
            get { return Claims.Enabled; }
            set { Claims.Enabled = value; }
        }
        // Mensagem recebida em duplicidade e descartada por esta instancia.
        public EventDuplicateDiscarded OnDuplicateDiscarded
        {
            get { return Claims.OnDuplicateDiscarded; }
            set { Claims.OnDuplicateDiscarded = value; }
        }
        // O controle de duplicidade falhou e a mensagem foi processada sem ele.
        public EventClaimFailure OnClaimFailure
        {
            get { return Claims.OnClaimFailure; }
            set { Claims.OnClaimFailure = value; }
        }

        private readonly ZapMQSettings Settings;
        private readonly object TransportLock = new object();
        private readonly CancellationTokenSource Stopping = new CancellationTokenSource();
        private readonly ManualResetEventSlim ConsumptionEnded = new ManualResetEventSlim(false);
        private readonly BlockingCollection<Delivery> Deliveries = new BlockingCollection<Delivery>();
        private readonly ConcurrentDictionary<string, PendingRPC> PendingRPCs = new ConcurrentDictionary<string, PendingRPC>();
        private readonly Thread ConsumptionThread;
        private readonly Timer ExpiryTimer;
        private volatile ZapMQSocket Socket;
        private volatile int ProtocolInUse = ProtocolNone;
        // A handler left pProcessing on: this instance takes no more messages.
        private volatile bool Halted;
        private int Expiring;
        private int Stopped;

        public ZapMQWrapper(string pHost, int pPort) : this(pHost, pPort, new ZapMQSettings())
        {
        }

        internal ZapMQWrapper(string pHost, int pPort, ZapMQSettings pSettings)
        {
            Settings = pSettings;
            Core = new ZapMQ(pHost, pPort);
            Claims = new ZapMQMessageClaims();
            ThreadsRPC = new List<ZapMQRPCThread>();
            DefaultRPCTimeout = 60000;

            ConsumptionThread = new Thread(Consume);
            ConsumptionThread.IsBackground = true;
            ConsumptionThread.Start();
            ExpiryTimer = new Timer(ExpireRPCs, null, Settings.PollIntervalMs, Settings.PollIntervalMs);
            Thread supervisor = new Thread(Supervise);
            supervisor.IsBackground = true;
            supervisor.Start();
        }

        // 2 with a v2 connection, 1 with a server known to speak v1 only, 0 while neither is
        // known. Sending and consuming go over v1 in the last two cases.
        internal int Protocol
        {
            get { return ProtocolInUse; }
        }

        // Cuts the connection as a network failure would.
        internal void DropConnection()
        {
            ZapMQSocket socket = Socket;
            if (socket != null)
            {
                socket.Abort();
            }
        }

        public void Bind(string pQueueName, ZapMQHandler pHandler)
        {
            if (pQueueName != string.Empty)
            {
                ZapMQQueue Queue = new ZapMQQueue();
                Queue.Name = pQueueName;
                Queue.Handler = pHandler;
                lock (Core.Queues)
                {
                    Core.Queues.Add(Queue);
                }
                // A connection opened after this point binds the queue by itself, because
                // it reads the list only after becoming the current one.
                ZapMQSocket socket = Socket;
                if ((socket != null) && !Halted)
                {
                    socket.Post(QueueFrame("bind", pQueueName));
                }
            }
            else
            {
                throw new Exception("You cannot bind an unnamed Queue");
            }
        }
        public void UnBind(string pQueueName)
        {
            ZapMQQueue Queue;
            bool last;
            lock (Core.Queues)
            {
                Queue = Core.FindQueue(pQueueName);
                last = Core.Queues.Count(x => x.Name == pQueueName) == 1;
            }
            if (Queue != null)
            {
                // The server is told first and the handler leaves afterwards: a message
                // pushed before the server knew of the unbind still finds its handler.
                ZapMQSocket socket = Socket;
                if (last && (socket != null) && !Halted)
                {
                    socket.Request(QueueFrame("unbind", pQueueName));
                }
                lock (Core.Queues)
                {
                    Core.Queues.Remove(Queue);
                }
            }
        }
        public bool IsBinded(string pQueueName)
        {
            lock (Core.Queues)
            {
                return Core.FindQueue(pQueueName) != null;
            }
        }
        public bool SendMessage(string pQueueName, object pMessage, int pTTL = 0)
        {
            if (pQueueName == string.Empty)
            {
                throw new Exception("Inform the Queue name");
            }
            if (!IsBinded(pQueueName))
            {
                return Send(pQueueName, pMessage, false, pTTL, null);
            }
            else
            {
                throw new Exception("You cannot send message to a Queue self binded");
            }
        }
        public bool SendRPCMessage(string pQueueName, object pMessage, ZapMQHandlerRPC pHandler, int pTTL = 0)
        {
            if (pQueueName == string.Empty)
            {
                throw new Exception("Inform the Queue name");
            }
            if (!IsBinded(pQueueName))
            {
                return Send(pQueueName, pMessage, true, pTTL, pHandler);
            }
            else
            {
                throw new Exception("You cannot send message to a Queue self binded");
            }
        }
        public int PendingRPCCount()
        {
            lock (ThreadsRPC)
            {
                return ThreadsRPC.Count + PendingRPCs.Count;
            }
        }
        public void StopThreads()
        {
            if (Interlocked.Exchange(ref Stopped, 1) == 1)
            {
                return;
            }
            Stopping.Cancel();
            ExpiryTimer.Dispose();
            PendingRPCs.Clear();
            // Percorre uma copia: cada thread se remove da lista ao terminar, e
            // isso invalidaria o enumerador no meio do foreach.
            ZapMQRPCThread[] threads;
            lock (ThreadsRPC)
            {
                threads = ThreadsRPC.ToArray();
            }
            foreach (var thread in threads)
            {
                thread.Stop();
            }
            // The consumption thread closes the connection once the message it is
            // processing has been confirmed. Called from a handler, there is nothing to
            // wait for: that is the thread itself.
            if (Thread.CurrentThread != ConsumptionThread)
            {
                ConsumptionEnded.Wait(Settings.StopWaitMs);
            }
        }

        private int ResolveRPCTimeout(int pTTL)
        {
            return (pTTL == 0) ? DefaultRPCTimeout : pTTL;
        }

        private void RPCThreadFinished(ZapMQRPCThread pThread)
        {
            lock (ThreadsRPC)
            {
                ThreadsRPC.Remove(pThread);
            }
        }

        private static JObject QueueFrame(string op, string queueName)
        {
            return new JObject { ["op"] = op, ["queue"] = queueName };
        }

        private static void Log(string text)
        {
            Trace.WriteLine("ZapMQ: " + text);
        }

        // ------------------------------------------------------------------
        // Sending
        // ------------------------------------------------------------------

        private bool Send(string queueName, object body, bool rpc, int ttl, ZapMQHandlerRPC handler)
        {
            ZapMQSocket socket = Socket;
            if ((socket != null) && (Volatile.Read(ref Stopped) == 0))
            {
                return SendV2(socket, queueName, body, rpc, ttl, handler);
            }
            // No connection (yet, anymore, or after StopThreads): the way it always went.
            return SendV1(queueName, body, rpc, ttl, handler);
        }

        private bool SendV2(ZapMQSocket socket, string queueName, object body, bool rpc, int ttl, ZapMQHandlerRPC handler)
        {
            ZapJSONMessage message = new ZapJSONMessage();
            message.Body = body;
            message.RPC = rpc;
            message.TTL = ttl;

            JObject frame = QueueFrame("publish", queueName);
            try
            {
                frame["body"] = ToToken(body);
            }
            catch (Exception)
            {
                return false;
            }
            frame["rpc"] = rpc;
            frame["ttlMs"] = Math.Max(ttl, 0);

            Action<JObject> onAccepted = null;
            if (rpc)
            {
                EventRPCExpired expired = OnRPCExpired;
                int timeout = ResolveRPCTimeout(ttl);
                // Registered before the next frame is read: the answer may be right behind
                // the confirmation of the publish.
                onAccepted = reply =>
                {
                    message.Id = (string)reply["messageId"];
                    PendingRPCs[message.Id] = new PendingRPC
                    {
                        Id = message.Id,
                        QueueName = queueName,
                        Handler = handler,
                        Message = message,
                        Expired = expired,
                        TimeoutMs = timeout,
                        Lifetime = Stopwatch.StartNew()
                    };
                };
            }
            return ZapMQSocket.Accepted(socket.Request(frame, onAccepted));
        }

        private bool SendV1(string queueName, object body, bool rpc, int ttl, ZapMQHandlerRPC handler)
        {
            ZapJSONMessage message = new ZapJSONMessage();
            message.Body = body;
            message.RPC = rpc;
            message.TTL = ttl;
            // Core.SendMessage devolve null quando a chamada falha.
            message.Id = Core.SendMessage(queueName, message);
            if (string.IsNullOrEmpty(message.Id))
            {
                return false;
            }
            if (rpc)
            {
                StartRPCThread(queueName, message, handler, OnRPCExpired, ResolveRPCTimeout(ttl));
            }
            return true;
        }

        private void StartRPCThread(string queueName, ZapJSONMessage message, ZapMQHandlerRPC handler, EventRPCExpired expired, int timeout)
        {
            ZapMQRPCThread responseThread = new ZapMQRPCThread(Core.Host, Core.Port, handler, message, queueName, expired, timeout, RPCThreadFinished);
            // Registra antes do Start para nao correr o risco de a thread terminar
            // e pedir a remocao antes de existir na lista.
            lock (ThreadsRPC)
            {
                ThreadsRPC.Add(responseThread);
            }
            responseThread.Start();
        }

        private static JToken ToToken(object value)
        {
            return (value == null) ? JValue.CreateNull() : JToken.FromObject(value);
        }

        // Through the same parser a 1.x message goes through, so the handler gets Body and
        // Response in the types it always did.
        private static ZapJSONMessage ToMessage(JToken message)
        {
            return ZapJSONMessage.FromJSON(message.ToString(Formatting.None));
        }

        // ------------------------------------------------------------------
        // Choosing the protocol and keeping the connection
        // ------------------------------------------------------------------

        // Runs on a thread of its own for as long as the wrapper lives.
        private void Supervise()
        {
            int jitter = (Settings.V2StartJitterMs > 0) ? new Random(Guid.NewGuid().GetHashCode()).Next(Settings.V2StartJitterMs) : 0;
            Pause(Settings.V2StartDelayMs + jitter);

            int wait = Settings.ReconnectMinMs;
            while (!Stopping.IsCancellationRequested)
            {
                bool endpointFound;
                ZapMQSocket socket = ZapMQSocket.Open(Core.Host, Core.Port, Settings, OnPush, out endpointFound);
                if (socket != null)
                {
                    Stopwatch lifetime = Stopwatch.StartNew();
                    RunV2(socket);
                    if (lifetime.ElapsedMilliseconds >= Settings.ReconnectMaxMs)
                    {
                        // It was a working connection: the first new attempt is immediate.
                        wait = Settings.ReconnectMinMs;
                        continue;
                    }
                }
                else if (!endpointFound && SpeaksV1())
                {
                    // A server without v2. One that has it but was slow to greet is tried
                    // again shortly, below.
                    EnterV1();
                    wait = Settings.ReconnectMinMs;
                    Pause(Settings.V2ProbeIntervalMs);
                    continue;
                }

                Pause(wait);
                wait = Math.Min(wait * 2, Settings.ReconnectMaxMs);
            }
        }

        private void Pause(int milliseconds)
        {
            Stopping.Token.WaitHandle.WaitOne(milliseconds);
        }

        // A harmless 1.x call: the answer of a message that does not exist. Any HTTP answer
        // means there is a server that takes the old protocol.
        private bool SpeaksV1()
        {
            string url = "http://" + Core.Host + ":" + Core.Port + "/datasnap/rest/TZapMethods/GetRPCResponse/zapmq.probe/0";
            try
            {
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Stopping.Token))
                {
                    timeout.CancelAfter(Settings.ConnectTimeoutMs);
                    using (ProbeClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false).GetAwaiter().GetResult())
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void EnterV1()
        {
            lock (TransportLock)
            {
                if (ProtocolInUse != ProtocolV1)
                {
                    Log("server at " + Core.Host + ":" + Core.Port + " speaks the 1.x protocol only");
                }
                ProtocolInUse = ProtocolV1;
            }
            // Answers still awaited over v2 are collected the old way from here on.
            foreach (PendingRPC rpc in PendingRPCs.Values)
            {
                PendingRPC moved;
                if (PendingRPCs.TryRemove(rpc.Id, out moved))
                {
                    StartRPCThread(moved.QueueName, moved.Message, moved.Handler, moved.Expired, moved.RemainingMs);
                }
            }
        }

        private void RunV2(ZapMQSocket socket)
        {
            lock (TransportLock)
            {
                Socket = socket;
                ProtocolInUse = ProtocolV2;
            }
            try
            {
                if (Stopping.IsCancellationRequested || !Synchronize(socket))
                {
                    socket.Abort();
                    return;
                }
                Log("connected to server " + socket.ServerVersion + " at " + Core.Host + ":" + Core.Port + " (v2)");

                while (!socket.WaitClosed(Settings.PingIntervalMs))
                {
                    if (socket.SilenceMs > Settings.PingTimeoutMs)
                    {
                        Log("no sign of the server for " + socket.SilenceMs + " ms");
                        socket.Abort();
                        break;
                    }
                    socket.Post(new JObject { ["op"] = "ping", ["id"] = 0 });
                }
            }
            finally
            {
                lock (TransportLock)
                {
                    Socket = null;
                    ProtocolInUse = ProtocolNone;
                }
                if (!Stopping.IsCancellationRequested)
                {
                    Log("connection to " + Core.Host + ":" + Core.Port + " lost");
                }
            }
        }

        // Tells a new connection what this instance consumes and which answers it waits for.
        private bool Synchronize(ZapMQSocket socket)
        {
            if (!Halted)
            {
                foreach (string queueName in BoundQueueNames())
                {
                    if (!ZapMQSocket.Accepted(socket.Request(QueueFrame("bind", queueName))))
                    {
                        return false;
                    }
                }
            }
            foreach (IGrouping<string, PendingRPC> queue in PendingRPCs.Values.GroupBy(rpc => rpc.QueueName))
            {
                JObject frame = QueueFrame("await", queue.Key);
                frame["messageIds"] = new JArray(queue.Select(rpc => rpc.Id));
                if (socket.Request(frame) == null)
                {
                    return false;
                }
            }
            return true;
        }

        private string[] BoundQueueNames()
        {
            lock (Core.Queues)
            {
                return Core.Queues.Select(queue => queue.Name).Distinct().ToArray();
            }
        }

        // What the server sends on its own. Runs on the thread that reads the connection, so
        // it only hands work over.
        private void OnPush(ZapMQSocket socket, JObject frame)
        {
            switch ((string)frame["op"])
            {
                case "deliver":
                {
                    string queueName = (string)frame["queue"];
                    ZapMQQueue queue;
                    lock (Core.Queues)
                    {
                        queue = Core.FindQueue(queueName);
                    }
                    if ((queue == null) || Halted || ConsumptionEnded.IsSet)
                    {
                        // Nobody here can process it. Dropping the connection leaves the
                        // message with the server as unconfirmed instead of losing it.
                        Log("message of queue " + queueName + " arrived with no handler for it; the connection is being reopened");
                        socket.Abort();
                        return;
                    }
                    Deliveries.Add(new Delivery { Socket = socket, Queue = queue, Message = ToMessage(frame["message"]) });
                    break;
                }
                case "response":
                {
                    PendingRPC rpc;
                    if (PendingRPCs.TryRemove((string)frame["messageId"], out rpc))
                    {
                        ZapJSONMessage response = ToMessage(frame["message"]);
                        ThreadPool.QueueUserWorkItem(state => rpc.Handler(response));
                    }
                    break;
                }
                case "bye":
                    Log("server is closing the connection: " + (string)frame["reason"]);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Consuming
        // ------------------------------------------------------------------

        // The only thread that calls queue handlers, whatever the protocol, so they never run
        // side by side.
        private void Consume()
        {
            try
            {
                while (!Stopping.IsCancellationRequested)
                {
                    Delivery delivery;
                    if (Deliveries.TryTake(out delivery, Settings.PollIntervalMs))
                    {
                        Process(delivery);
                    }
                    else if ((Socket == null) && !Halted)
                    {
                        PollV1();
                    }
                }
                CloseConnection();
            }
            finally
            {
                ConsumptionEnded.Set();
            }
        }

        private void Process(Delivery delivery)
        {
            ZapMQSocket socket = delivery.Socket;
            ZapJSONMessage message = delivery.Message;
            if (!socket.IsOpen)
            {
                // The server already holds it as unconfirmed. Processing it here could make
                // it run twice if it is delivered again.
                Log("message " + message.Id + " of queue " + delivery.Queue.Name + " was not processed: its connection was lost first");
                return;
            }

            bool processing = true;
            object answer = delivery.Queue.Handler(message, out processing);

            if (processing)
            {
                // Before confirming, so the server has nothing else to push here.
                Halt(socket);
            }

            JObject frame = QueueFrame(((answer != null) && message.RPC) ? "respond" : "ack", delivery.Queue.Name);
            frame["messageId"] = message.Id;
            if ((string)frame["op"] == "respond")
            {
                frame["response"] = ToToken(answer);
            }
            JObject reply = socket.Request(frame);
            if (!ZapMQSocket.Accepted(reply))
            {
                Log("message " + message.Id + " of queue " + delivery.Queue.Name + " was processed but its confirmation was refused ("
                    + ZapMQSocket.ErrorCode(reply) + "); the server holds it as unconfirmed");
            }
        }

        private void PollV1()
        {
            ZapMQQueue[] queues;
            lock (Core.Queues)
            {
                queues = Core.Queues.ToArray();
            }
            foreach (ZapMQQueue queue in queues)
            {
                // A v2 connection came up in the meantime: from here on the server pushes.
                if (Stopping.IsCancellationRequested || (Socket != null) || Halted)
                {
                    return;
                }
                lock (Core.Queues)
                {
                    if (!Core.Queues.Contains(queue))
                    {
                        continue;
                    }
                }
                ZapJSONMessage message = Core.GetMessage(queue.Name);
                if (message == null)
                {
                    continue;
                }
                // Outro consumidor recebeu a mesma mensagem e ja a assumiu: nao
                // processa e nao responde o RPC, que fica a cargo de quem de fato
                // processou.
                if (!Claims.TryClaim(queue.Name, message))
                {
                    continue;
                }
                bool processing = true;
                object answer = queue.Handler(message, out processing);
                if ((answer != null) && message.RPC)
                {
                    Core.SendRPCResponse(queue.Name, message.Id, answer);
                }
                if (processing)
                {
                    Halt(null);
                }
            }
        }

        // The handler left pProcessing on. As always, this instance receives nothing else.
        private void Halt(ZapMQSocket socket)
        {
            Halted = true;
            Log("a handler left pProcessing on; this instance stopped consuming");
            if (socket != null)
            {
                UnbindAll(socket);
            }
        }

        private void UnbindAll(ZapMQSocket socket)
        {
            foreach (string queueName in BoundQueueNames())
            {
                socket.Request(QueueFrame("unbind", queueName));
            }
        }

        // Leaves the server in order: stops receiving, finishes what had already arrived and
        // only then closes, so nothing is left behind as unconfirmed.
        private void CloseConnection()
        {
            ZapMQSocket socket = Socket;
            if ((socket == null) || !socket.IsOpen)
            {
                return;
            }
            if (!Halted)
            {
                UnbindAll(socket);
            }
            Delivery delivery;
            while (Deliveries.TryTake(out delivery))
            {
                Process(delivery);
            }
            socket.Close(Settings.StopWaitMs);
        }

        // ------------------------------------------------------------------
        // RPC answers that never came
        // ------------------------------------------------------------------

        private void ExpireRPCs(object state)
        {
            if (Interlocked.Exchange(ref Expiring, 1) == 1)
            {
                return;
            }
            try
            {
                foreach (PendingRPC rpc in PendingRPCs.Values)
                {
                    PendingRPC expired;
                    if (rpc.IsExpired && PendingRPCs.TryRemove(rpc.Id, out expired) && (expired.Expired != null))
                    {
                        expired.Expired(expired.Message);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref Expiring, 0);
            }
        }
    }
}
