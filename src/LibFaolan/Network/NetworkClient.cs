using Hik.Communication.Scs.Server;

namespace LibFaolan.Network
{
    public sealed class NetworkClient
    {
        private string _ipaddress;
        internal NetworkClientDisconnectedDelegate Disconnected;

        public NetworkClient(IScsServerClient client)
        {
            InternalClient = client;
        }

        public object Tag { get; set; }
        public IScsServerClient InternalClient { get; }

        public string IpAddress
        {
            get
            {
                if (_ipaddress == null)
                {
                    _ipaddress = InternalClient.RemoteEndPoint.ToString();

                    var index = _ipaddress.LastIndexOf('/') + 1;
                    _ipaddress = _ipaddress.Substring(index, _ipaddress.Length - index);
                    _ipaddress = _ipaddress.Substring(0, _ipaddress.IndexOf(':'));
                }

                return _ipaddress;
            }
        }

        public void Send(byte[] value)
        {
            try
            {
                InternalClient.SendMessage(new WireProtocol.SendPacket(value));
            }
            catch
            {
                Disconnected?.Invoke(this);
            }
        }

        internal delegate void NetworkClientDisconnectedDelegate(NetworkClient client);
    }
}