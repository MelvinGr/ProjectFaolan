using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// BehaviorControlCenterIIR (0x642CD3D6) - the generic BEHAVIOUR channel. It is the single busiest s2c IIR in the
	// corpus (49158 records carry seq 0x0004 alone) and it carries everything an entity *does*: combat swings, casts,
	// the generic action pulse, animations aimed at world objects, the equip/interact acks, the death transition and
	// the revive.
	//
	// ONE grammar, always:
	//   [u32 subop][u32 class=0xC350][u32 actor][u16 seq][u32 behaviorId][u32 argsLen][args protobuf][tail]
	//
	// That is what collapsed FIVE emitters into this one codec (#530 wave 4). They had each named the same six fields
	// differently and two of them did not realise they were writing this record at all: the death BCC called `seq` a
	// "form" and `behaviorId` a "slot", and the NPC attack ack called the tail a "result block". Same bytes, five
	// vocabularies - exactly the drift rule 5 exists to stop.
	//
	// `Args` is a `CommandMessage` protobuf and `Tail` is behaviour-specific. Both are carried as bytes rather than
	// modelled here, deliberately:
	//   * the args round-trip through protobuf-net only because nothing re-encodes them - `BehaviorAcks` echoes the
	//     CLIENT's own bytes back with one field spliced in, and a re-encode of a field we do not model would change
	//     them;
	//   * the tail genuinely is not one grammar, and the CLIENT says so: `ReadSubClass` (FUN_005e24e0) reads the
	//     phase byte, then this `behaviorId`, then hands off to `InstantiateCommand` (FUN_007dcd60) which looks the
	//     id up in a classmap and constructs a per-COMMAND object that reads the rest itself. So the tail belongs to
	//     the command, not to this record - see BehaviorId, which is that classmap with the client's own names.
	//
	// ⚠️ Every per-command reader the corpus exercises IS decoded now (#536) - see
	// docs/re/behaviorcontrolcenter-grammar.md for the table, which accounts for 67289 of the 67299 captured records
	// with zero mismatches. Eight commands share one no-op reader and so carry NO tail at all (83% of the traffic);
	// the rest are fixed-width but for Search, Magic:Attack, PlayAnimation, ObjectInteraction and the three attack
	// commands. The tail stays `byte[]` here regardless: nothing on the server needs it taken apart yet, and this
	// codec's job is the record, not the command.
	//
	// The semantic forms live beside the codec as VALUE factories (`BehaviorAnimations`, `BehaviorAcks`,
	// `DeathSequence`), not as Build overloads - see CLAUDE.md's codec conventions.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class BehaviorControlCenterCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(BehaviorControlCenterIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static BehaviorControlCenterIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(BehaviorControlCenterCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static BehaviorControlCenterIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "BehaviorControlCenterIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var actor = r.ReadU32Be();
			var seq = r.ReadU16Be();
			var behaviorId = r.ReadU32Be();
			var args = r.ReadBytesU32Length().ToArray();
			return new BehaviorControlCenterIIR(actor, seq, behaviorId, args, r.ReadRemaining().ToArray());
		}

		internal static void Write(FaolanStream s, BehaviorControlCenterIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Actor)
				.WriteUInt16(v.Seq)
				.WriteUInt32(v.BehaviorId)
				.WriteArrayUInt32Length(v.Args)
				.WriteArray(v.Tail);
		}
	}

	// BehaviorControlCenterIIR (0x642CD3D6). `Seq` distinguishes the phases of one behaviour - 0x0004 begin,
	// 0x000A complete, 0x0007 abort, 0x000F commit - and `BehaviorId` selects the COMMAND itself.
	//
	// ⚠️ `Seq`'s high byte is always zero because the client reads its low byte ALONE: `ReadSubClass` takes one u8
	// (the byte before it belongs to the base record) and switches on it, and every value the corpus shows - 2, 3,
	// 4, 7, 0xA, 0xF - is in the branch that then reads a command id. Kept as a u16 because that is the field's
	// width on our side of every emitter; nothing depends on the split.
	//
	// `BehaviorId` values are the client's own catalog - see BehaviorId (0x0F Combat:Melee, 0x07 BccAction, 0x12
	// UseDynel, 0x2A ObjectInteraction, ...). Those four were previously guessed here as "NormalAttack", "the idle
	// pulse", "an object-hold state" and "an object action"; only the last was close.
	public sealed record BehaviorControlCenterIIR(uint Actor, ushort Seq, uint BehaviorId, byte[] Args, byte[] Tail);
}
