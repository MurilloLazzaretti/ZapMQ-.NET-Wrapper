using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace ZapMQ
{
    public class ZapMQThread
    {
        private Thread mainThread { get; set; }
        private ZapMQ Core { get; set; }
        private ZapMQMessageClaims Claims { get; set; }
        private bool Terminated { get; set; }
        public ZapMQThread(ZapMQ pCore) : this(pCore, new ZapMQMessageClaims())
        {
        }
        public ZapMQThread(ZapMQ pCore, ZapMQMessageClaims pClaims)
        {
            Core = pCore;
            Claims = pClaims;
        }
        public void Start()
        {
            mainThread = new Thread(Execute);
            mainThread.IsBackground = true;
            mainThread.Start();
        }
        public void Stop()
        {
            Terminated = true;
        }
        private void Execute()
        {
            bool ProcessingMessage = false;
            while (!Terminated)
            {
                if (!ProcessingMessage)
                {
                    lock (Core.Queues) { 
                        Core.Queues.ForEach(Queue =>
                        {
                            ZapJSONMessage JSONMessage = Core.GetMessage(Queue.Name);
                            if (JSONMessage != null)
                            {
                                // Outro consumidor recebeu a mesma mensagem e ja
                                // a assumiu: nao processa e nao responde o RPC,
                                // que fica a cargo de quem de fato processou.
                                if (!Claims.TryClaim(Queue.Name, JSONMessage))
                                {
                                    return;
                                }
                                ProcessingMessage = true;
                                var RPCAnswer = Queue.Handler(JSONMessage, out ProcessingMessage);
                                if ((RPCAnswer != null) && (JSONMessage.RPC))
                                {
                                    Core.SendRPCResponse(Queue.Name, JSONMessage.Id, RPCAnswer);
                                }
                            }
                        });
                    }
                }
                Thread.Sleep(300);
            }
        }
    }

    public delegate void EventRPCExpired(ZapJSONMessage pMessage);

    public delegate void EventRPCFinished(ZapMQRPCThread pThread);

    public class ZapMQRPCThread
    {
        private Thread mainThread { get; set; }
        private ZapMQ Core { get; set; }
        private ZapMQHandlerRPC Handler { get; set; }
        private ZapJSONMessage Message { get; set; }
        private string QueueName { get; set; }
        private int TTL { get; set; }
        private Stopwatch Lifetime { get; set; }
        private bool Terminated { get; set; }
        private EventRPCExpired eventRPCExpired { get; set; }
        private EventRPCFinished eventRPCFinished { get; set; }
        private bool IsExpired()
        {
            // TTL negativo mantem a espera indefinida. O Stopwatch substitui o
            // Environment.TickCount, que estoura o int e fica negativo a cada
            // ~24,9 dias de uptime, fazendo a expiracao nunca disparar.
            return (TTL > 0) && (Lifetime.ElapsedMilliseconds > TTL);
        }
        public ZapMQRPCThread(string pHost, int pPort, ZapMQHandlerRPC pHandler, ZapJSONMessage pMessage, string pQueueName, EventRPCExpired pEventRPCExpired, int pTTL = 0, EventRPCFinished pEventRPCFinished = null)
        {
            Core = new ZapMQ(pHost, pPort);
            Handler = pHandler;
            Message = pMessage;
            QueueName = pQueueName;
            eventRPCExpired = pEventRPCExpired;
            eventRPCFinished = pEventRPCFinished;
            TTL = pTTL;
            Lifetime = Stopwatch.StartNew();
        }
        public void Start()
        {
            mainThread = new Thread(Execute);
            mainThread.IsBackground = true;
            mainThread.Start();
        }
        public void Stop()
        {
            Terminated = true;
        }
        private void Execute()
        {
            try
            {
                ZapJSONMessage response = null;
                while ((response == null) && (!IsExpired()) && (!Terminated))
                {
                    response = Core.GetRPCResponse(QueueName, Message.Id);
                    if (response != null)
                    {
                        Handler(response);
                        break;
                    }
                    Thread.Sleep(300);
                }
                if ((response == null) && (IsExpired()))
                {
                    eventRPCExpired?.Invoke(Message);
                }
            }
            finally
            {
                // Avisa o fim em qualquer saida (resposta, expiracao, Stop ou
                // excecao no handler) para o Wrapper soltar a referencia.
                eventRPCFinished?.Invoke(this);
            }
        }
    }
}
