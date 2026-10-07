using System;
using System.Collections.Generic;
using System.Net;
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

        // Um HttpClient por endpoint, compartilhado por todo o processo. Criar e
        // descartar um HttpClient por operacao abre um socket TCP novo a cada
        // chamada, que fica em TIME_WAIT por minutos. Com o polling de 300ms das
        // threads isso esgota as portas efemeras do host.
        private static readonly ConcurrentDictionary<string, HttpClient> ConnectionPool =
            new ConcurrentDictionary<string, HttpClient>(StringComparer.OrdinalIgnoreCase);
        private static int ServicePointConfigured;

        private HttpClient GetRestConnection()
        {
            string BaseAddress = "http://" + Host + ":" + Port.ToString() + "/datasnap/rest/";
            return ConnectionPool.GetOrAdd(BaseAddress, CreateRestConnection);
        }

        private static HttpClient CreateRestConnection(string pBaseAddress)
        {
            Uri uri = new Uri(pBaseAddress);
            ConfigureServicePoint(uri);
            HttpClient Connection = new HttpClient();
            Connection.BaseAddress = uri;
            Connection.DefaultRequestHeaders.ConnectionClose = false;
            Connection.DefaultRequestHeaders.Connection.Add("keep-alive");
            return Connection;
        }

        private static void ConfigureServicePoint(Uri pUri)
        {
            // Só tem efeito no .NET Framework; no .NET Core/5+ o SocketsHttpHandler
            // ignora o ServicePointManager e essas chamadas viram no-op.
            try
            {
                if (System.Threading.Interlocked.Exchange(ref ServicePointConfigured, 1) == 0)
                {
                    if (ServicePointManager.DefaultConnectionLimit < 64)
                    {
                        // O padrao fora do ASP.NET e 2 conexoes por endpoint, o que
                        // serializa as filas concorrentes.
                        ServicePointManager.DefaultConnectionLimit = 64;
                    }
                    ServicePointManager.Expect100Continue = false;
                    ServicePointManager.UseNagleAlgorithm = false;
                }
                // Recicla a conexao periodicamente para que o client estatico
                // acompanhe mudancas de DNS do host.
                ServicePointManager.FindServicePoint(pUri).ConnectionLeaseTimeout = 60000;
            }
            catch
            {
                //
            }
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
            ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(GetRestConnection());
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
        public ZapJSONMessage GetRPCResponse(string pQueueName, string pIdMessage)
        {
            ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(GetRestConnection());
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
        public string SendMessage(string pQueueName, ZapJSONMessage pMessage)
        {
            ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(GetRestConnection());
            try
            {
                return methodsClient.UpdateMessage(pQueueName, pMessage.ToJSON());
            }
            catch
            {
                return null;
            }
        }
        public void SendRPCResponse(string pQueueName, string pIdMessage, object pResponse)
        {
            ZapMQMethodsClient methodsClient = new ZapMQMethodsClient(GetRestConnection());
            try
            {
                methodsClient.UpdateRPCResponse(pQueueName, pIdMessage, JsonConvert.SerializeObject(pResponse));
            }
            catch
            {
                //
            }
        }
    }
}
