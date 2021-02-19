using System;
using System.Net;
using System.Net.Http;
using System.Web;
using System.IO;
using Newtonsoft.Json;

namespace ZapMQ
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
            HttpResponseMessage response = Connection.GetAsync("TZapMethods/GetMessage/" + pQueueName).Result;
            string contentResponse = response.Content.ReadAsStringAsync().Result;
            RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
            return rootZapJSONResponse.result[0];
        }
        public string GetRPCMessage(string pQueueName, string pIdMessage)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods/GetRPCResponse/" + pQueueName + "/" + pIdMessage).Result;
            string contentResponse = response.Content.ReadAsStringAsync().Result;
            RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
            return rootZapJSONResponse.result[0];
        }
        public string UpdateMessage(string pQueueName, string pMessage)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods/UpdateMessage/" + pQueueName + "/" + pMessage).Result;
            string contentResponse = response.Content.ReadAsStringAsync().Result;
            RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
            return rootZapJSONResponse.result[0];
        }
        public string UpdateRPCResponse(string pQueueName, string pIdMessage, string pResponse)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods/UpdateRPCResponse/" + pQueueName + "/" + pIdMessage + "/" + pResponse).Result;
            string contentResponse = response.Content.ReadAsStringAsync().Result;
            RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
            return rootZapJSONResponse.result[0];
        }
    }
}
