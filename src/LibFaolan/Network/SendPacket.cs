using Hik.Communication.Scs.Communication.Messages;

namespace LibFaolan.Network
{
    public class SendPacket : IScsMessage
    {
        public SendPacket(byte[] data)
        {
            Data = data;
        }

        public byte[] Data { get; }
        public string MessageId => null;

        public string RepliedMessageId
        {
            get { return null; }
            set { }
        }
    }
}