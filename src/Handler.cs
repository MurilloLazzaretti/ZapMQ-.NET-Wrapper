using System;
using System.Collections.Generic;
using System.Text;

namespace ZapMQWrapper
{
    public delegate string ZapMQHandler(ZapJSONMessage pMessage, out bool pProcessing);

    public delegate string ZapMQHandlerRPC(ZapJSONMessage pMessage);
}
