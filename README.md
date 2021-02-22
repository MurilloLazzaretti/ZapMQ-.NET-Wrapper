## 🇧🇷 ZapMQ-.NET-Wrapper 🇧🇷

Wrapper for .NET to connect to [`ZapMQ server.`](https://github.com/MurilloLazzaretti/ZapMQ) With this wrapper you can connect easily and with a low code you can send/recive messages to/from others ZapMQ clients.

## ⚙️ Installation

Download the lastest realease of this repository and add to your project like a reference ZapMQWrapper.dll file.

## 💉 Dependency

ZapMQWrapper needs Newtonsoft.Json library installed in your project. Install it by NuGet.

## ⚡️ First step

You need to create the Wrapper and provide the IP and Port of the ZapMQ service.

```cs
using ZapMQ;

{
    zapMQWrapper = new ZapMQWrapper("localhost", 5679);
}

```
Probably ZapMQWrapper will gona be a Field of a Form in your application or Property in a class, the code above is just for example.

Dont forget to free ZapMQWrapper when your application will terminate, this will stop all threads and kill others objects. (Please, no memory leak)

```cs
{
    zapMQWrapper.StopThreads();
} 
```
