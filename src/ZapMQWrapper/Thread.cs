using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace ZapMQWrapper
{
    public class ZapMQThread
    {
        private Thread mainThread { get; set; }
        private ZapMQ Core { get; set; }
        private bool Terminated { get; set; }
        public ZapMQThread(ZapMQ pCore)
        {
            Core = pCore;
        }
        public void Start()
        {
            mainThread = new Thread(Execute);
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
                    foreach (var Queue in Core.Queues)
                    {
                        ZapJSONMessage JSONMessage = Core.GetMessage(Queue.Name);
                        if (JSONMessage != null)
                        {
                            ProcessingMessage = true;
                            string RPCAnswer = Queue.Handler(JSONMessage, out ProcessingMessage);
                            if ((RPCAnswer != null) && (JSONMessage.RPC))
                            {
                                Core.SendRPCResponse(Queue.Name, JSONMessage.Id, RPCAnswer);
                            }
                        }
                    }
                }
                Thread.Sleep(100);
            }
        }

    }

    public delegate void EventRPCExpired(ZapJSONMessage pMessage);

    public class ZapMQRPCThread
    {
        private Thread mainThread { get; set; }
        private ZapMQ Core { get; set; }
        private ZapMQHandlerRPC Handler { get; set; }
        private ZapJSONMessage Message { get; set; }
        private string QueueName { get; set; }
        private int TTL { get; set; }
        private int BirthTime { get; set; }
        private bool Terminated { get; set; }
        private EventRPCExpired eventRPCExpired { get; set; }
        private bool IsExpired()
        {
            return (TTL > 0) && ((BirthTime + TTL) < Environment.TickCount);    
        }
        public ZapMQRPCThread(string pHost, int pPort, ZapMQHandlerRPC pHandler, ZapJSONMessage pMessage, string pQueueName, EventRPCExpired pEventRPCExpired, int pTTL = 0)
        {
            Core = new ZapMQ(pHost, pPort);
            Handler = pHandler;
            Message = pMessage;
            QueueName = pQueueName;
            eventRPCExpired = pEventRPCExpired;
            TTL = pTTL;
            BirthTime = Environment.TickCount;
        }
        public void Start()
        {
            mainThread = new Thread(Execute);
            mainThread.Start();
        }
        public void Stop()
        {
            Terminated = true;
        }
        private void Execute()
        {
            ZapJSONMessage response = null;
            while((response == null) && (!IsExpired()) && (!Terminated))
            {
                response = Core.GetRPCResponse(QueueName, Message.Id);
                if (response != null)
                {
                    Handler(response);
                }
                Thread.Sleep(100);
            }
            if ((response == null) && (IsExpired()))
            {
                eventRPCExpired?.Invoke(Message);
            }
        }
    }
}
