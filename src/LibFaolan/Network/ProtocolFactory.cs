using Hik.Communication.Scs.Communication.Protocols;

namespace LibFaolan.Network
{
    public class ProtocolFactory<TWireProtocol> : IScsWireProtocolFactory
        where TWireProtocol : IScsWireProtocol, new()
    {
        public IScsWireProtocol CreateWireProtocol()
        {
            return new TWireProtocol();
        }
    }
}