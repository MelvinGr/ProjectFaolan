using Hik.Communication.Scs.Server;

namespace LibFaolan.Network
{
    public sealed class NetworkClient
    {
        private readonly IScsServerClient _client;
        private string _ipaddress;
        public object Tag;

        public NetworkClient(IScsServerClient client)
        {
            _client = client;
        }

        public string IpAddress
        {
            get
            {
                if (_ipaddress == null)
                {
                    _ipaddress = _client.RemoteEndPoint.ToString();

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
                _client.SendMessage(new WireProtocol.SendPacket(value));
            }
            catch
            {
            }
        }
    }
}