using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;

namespace ZapMQWrapper
{
    public class ZapMQ
    {
        public List<ZapMQQueue> Queues { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        private HttpClient CreateRestConnection()
        {
            HttpClient Connection = new HttpClient();
            Uri uri = new Uri("http://" + Host + ":" + Port.ToString() + "/datasnap/rest/");
            Connection.BaseAddress = uri;
            return Connection;
        }
        public ZapMQ(string pHost, int pPort)
        {
            Queues = new List<ZapMQQueue>();
            Host = pHost;
            Port = pPort;
        }
        public ZapMQQueue FindQueue(string pQueueName)
        {
            ZapMQQueue Queue = null;
            foreach (var item in Queues)
            {
                if (item.Name == pQueueName)
                {
                    Queue = item;
                    break;
                }
            }
            return Queue;
        }

        public ZapJSONMessage GetMessage(string pQueueName)
        {
            HttpClient Connection = CreateRestConnection();
            try
            {
                ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(Connection);
                try
                {
                    string Content = methodsClient.GetMessage(pQueueName);
                    if (Content != string.Empty)
                    {
                        return ZapJSONMessage.FromJSON(Content);
                    }
                    else
                    {
                        return null;
                    }
                }
                catch
                {
                    return null;
                }
            }
            finally
            {
                Connection.Dispose();
            }
        }
        public ZapJSONMessage GetRPCResponse(string pQueueName, string pIdMessage)
        {
            HttpClient Connection = CreateRestConnection();
            try
            {
                ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(Connection);
                try
                {
                    return ZapJSONMessage.FromJSON(methodsClient.GetRPCMessage(pQueueName, pIdMessage));
                }
                catch
                {
                    throw new Exception("Error getting RPC message from ZapMQ Server");
                }
            }
            finally
            {
                Connection.Dispose();
            }
        }
        public string SendMessage(string pQueueName, ZapJSONMessage pMessage)
        {
            HttpClient Connection = CreateRestConnection();
            try
            {
                ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(Connection);
                try
                {
                    return methodsClient.UpdateMessage(pQueueName, pMessage.ToJSON());
                }
                catch
                {
                    throw new Exception("Error sending message to ZapMQ Server");
                }
            }
            finally
            {
                Connection.Dispose();
            }
        }
        public void SendRPCResponse(string pQueueName, string pIdMessage, string pResponse)
        {
            HttpClient Connection = CreateRestConnection();
            try
            {
                ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(Connection);
                try
                {
                    methodsClient.UpdateRPCResponse(pQueueName, pIdMessage, pResponse);
                }
                catch
                {
                    throw new Exception("Error sending RPC response to ZapMQ Server");
                }
            }
            finally
            {
                Connection.Dispose();
            }
        }
    }
}
