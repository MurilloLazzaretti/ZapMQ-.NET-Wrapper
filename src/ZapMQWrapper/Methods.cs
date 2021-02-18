using System;
using System.Net.Http; 

namespace ZapMQWrapper
{
    public class ZapMQMethodsClient
    {
        private readonly HttpClient Connection;
        public ZapMQMethodsClient(HttpClient pConnection)
        {
            Connection = pConnection;
        }
        public string GetMessage(string pQueueName)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods.GetMessage/" + pQueueName).Result;                
            return response.Content.ToString();
        }
        public string GetRPCMessage(string pQueueName, string pIdMessage)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods.GetRPCResponse/" + pQueueName + "/" + pIdMessage).Result;
            return response.Content.ToString();
        }
        public string UpdateMessage(string pQueueName, string pMessage)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods.UpdateMessage/" + pQueueName + "/" + pMessage).Result;
            return response.Content.ToString();
        }
        public string UpdateRPCResponse(string pQueueName, string pIdMessage, string pResponse)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods.UpdateRPCResponse/" + pQueueName + "/" + pIdMessage + "/" + pResponse).Result;
            return response.Content.ToString();
        }
    }
}
