namespace ZapMQ
{
    // Timings of the wrapper, in milliseconds. They are fixed for applications; only the tests
    // shorten them.
    internal class ZapMQSettings
    {
        // Opening the connection and greeting the server.
        public int ConnectTimeoutMs = 3000;

        // How long SendMessage and SendRPCMessage wait for a connection before giving up.
        public int SendWaitMs = 5000;

        // A request without an answer for this long means the connection is no longer usable.
        public int RequestTimeoutMs = 30000;

        // Waits between attempts while the server does not answer at all.
        public int ReconnectMinMs = 250;
        public int ReconnectMaxMs = 5000;

        // While working with a 1.x server, how often the v2 protocol is tried again.
        public int V2ProbeIntervalMs = 60000;

        public int PingIntervalMs = 15000;
        public int PingTimeoutMs = 30000;

        // Interval of the 1.x polling and of the check for expired RPCs.
        public int PollIntervalMs = 300;

        // How long StopThreads waits for the message being processed to be confirmed.
        public int StopWaitMs = 2000;
    }
}
