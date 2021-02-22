﻿using System;
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
            if (response.IsSuccessStatusCode)
            {
                string contentResponse = response.Content.ReadAsStringAsync().Result;
                if (!string.IsNullOrEmpty(contentResponse)) 
                {
                    RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
                    return rootZapJSONResponse.result[0];
                }
                else
                {
                    return string.Empty;
                }
            }
            else
            {
                return string.Empty;
            }
        }
        public string GetRPCMessage(string pQueueName, string pIdMessage)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods/GetRPCResponse/" + pQueueName + "/" + pIdMessage).Result;
            if (response.IsSuccessStatusCode)
            {
                string contentResponse = response.Content.ReadAsStringAsync().Result;
                if (!string.IsNullOrEmpty(contentResponse))
                {
                    RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
                    return rootZapJSONResponse.result[0];
                }
                else
                {
                    return string.Empty;
                }
            }
            else
            {
                return string.Empty;
            }
        }
        public string UpdateMessage(string pQueueName, string pMessage)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods/UpdateMessage/" + pQueueName + "/" + pMessage).Result;
            if (response.IsSuccessStatusCode)
            {
                string contentResponse = response.Content.ReadAsStringAsync().Result;
                if (!string.IsNullOrEmpty(contentResponse))
                {
                    RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
                    return rootZapJSONResponse.result[0];
                }
                else
                {
                    return string.Empty;
                }    
            }
            else
            {
                return string.Empty;
            }
        }
        public string UpdateRPCResponse(string pQueueName, string pIdMessage, string pResponse)
        {
            HttpResponseMessage response = Connection.GetAsync("TZapMethods/UpdateRPCResponse/" + pQueueName + "/" + pIdMessage + "/" + pResponse).Result;
            if (response.IsSuccessStatusCode)
            {
                string contentResponse = response.Content.ReadAsStringAsync().Result;
                if (!string.IsNullOrEmpty(contentResponse))
                {
                    RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
                    return rootZapJSONResponse.result[0];
                }
                else
                {
                    return string.Empty;
                }
            }
            else
            {
                return string.Empty;
            }
        }
    }
}
