using Hik.Communication.Scs.Server;

namespace LibFaolan.Network.Shared
{
    public class NetworkClient
    {
        private string _ipaddress;

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

        public void Send(Packet value)
        {
            InternalClient.SendMessage(value);
        }
    }
}