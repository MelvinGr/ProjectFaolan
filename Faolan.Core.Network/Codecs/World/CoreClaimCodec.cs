using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// CoreClaimIIR (0x06EC1255) "new claims available" glow notification - the short push that lights the client's
	// Claims/Redeem button. Body = [opcode][0xC350 Character][handle][u16 sub=0x0009], no payload. Retail sends it at
	// world entry ONLY when the account has unseen claims: it is present in the Faolanx login capture (new claims) but
	// absent in the NecroL80 login_redeem-claim capture (claims already seen), so it is gated on an IsNew flag rather
	// than pushed unconditionally. The full claim list (sub 0x0001, on the client's request) is CoreClaimListCodec;
	// see docs/subsystems/item-claims.md.
	public static class CoreClaimCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CoreClaimIIR;

		public static byte[] Build(uint handle)
		{
			return new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(CoreClaimListCodec.SubNewClaims)
				.ToArray();
		}
	}
}
