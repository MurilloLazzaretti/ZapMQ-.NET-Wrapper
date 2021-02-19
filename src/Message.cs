using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace ZapMQ
{
    public class ZapJSONMessage
    {
        public string Id { get; set; }
        public object Body { get; set; }
        public bool RPC { get; set; }
        public int TTL { get; set; }
        public object Response { get; set; }
        public string ToJSON()
        {
            return JsonConvert.SerializeObject(this);
        }
        public static ZapJSONMessage FromJSON(string pJSONString)
        {
            return JsonConvert.DeserializeObject<ZapJSONMessage>(pJSONString);
        }
    }
    public class RootZapJSONMessage
    {
        public string[] result { get; set; }
    }
}
