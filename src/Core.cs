using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace ZapMQ
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
            return Queues.Find(x => x.Name == pQueueName);
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
                    string content = methodsClient.GetRPCMessage(pQueueName, pIdMessage);
                    if (content != string.Empty)
                    {
                        return ZapJSONMessage.FromJSON(content);
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
                    return null;
                }
            }
            finally
            {
                Connection.Dispose();
            }
        }
        public void SendRPCResponse(string pQueueName, string pIdMessage, object pResponse)
        {
            HttpClient Connection = CreateRestConnection();
            try
            {
                ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(Connection);
                try
                {
                    methodsClient.UpdateRPCResponse(pQueueName, pIdMessage, JsonConvert.SerializeObject(pResponse));
                }
                catch
                {
                    //
                }
            }
            finally
            {
                Connection.Dispose();
            }
        }
    }
}
