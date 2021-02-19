using System;
using System.Collections.Generic;
using System.Text;

namespace ZapMQ
{
    public delegate object ZapMQHandler(ZapJSONMessage pMessage, out bool pProcessing);

    public delegate void ZapMQHandlerRPC(ZapJSONMessage pMessage);
}
