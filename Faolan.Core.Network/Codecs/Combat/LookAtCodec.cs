using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// LookAtIIR (0x0D155695) - TARGET SELECTION. The client sends it when the player picks a target and the server
	// echoes it; NPCs and pets send it too, which is how a pet's chosen target is visible on the wire.
	//
	//   [subop][class][subject][u8 env][u32 targetClass][u32 target][u32 trailerA][u32 trailerB]     (29 B)
	//
	// A zero target is not a missing field - it is the subject CLEARING their selection, and it is 26 of the 112
	// LookAtIIR events in the necromancer Tortage capture. Anything that treats 0 as "unknown" loses the deselect. Both
	// target words go to zero together: a null entity reference is `[class 0][handle 0]`, not a handle of 0 under a
	// live class word.
	//
	// ⚠️ It does NOT share DamageResultIIR's layout even though both name two entities: the target endpoint sits at
	// 13/17 here against 17/21 there. Reading one at the other's offsets yields a plausible-looking handle
	// (0x500003b2 - half a class word and half a handle), which is exactly the kind of confident nonsense a fixed
	// offset copied between message types produces.
	//
	// The two trailing words are not decoded. `[0][2]` is what a live selection carries and `[2][2]` is what the
	// death-deselect carries (Stickienecro idx 545), so they are fields rather than constants - which is how the
	// death sequence's hand-rolled copy of this record merged into it (#530 wave 4).
	//
	// [V2: xxx_mage-necromancer_login_play2_tortage_city_first-quests.tshark.json - 112 events, 29-byte bodies,
	// c2s selections matching the conversations and fights that follow them]
	public static class LookAtCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.LookAtIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(LookAtIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// A live selection. `target` 0 = clear the selection, which nulls the class word with it.
		public static byte[] Build(uint subject, uint target, uint trailerB = 2)
		{
			return Build(LookAts.Select(subject, target, trailerB));
		}

		public static LookAtIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(LookAtCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static LookAtIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "LookAtIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var subject = r.ReadU32Be();
			var env = r.ReadU8();
			var targetClass = r.ReadU32Be();
			var target = r.ReadU32Be();
			return new LookAtIIR(subject, env, targetClass, target, r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, LookAtIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Subject)
				.WriteByte(v.Env)
				.WriteUInt32(v.TargetClass)
				.WriteUInt32(v.Target)
				.WriteUInt32(v.TrailerA)
				.WriteUInt32(v.TrailerB);
		}
	}

	// LookAtIIR (0x0D155695).
	public sealed record LookAtIIR(uint Subject, byte Env, uint TargetClass, uint Target, uint TrailerA, uint TrailerB);

	// The semantic forms of a LookAtIIR, as VALUE factories - the two differ only in the trailing words, so they cannot
	// be told apart by a `Build` signature.
	public static class LookAts
	{
		// `target` 0 = clear the selection.
		public static LookAtIIR Select(uint subject, uint target, uint trailerB = 2)
		{
			return new LookAtIIR(subject, 0x00, target == 0
				? 0
				: EntityClass.Character, target, 0, trailerB);
		}

		// The dying entity DROPS ITS TARGET, sent between the killing damage and the death pose. Retail sends it on 95%
		// of captured kills - Stickienecro idx 545 is the worked example. We were never sending it, so a corpse died
		// still holding the aggro target it had a frame earlier.
		public static LookAtIIR DropTarget(uint subject)
		{
			return new LookAtIIR(subject, 0x00, 0, 0, 2, 2);
		}
	}
}
