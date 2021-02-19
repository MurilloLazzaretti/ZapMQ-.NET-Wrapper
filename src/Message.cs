using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace ZapMQWrapper
{
    public class ZapJSONMessage
    {
        public string Id { get; set; }
        public string Body { get; set; }
        public bool RPC { get; set; }
        public int TTL { get; set; }
        public string ToJSON()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
        public static ZapJSONMessage FromJSON(string pJSONString)
        {
            return JsonConvert.DeserializeObject<ZapJSONMessage>(pJSONString);
        }
    }
}
