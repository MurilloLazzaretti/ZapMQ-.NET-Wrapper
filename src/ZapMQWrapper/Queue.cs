using System;
using System.Collections.Generic;
using System.Text;

namespace ZapMQWrapper
{
    public class ZapMQQueue
    {
        public string Name { get; set; }
        public ZapMQHandler Handler { get; set; }
    }
}
