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
	//   [u32 subop][u32 class=0xC350][u32 actor][u16 seq][u32 delay if seq==0x0E][u32 behaviorId][u32 argsLen][args protobuf][tail]
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

		// The one phase whose record carries an EXTRA u32 in front of the behaviorId - see BehaviorControlCenterIIR.
		private const byte DelayPhase = 0x0E;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(BehaviorControlCenterIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);

			// The phase byte alone decides whether the delay word is on the wire, so a value that disagrees with it
			// would build a body the client reads one field out of step - refuse rather than silently drop/insert.
			if ((value.DelayCentiseconds != null) != IsDelayPhase(value.Seq))
				throw new ArgumentException($"DelayCentiseconds is carried by phase 0x{DelayPhase:X2} and only by it (seq 0x{value.Seq:X4}, delay {value.DelayCentiseconds?.ToString() ?? "none"})", nameof(value));

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
			var delay = IsDelayPhase(seq) ? r.ReadU32Be() : (uint?)null;
			var behaviorId = r.ReadU32Be();
			var args = r.ReadBytesU32Length().ToArray();
			return new BehaviorControlCenterIIR(actor, seq, behaviorId, args, r.ReadRemaining().ToArray(), delay);
		}

		internal static void Write(FaolanStream s, BehaviorControlCenterIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Actor)
				.WriteUInt16(v.Seq);

			if (v.DelayCentiseconds is { } delay)
				s.WriteUInt32(delay);

			s
				.WriteUInt32(v.BehaviorId)
				.WriteArrayUInt32Length(v.Args)
				.WriteArray(v.Tail);
		}

		// The client switches on the LOW byte of `seq` alone (BccIIR::ReadSubClass, FUN_005e24e0), so the high byte -
		// which belongs to the base record - must not take part in the discrimination.
		private static bool IsDelayPhase(ushort seq)
		{
			return (byte)seq == DelayPhase;
		}
	}

	// BehaviorControlCenterIIR (0x642CD3D6). `Seq` distinguishes the phases of one behaviour - 0x0004 begin,
	// 0x000A complete, 0x0007 abort, 0x000F commit - and `BehaviorId` selects the COMMAND itself.
	//
	// ⚠️ `Seq`'s high byte is always zero because the client reads its low byte ALONE: `ReadSubClass` takes one u8
	// (the byte before it belongs to the base record) and switches on it. Kept as a u16 because that is the field's
	// width on our side of every emitter; nothing depends on the split. The corpus shows 2, 3, 4, 5, 7, 0xA, 0xE, 0xF
	// and 0x10 - all but 0xE go straight to a command id, and 0xE reads the delay word first.
	//
	// `BehaviorId` values are the client's own catalog - see BehaviorId (0x0F Combat:Melee, 0x07 BccAction, 0x12
	// UseDynel, 0x2A ObjectInteraction, ...). Those four were previously guessed here as "NormalAttack", "the idle
	// pulse", "an object-hold state" and "an object action"; only the last was close.
	//
	// ⚠️ `DelayCentiseconds` is present for phase 0x0E and ONLY for it, and the phase byte is what says so - the field
	// is not optional data, it is a different record shape. `ReadSubClass`'s `case 0xe` reads a u32 BEFORE the
	// behaviorId (`this+0x24`), and the record's `SetTime` consumer scales it by 0.01 and ADDS it to two of the
	// running Command's six timeline doubles (indices 3 and 4, vtable slot 0x8c) when index 3 is non-zero - i.e. it
	// defers a command already in flight by hundredths of a second. The ten captured records are 0.50-0.71 s, on
	// Magic:Attack casts and on a treasure chest's ObjectInteraction (#549).
	//
	// Missing that extra word is what made those ten look like commands 0x32/0x35/0x39/0x3A/0x47, none of which the
	// client's classmap registers - the shape of the "third registration site" #549 was opened to find. There is no
	// third site: every one of the ten is a Magic:Attack (0x04) or ObjectInteraction (0x2A) read one field out of step.
	public sealed record BehaviorControlCenterIIR(uint Actor, ushort Seq, uint BehaviorId, byte[] Args, byte[] Tail, uint? DelayCentiseconds = null);
}
