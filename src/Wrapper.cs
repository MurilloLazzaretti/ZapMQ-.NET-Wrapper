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
        public ZapMQWrapper(string pHost, int pPort)
        {
            Core = new ZapMQ(pHost, pPort);
            Thread = new ZapMQThread(Core);
            Thread.Start();
            ThreadsRPC = new List<ZapMQRPCThread>();
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
                    if (JSONMessage.Id != string.Empty)
                    {
                        ZapMQRPCThread responseThread = new ZapMQRPCThread(Core.Host, Core.Port, pHandler, JSONMessage, pQueueName, OnRPCExpired, pTTL);
                        ThreadsRPC.Add(responseThread);
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
        public void StopThreads()
        {
            Thread.Stop();
            foreach (var thread in ThreadsRPC)
            {
                thread.Stop();
            }
        }
    }
}
