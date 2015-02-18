using Hik.Communication.Scs.Communication.Protocols;

namespace LibFaolan.Network.Shared
{
    public sealed class ProtocolFactory<T> : IScsWireProtocolFactory
        where T : Packet
    {
        //public bool EnableCompression { get; set; }

        public IScsWireProtocol CreateWireProtocol()
        {
            return new WireProtocol<T>(); //EnableCompression);
        }
    }
}