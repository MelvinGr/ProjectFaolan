using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Codecs;

namespace Faolan.Core.Network.Codecs.GameServer
{
	// Server-originated BehaviorControlCenterIIR (0x642CD3D6) ANIMATIONS - the generic behavior channel that makes a
	// client play an actor's animation/pose. The same channel carries combat swings/casts (CombatPacketCodec) and NPC
	// swing acks; this file is the home for the server-INITIATED animation packets (not echo-acks of client behaviors)
	// as more subsystems need to drive a pose. Each builder returns the record bytes AFTER the 0x2000 carrier's u32
	// length prefix (the caller wraps them in the world-framed IIR carrier), like PetIirCodec.
	//
	// Common BCC framing: [op][0xC350][actor][u16 seq][u32 behaviorId][u32 argsLen][args protobuf][trailer]. `seq`
	// distinguishes begin (0x0004) from complete (0x000A) - the same form-4 -> form-0x0A pairing as a cast. The args
	// and trailer are behavior-specific.
	public static class BehaviorAnimationCodec
	{
		public const ushort SeqBegin = 0x0004;
		public const ushort SeqComplete = 0x000A;
		private const uint Bcc = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR;

		// The caster's component-ADD gesture on each weave add - a begin/complete pair carrying the added element
		// (frames 1657/1658): behaviorId 4, args {f1=AddAction, f3=5, f5=8, f6=0, f10=SpellTarget{f1=0xC350, f2=caster}},
		// trailer [1][element][0x01][0][0][0][completeFlag].
		private const uint SpellweaveAddAction = 186463;

		// The caster's trance-EXIT animation on spellweave cancel/fire (cancel-weave capture, frame 141.61): behaviorId
		// 1, args {f1=ExitAction, f3=6, f5=8, f6=0, f11=79}, trailer [4][0x27][0][0][0]. Without it the buffs clear but
		// the character stays stuck in the trance pose. Action params are capture-derived (necro).
		private const uint SpellweaveExitAction = 41302;

		// The caster's TRANCE-POSE animation on spellweave ACTIVATE (Captures/decode_spellweave.py frame 1645):
		// behaviorId 1, args {f1=TranceAction, f3=4, f5=8, f6=0, f11=1199}, trailer [0x27][1][0][0][0]. Without it the
		// trance buffs render but the character stays idle. Action params are capture-derived (necro).
		private const uint SpellweaveTranceAction = 42041;
		private const uint Type = EntityClass.Character; // the shared 0xC350 type word

		public static byte[] SpellweaveComponentAdd(uint caster, uint elementBaseId, bool complete)
		{
			var spellTarget = new FaolanStream().WriteByte(0x08).WriteVarint(Type).WriteByte(0x10).WriteVarint(caster).ToArray();
			var args = new FaolanStream()
				.WriteByte(0x08)
				.WriteVarint(SpellweaveAddAction)
				.WriteByte(0x18)
				.WriteVarint(5)
				.WriteByte(0x28)
				.WriteVarint(8)
				.WriteByte(0x30)
				.WriteVarint(0)
				.WriteByte(0x52)
				.WriteVarint((ulong)spellTarget.Length)
				.WriteArray(spellTarget)
				.ToArray();

			var trailer = new FaolanStream().WriteUInt32(1).WriteUInt32(elementBaseId).WriteByte(0x01).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).WriteByte(complete).ToArray();

			return Build(caster,
				complete
					? SeqComplete
					: SeqBegin,
				4,
				args,
				trailer);
		}

		public static byte[] SpellweaveTrance(uint caster)
		{
			var args = new FaolanStream()
				.WriteByte(0x08)
				.WriteVarint(SpellweaveTranceAction)
				.WriteByte(0x18)
				.WriteVarint(4)
				.WriteByte(0x28)
				.WriteVarint(8)
				.WriteByte(0x30)
				.WriteVarint(0)
				.WriteByte(0x58)
				.WriteVarint(1199)
				.ToArray();

			var trailer = new FaolanStream().WriteUInt32(0x27).WriteUInt32(1).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).ToArray();

			return Build(caster, SeqBegin, 1, args, trailer);
		}

		public static byte[] SpellweaveTranceExit(uint caster)
		{
			var args = new FaolanStream().WriteByte(0x08).WriteVarint(SpellweaveExitAction).WriteByte(0x18).WriteVarint(6).WriteByte(0x28).WriteVarint(8).WriteByte(0x30).WriteVarint(0).WriteByte(0x58).WriteVarint(79).ToArray();

			var trailer = new FaolanStream().WriteUInt32(4).WriteUInt32(0x27).WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).ToArray();

			return Build(caster, SeqBegin, 1, args, trailer);
		}

		// The shared BCC framing primitive: header + behaviorId + length-prefixed args + the behavior-specific trailer.
		private static byte[] Build(uint actor, ushort seq, uint behaviorId, byte[] args, byte[] trailer)
		{
			return new FaolanStream().WriteUInt32(Bcc).WriteUInt32(Type).WriteUInt32(actor).WriteUInt16(seq).WriteUInt32(behaviorId).WriteUInt32(args.Length).WriteArray(args).WriteArray(trailer).ToArray();
		}
	}
}
