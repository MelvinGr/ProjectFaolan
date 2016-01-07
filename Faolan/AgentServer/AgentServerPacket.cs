using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;

namespace Faolan.AgentServer
{
    public class AgentServerPacket : Packet
    {
        public AgentServerPacket(ConanStream stream)
            : base(stream)
        {
            Opcode = (AgentServerOpcodes) stream.ReadUInt16();
            Length = stream.ReadUInt16();

            if (Length == 0 || stream.Length < Length)
            {
                Valid = false;
                return;
            }

            Data = new ConanStream(stream.ReadArray(Length - sizeof(ushort) * 2u));
        }

        public AgentServerOpcodes Opcode { get; protected set; }
        public override bool Valid { get; }
    }
}