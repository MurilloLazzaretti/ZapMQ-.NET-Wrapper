using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace ZapMQWrapper
{
    public class ZapMQWrapper
    {
        private ZapMQ Core { get; set; }
        private ZapMQThread Thread { get; set; }
        public EventRPCExpired OnRPCExpired { get; set; }
        public ZapMQWrapper(string pHost, int pPort)
        {
            Core = new ZapMQ(pHost, pPort);
            Thread = new ZapMQThread(Core);
            Thread.Start();
        }
        public void Bind(string pQueueName, ZapMQHandler pHandler)
        {
            if (pQueueName != string.Empty)
            {
                ZapMQQueue Queue = new ZapMQQueue();
                Queue.Name = pQueueName;
                Queue.Handler = pHandler;
                Core.Queues.Add(Queue);
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
                Core.Queues.Remove(Queue);
            }
        }
        public bool IsBinded(string pQueueName)
        {
            ZapMQQueue Queue = Core.FindQueue(pQueueName);
            return Queue != null;
        }
        public bool SendMessage(string pQueueName, string pMessage, int pTTL)
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
        public bool SendRPCMessage(string pQueueName, string pMessage, ZapMQHandlerRPC pHandler, int pTTL = 0)
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
                    JSONMessage.Id = Core.SendMessage(pQueueName, JSONMessage);
                    if (JSONMessage.Id != string.Empty)
                    {
                        ZapMQRPCThread responseThread = new ZapMQRPCThread(Core.Host, Core.Port, pHandler, JSONMessage, pQueueName, OnRPCExpired, pTTL);
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
    }
}
