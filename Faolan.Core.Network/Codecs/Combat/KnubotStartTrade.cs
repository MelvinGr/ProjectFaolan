using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// KnubotStartTradeIIR_c (0x4B2A47BF) - opens a vendor/NPC trade window. ReadSubClass (FUN_009a63a0) runs the
	// Knubot base prefix then a list of TradeItem_c entries, each named by an XML `<remoteformat>` string (the same
	// Knubot dialogue text as the conversation packets). The trade/dialogue PAYLOAD is that conversation-system
	// structure, so this codec constructs the frame ([handle][env]) and carries the payload verbatim - byte-exact -
	// rather than re-modelling the whole Knubot XML shop grammar here (docs/re/v1-iir-readers.md).
	public record KnubotStartTrade(uint Handle, byte Env, byte[] Payload) : ICodec<KnubotStartTrade>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.KnubotStartTradeIIR;

		public static KnubotStartTrade Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // EntityClass.Character
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			return new KnubotStartTrade(handle, env, s.ReadRemainingArray());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(b => b.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env).WriteArray(Payload));
		}

		public static KnubotStartTrade Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}
