using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace ZapMQ
{
    public class ZapMQWrapper
    {
        private ZapMQ Core { get; set; }
        private ZapMQThread Thread { get; set; }
        private List<ZapMQRPCThread> ThreadsRPC {get; set;}
        public EventRPCExpired OnRPCExpired { get; set; }
        // Tempo de espera aplicado as mensagens RPC enviadas sem TTL. Sem ele a
        // thread de espera ficaria viva indefinidamente quando a resposta nunca
        // chega. Use TTL negativo no SendRPCMessage para esperar para sempre.
        public int DefaultRPCTimeout { get; set; }
        private ZapMQMessageClaims Claims { get; set; }
        // Ligado por padrao: com mais de um consumidor na mesma fila, o servidor
        // pode entregar a mesma mensagem a dois deles (ver ZapMQMessageClaims).
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
        public ZapMQWrapper(string pHost, int pPort)
        {
            Core = new ZapMQ(pHost, pPort);
            Claims = new ZapMQMessageClaims();
            Thread = new ZapMQThread(Core, Claims);
            Thread.Start();
            ThreadsRPC = new List<ZapMQRPCThread>();
            DefaultRPCTimeout = 60000;
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
            }
            else
            {
                throw new Exception("You cannot bind an unnamed Queue");
            }
        }
        public void UnBind(string pQueueName)
        {
            ZapMQQueue Queue = Core.FindQueue(pQueueName);
            if (Queue != null)
            {
                lock (Core.Queues)
                {
                    Core.Queues.Remove(Queue);
                }
            }
        }
        public bool IsBinded(string pQueueName)
        {
            ZapMQQueue Queue = Core.FindQueue(pQueueName);
            return Queue != null;
        }
        public bool SendMessage(string pQueueName, object pMessage, int pTTL = 0)
        {
            if (pQueueName == string.Empty)
            {
                throw new Exception("Inform the Queue name");
            }
            if (!IsBinded(pQueueName))
            {
                ZapJSONMessage JSONMessage = new ZapJSONMessage();
                JSONMessage.Body = pMessage;
                JSONMessage.RPC = false;
                JSONMessage.TTL = pTTL;
                try
                {
                    Core.SendMessage(pQueueName, JSONMessage);
                    return true;
                }
                catch
                {
                    return false;
                }
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
                ZapJSONMessage JSONMessage = new ZapJSONMessage();
                JSONMessage.Body = pMessage;
                JSONMessage.RPC = true;
                JSONMessage.TTL = pTTL;
                try
                {
                    JSONMessage.Id = Core.SendMessage(pQueueName, JSONMessage);
                    // Core.SendMessage devolve null quando a chamada falha, e null
                    // passaria por uma comparacao com string.Empty, colocando uma
                    // thread para aguardar a resposta de uma mensagem inexistente.
                    if (!string.IsNullOrEmpty(JSONMessage.Id))
                    {
                        ZapMQRPCThread responseThread = new ZapMQRPCThread(Core.Host, Core.Port, pHandler, JSONMessage, pQueueName, OnRPCExpired, ResolveRPCTimeout(pTTL), RPCThreadFinished);
                        // Registra antes do Start para nao correr o risco de a
                        // thread terminar e pedir a remocao antes de existir na lista.
                        lock (ThreadsRPC)
                        {
                            ThreadsRPC.Add(responseThread);
                        }
                        responseThread.Start();
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
            else
            {
                throw new Exception("You cannot send message to a Queue self binded");
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
        public int PendingRPCCount()
        {
            lock (ThreadsRPC)
            {
                return ThreadsRPC.Count;
            }
        }
        public void StopThreads()
        {
            Thread.Stop();
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
        }
    }
}
