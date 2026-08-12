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
	// The grammar is chosen by the PHASE byte, and the client's own reader (`BccIIR::ReadSubClass`, FUN_005e24e0) is
	// the spec - four shapes, mirrored exactly by its writer (FUN_005e2650):
	//
	//   2,3,4,5,7,0xA,0xF,0x10,0x12 : [u32 behaviorId][u32 argsLen][args protobuf][tail]   - a COMMAND
	//   0x0E                        : [u32 delay][u32 behaviorId][u32 argsLen][args][tail] - a command, deferred
	//   0x0B,0x0C                   : [u32 stateWord]                                      - and nothing else
	//   6,8,9,0xD,0x11              : (nothing at all)
	//
	// so a record is `[u32 subop][u32 class=0xC350][u32 actor][u16 seq][ the phase's own payload ]`.
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
		public const uint Opcode = (uint)IirTypes.BehaviorControlCenterIIR;

		// The one phase whose record carries an EXTRA u32 in front of the behaviorId - see BehaviorControlCenterIIR.
		private const byte DelayPhase = 0x0E;

		// The two phases that carry a bare SUBSTATE ID instead of a command: 0x0B enters it, 0x0C leaves it
		// (#810) - see BehaviorControlCenterIIR.SubstateId.
		private static readonly byte[] SubstatePhases = [0x0B, 0x0C];

		// The phases that read a COMMAND (client `ReadSubClass`'s first switch arm, plus the delay phase). Every other
		// phase byte - 6/8/9/0xD/0x11 explicitly, and anything unlisted through the `default:` arm - ends at the seq
		// word. This is a positive list precisely because `default:` reads nothing: guessing the other way round would
		// make an unseen phase byte parse as a command and swallow the next record.
		private static readonly byte[] CommandPhases = [0x02, 0x03, 0x04, 0x05, 0x07, 0x0A, 0x0E, 0x0F, 0x10, 0x12];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(BehaviorControlCenterIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);

			// The phase byte alone decides the record's SHAPE, so a value that disagrees with it would build a body
			// the client reads out of step - refuse rather than silently drop/insert a field.
			if ((value.DelayCentiseconds != null) != IsDelayPhase(value.Seq))
				throw new ArgumentException($"DelayCentiseconds is carried by phase 0x{DelayPhase:X2} and only by it (seq 0x{value.Seq:X4}, delay {value.DelayCentiseconds?.ToString() ?? "none"})", nameof(value));

			if ((value.SubstateId != null) != IsSubstatePhase(value.Seq))
				throw new ArgumentException($"SubstateId is carried by phases 0x0B/0x0C and only by them (seq 0x{value.Seq:X4}, substate {value.SubstateId?.ToString() ?? "none"})", nameof(value));

			if (!CarriesCommand(value.Seq) && (value.BehaviorId != 0 || value.Args.Length != 0 || value.Tail.Length != 0))
				throw new ArgumentException($"phase 0x{(byte)value.Seq:X2} carries no command block, so it has nowhere to put one (seq 0x{value.Seq:X4}, behavior 0x{value.BehaviorId:X}, {value.Args.Length} args + {value.Tail.Length} tail bytes)", nameof(value));

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

			if (IsSubstatePhase(seq))
				return new BehaviorControlCenterIIR(actor, seq, 0, [], [], SubstateId: r.ReadU32Be());

			if (!CarriesCommand(seq))
				return new BehaviorControlCenterIIR(actor, seq, 0, [], []);

			var delay = IsDelayPhase(seq) ? r.ReadU32Be() : (uint?)null;

			// `case 0xe` reads the commandId only when the delay is non-zero - a zero delay ends the record there.
			// No captured record takes that arm (all eight are 0.50-0.71 s), but the reader's shape is the contract.
			if (delay == 0)
				return new BehaviorControlCenterIIR(actor, seq, 0, [], [], delay);

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

			if (v.SubstateId is { } substate)
			{
				s.WriteUInt32(substate);
				return;
			}

			if (!CarriesCommand(v.Seq))
				return;

			if (v.DelayCentiseconds is { } delay)
			{
				s.WriteUInt32(delay);
				if (delay == 0)
					return; // see Read: a zero delay ends the record before the commandId
			}

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

		private static bool IsSubstatePhase(ushort seq)
		{
			return Array.IndexOf(SubstatePhases, (byte)seq) >= 0;
		}

		// Whether this phase reads a behaviorId + args + tail at all. The client's `default:` arm reads NOTHING, so an
		// unlisted phase ends at the seq word exactly like the terminal ones - which is why the two are one test here.
		private static bool CarriesCommand(ushort seq)
		{
			return Array.IndexOf(CommandPhases, (byte)seq) >= 0;
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
	//
	// ⭐ `SubstateId` is present for phases 0x0B/0x0C and ONLY for them, and like the delay it is a different record
	// SHAPE rather than optional data: those two arms of `ReadSubClass` read one u32 into `this+0x28` and return, so
	// the record carries NO command at all and is 18 bytes on the wire. Nine such records are captured (#809).
	//
	// **0x0B ENTERS a substate and 0x0C LEAVES it; the word is the substate id** (#810). The client names it itself.
	// `BccIIR`'s vtable slot 2 (`FUN_005E1C70`) is the executor, and it is the only other function that touches
	// `+0x28`: its `case 0xb` calls `FUN_005EB5C0(bcc, id)` and its `case 0xc` calls `FUN_005EB890`, gated on one
	// predicate and its negation. `FUN_005EB5C0` scans the BCC's ACTIVE-SUBSTATE list for the id and logs
	// "Tried to enter substate %d. Already in it!" on a hit, or "Tried to enter substate %d, but did not find it in
	// this bcc (%d)" when the id does not resolve - both messages are about the argument, which is what names it.
	// Corroborated from a second direction: the same function reads stats 0x14 and 0x15, whose client-side names are
	// `BCCState` and `BCC`, and `Substate` is already a known GameAction key (174 / 0x4B6) across ten kinds.
	//
	// ⭐ **The table is RDB 1010203, and it is plain-text XML** (#812) - `<substate id="84" name="Charged"
	// priority="17">`, where `priority` is what `FUN_005EB5C0` orders the active list on. All four captured values
	// resolve: 33 `DefensiveStance`, 60 `FrenzyStance`, 83 `Crouching`, 84 `Charged`. The recurring 60 is a combat
	// STANCE, which is why four unrelated actors carry it - not a default. `--report bcc-trees` prints the table.
	//
	// ⚠️ **Scoping is per-BCC by the client's RULE** - its error says "in this bcc" - but the shipped data does not
	// collide: over all 29 trees every shared substate id carries the same name AND priority, while STATE id 11 is
	// `LoopingEmote` in one tree and `SneakHiding` in another. So this stays a plain `uint` here rather than an enum:
	// the consistency is a fact about this build's data, not a guarantee from the format, and the report is where it
	// gets re-checked.
	public sealed record BehaviorControlCenterIIR(uint Actor, ushort Seq, uint BehaviorId, byte[] Args, byte[] Tail, uint? DelayCentiseconds = null, uint? SubstateId = null);
}
