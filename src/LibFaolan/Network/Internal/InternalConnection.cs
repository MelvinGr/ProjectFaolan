using Hik.Communication.Scs.Client;
using Hik.Communication.Scs.Communication.EndPoints.Tcp;
using LibFaolan.Network.Shared;

namespace LibFaolan.Network.Internal
{
    public abstract class InternalConnection
    {
        public int RetryCount = 2;

        protected InternalConnection(string address, uint port)
        {
            Port = port;
            Address = address;

            ScsClient = ScsClientFactory.CreateClient(new ScsTcpEndPoint(Address, (int) Port));
            ScsClient.WireProtocol = new WireProtocol<InternalPacket>();
            ScsClient.Connected += (s, e) => { Connected(); };
            ScsClient.Disconnected += (s, e) => { Disconnected(); };
            ScsClient.MessageReceived += (s, ee) => ReceivedPacket((InternalPacket) ee.Message);
        }

        public IScsClient ScsClient { get; private set; }
        public string Address { get; }
        public uint Port { get; }

        public bool Start()
        {
            for (var i = 0; i < RetryCount; i++)
            {
                try
                {
                    ScsClient.Connect();
                    return true;
                }
                catch
                {
                    //
                }
            }
            return false;
        }

        public bool Stop()
        {
            try
            {
                ScsClient.Disconnect();
                ScsClient = null;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Send(Packet value)
        {
            ScsClient.SendMessage(value);
        }

        public virtual void Connected()
        {
        }

        public virtual void Disconnected()
        {
        }

        public abstract void ReceivedPacket(InternalPacket packet);
    }
}