using System;
using System.Net;
using System.Net.Http;
using System.Web;
using System.IO;
using Newtonsoft.Json;
using System.Text;
using static System.Net.Mime.MediaTypeNames;
using System.Linq;

namespace ZapMQ
{
    public class ZapMQMethodsClient
    {
        private readonly HttpClient Connection;
        public ZapMQMethodsClient(HttpClient pConnection)
        {
            Connection = pConnection;
            
        }
        // Descartar a HttpResponseMessage devolve a conexao ao pool assim que a
        // resposta e lida. O ConfigureAwait(false) evita deadlock quando a chamada
        // parte de uma thread com SynchronizationContext (WinForms/WPF/ASP.NET).
        private string Execute(string pResource)
        {
            using (HttpResponseMessage response = Connection.GetAsync(pResource).ConfigureAwait(false).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    return string.Empty;
                }
                string contentResponse = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(contentResponse))
                {
                    return string.Empty;
                }
                RootZapJSONMessage rootZapJSONResponse = JsonConvert.DeserializeObject<RootZapJSONMessage>(contentResponse);
                return rootZapJSONResponse.result[0];
            }
        }
        public string GetMessage(string pQueueName)
        {
            return Execute("TZapMethods/GetMessage/" + pQueueName);
        }
        public string GetRPCMessage(string pQueueName, string pIdMessage)
        {
            return Execute("TZapMethods/GetRPCResponse/" + pQueueName + "/" + pIdMessage);
        }
        public string UpdateMessage(string pQueueName, string pMessage)
        {
            return Execute("TZapMethods/UpdateMessage/" + pQueueName + "/" + System.Net.WebUtility.UrlEncode(pMessage).Replace("+", " "));
        }
        public string UpdateRPCResponse(string pQueueName, string pIdMessage, string pResponse)
        {
            return Execute("TZapMethods/UpdateRPCResponse/" + pQueueName + "/" + pIdMessage + "/" + System.Net.WebUtility.UrlEncode(pResponse).Replace("+", " "));
        }
    }
}
