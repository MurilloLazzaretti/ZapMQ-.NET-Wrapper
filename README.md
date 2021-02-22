## 🇧🇷 ZapMQ-.NET-Wrapper 🇧🇷

Wrapper for .NET to connect to [`ZapMQ server.`](https://github.com/MurilloLazzaretti/ZapMQ) With this wrapper you can connect easily and with a low code you can send/recive messages to/from others ZapMQ clients.

## ⚙️ Installation

Download the lastest realease of this repository and add to your project like a reference ZapMQWrapper.dll file.

## 💉 Dependency

ZapMQWrapper needs Newtonsoft.Json library installed in your project. Install it by NuGet package manager.

## ⚡️ First step

You need to create the Wrapper and provide the IP and Port of the ZapMQ service.

```cs
using ZapMQ;

{
    zapMQWrapper = new ZapMQWrapper("localhost", 5679);
}

```
Probably ZapMQWrapper will gona be a Field of a Form in your application or Property in a class, the code above is just for example.

Dont forget to free ZapMQWrapper when your application will terminate, this will stop all threads and kill others objects.

```cs
{
    zapMQWrapper.StopThreads();
} 
```
## 🧬 Resources

👂 _Publisher and Subscriber_

Send a message to a dermined queue with <b>no answer.</b> "One" of the "N" subscribers registered in this queue will process your message.

_Publisher_

In this type of message, you gonna send an object to a determined queue and you gonna have <b>no answer.</b> See the code below :

```cs

MyObject message = new MyObject();
message.message = "message to send";
if (zapMQWrapper.SendMessage("MyQueue", message, 5000))
{
    // Success to send the message 
}
else
{
    // Error to send the message
}
```
The code above, send an Object, with no answer to 'MyQueue' and this message has a TTL of 5 seconds, so if this message was not processed for any subscriber of this queue in 5 seconds, this message will gonna die in the server. If you dont want a TTL to your message, send 0.

_Subscriber_

To subscribe your application in a Queue, just bind it with his name and associate a handler :

```cs
{
    zapMQWrapper.Bind("MyQueue", zapMQHandler);
}
```

```cs
public object zapMQHandler(ZapJSONMessage pMessage, out bool pProcessing)
{
    try
    {
        // Do what you need with the message, for example :
        Log(pMessage.Body.ToString)
    }
    finally
    {
        pProcessing = False; // Telling the thread that you are done with this message and you can process another one.
    }
    return null;
}
```
⚠️ _Warning_

If you dont tell the thread that you finish to process the message (pProcessing = False), you never recive another one.

🔌 _RPC_ 

Send an object to a dermined queue with <b> answer.</b> "One" of the "N" subscribers registered in this queue will process your message and send an answer to the publisher.

_Publisher_

In this type of message, you gonna send an object to a determined queue and you gonna have <b>an answer.</b> See the code below :

```cs
{
    MyObject message = new MyObject();
    message.message = "RPC message to send";
    if (zapMQWrapper.SendRPCMessage("MyQueue", message, zapMQHandlerRPC, 5000))
    {
        // Success to send the message 
    }
    else
    {
        // Error to send the message
    }
}
```

The code above, send a JSON Object to 'MyQueue' and 'wait' for response asynchronously. This message has a TTL of 5 seconds, so if this message was not processed/answered for any subscriber of this queue in 5 seconds, this message will gonna die in the server and the thread on the publisher too. If you dont want a TTL to your message, send 0, but take care of it, if this this message never process for any subscriber you gonna have a started thread for ever.

_OnRPCExpired_

There is an event on the wrapper that raise when one of your RPC message was expired, this could be useful !

```cs
{
    zapMQWrapper.OnRPCExpired = RPCExpired;
}

public void RPCExpired(ZapJSONMessage pMessage)
{
    // Do what you need with the message, for example :
  Log('Message expired:' + pMessage.Id);
}

```

_Processing the answer_

```cs
public void zapMQHandlerRPC(ZapJSONMessage pMessage)
{
  // Do what you need with the message, for example :
  Log(pMessage.Body.ToString())
}
```

_Subscriber_

To subscribe your application in a Queue, just bind it with his name and associate a handler :

```cs
{
    zapMQWrapper.Bind("MyQueue", zapMQHandlerRPCMessage);
}
```
Processing the message :

```cs
public object zapMQHandlerRPCMessage(ZapJSONMessage pMessage, out bool pProcessing)
{
    try
    {
        // Do what you need with the message, for example :
        Log(pMessage.Body.ToString());
    }
    finally
    {
        pProcessing = false;
    }
    MyObject message = new MyObject();
    message.message = "RPC Answer Message";
    return message;
}
```

✏ _Tips_

The class ZapJSONMessage have a boolean property named RPC, if you want, you can have only one handler for the same queue binded and if the publisher needs an answer you do, other wise, result null :

```cs
public object zapMQHandlerRPCMessage(ZapJSONMessage pMessage, out bool pProcessing)
{
    try
    {
        // Do what you need with the message, for example :
        Log(pMessage.Body.ToString());
    }
    finally
    {
        pProcessing = false;
    }
    if (pMessage.RPC)
    {
        MyObject message = new MyObject();
        message.message = "RPC Answer Message";
        return message;
    }
    else
    {
        return null;
    }    
}
```

⚠️ _Warning_

If you dont tell the thread that you finish to process the message (pProcessing = False), you never recive another one.

🌐 _Exchange_ (Coming soon)

Send a message to a dermined queue with <b>no answer.</b> "All" of the subscribers registered in this queue will process your message. 