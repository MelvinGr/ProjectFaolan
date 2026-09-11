using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     BehaviorControlCenterIIR - the generic behaviour channel, and the busiest s2c IIR in the corpus (49158 records
	///     carry seq 0x0004 alone). It carries everything an entity does: combat swings, casts, the generic action pulse,
	///     animations aimed at world objects, the equip/interact acks, the death transition and the revive.
	/// </summary>
	/// <remarks>
	///     A record is <c>[u32 subop][u32 class][u32 actor][u16 seq][the phase's own payload]</c>, and the phase byte
	///     chooses the payload's grammar. The client's reader (BccIIR::ReadSubClass, FUN_005e24e0) is the spec, mirrored
	///     exactly by its writer (FUN_005e2650):
	///     <code>
	///     2,3,4,5,7,0xA,0xF,0x10,0x12 : [u32 behaviorId][u32 argsLen][args protobuf][tail]   - a command
	///     0x0E                        : [u32 delay][u32 behaviorId][u32 argsLen][args][tail] - a command, deferred
	///     0x0B,0x0C                   : [u32 substateId]                                     - and nothing else
	///     6,8,9,0xD,0x11              : nothing at all
	///     </code>
	///     <para>
	///         <c>Args</c> is a CommandMessage protobuf and <c>Tail</c> is behaviour-specific. Both are carried as bytes
	///         rather than modelled here, deliberately. The args round-trip only because nothing re-encodes them -
	///         <see cref="BehaviorAcks" /> echoes the client's own bytes back with one field spliced in, and re-encoding a
	///         field the schema does not model would change them. The tail genuinely is not one grammar, and the client says
	///         so: ReadSubClass reads the phase byte, then the behaviourId, then hands off to InstantiateCommand
	///         (FUN_007dcd60), which looks the id up in a classmap and constructs a per-command object that reads the rest
	///         itself. The tail belongs to the command, not to this record - see <see cref="BehaviorId" />, which is that
	///         classmap under the client's own names.
	///     </para>
	///     <para>
	///         Every per-command reader the corpus exercises is decoded; docs/re/behaviorcontrolcenter-grammar.md holds the
	///         table, which accounts for 67289 of the 67299 captured records with no mismatches. Eight commands share one
	///         no-op reader and carry no tail at all (83% of the traffic); the rest are fixed-width but for Search,
	///         Magic:Attack, PlayAnimation, ObjectInteraction and the three attack commands. The tail stays a byte array
	///         here regardless: this codec's job is the record, not the command.
	///     </para>
	///     <para>
	///         The semantic forms live beside the codec as value factories (<see cref="BehaviorAnimations" />,
	///         <see cref="BehaviorAcks" />, <see cref="DeathSequence" />) rather than Build overloads. The u32 carrier
	///         length belongs to the framing sender.
	///     </para>
	/// </remarks>
	public static class BehaviorControlCenterCodec
	{
		public const uint Opcode = (uint)IirTypes.BehaviorControlCenterIIR;

		/// <summary>
		///     The one phase whose record carries an extra u32 in front of the behaviourId; see
		///     <see cref="BehaviorControlCenterIIR" />.
		/// </summary>
		private const byte DelayPhase = 0x0E;

		/// <summary>
		///     The two phases that carry a bare substate id instead of a command: 0x0B enters it, 0x0C leaves it. See
		///     <see cref="BehaviorControlCenterIIR" />.
		/// </summary>
		private static readonly byte[] SubstatePhases = [0x0B, 0x0C];

		/// <summary>
		///     The phases that read a command: the client ReadSubClass's first switch arm, plus the delay phase. Every other
		///     phase byte - 6/8/9/0xD/0x11 explicitly, and anything unlisted through the default arm - ends at the seq word.
		/// </summary>
		/// <remarks>
		///     This must stay a positive list. The client's default arm reads nothing, so treating an unseen phase byte as a
		///     command would make it parse the following record's bytes as a command block and swallow it.
		/// </remarks>
		private static readonly byte[] CommandPhases = [0x02, 0x03, 0x04, 0x05, 0x07, 0x0A, 0x0E, 0x0F, 0x10, 0x12];

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(BehaviorControlCenterIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);

			// The phase byte alone decides the record's shape, so a value that disagrees with it would build a body the
			// client reads out of step. Refuse rather than silently drop or insert a field.
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

			// The client's case 0xe reads the commandId only when the delay is non-zero; a zero delay ends the record
			// there. No captured record takes that arm (all eight are 0.50-0.71 s), but the reader's shape is the contract.
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

		/// <summary>
		///     The client switches on the low byte of the seq alone (BccIIR::ReadSubClass, FUN_005e24e0), so the high byte -
		///     which belongs to the base record - must not take part in the discrimination.
		/// </summary>
		private static bool IsDelayPhase(ushort seq)
		{
			return (byte)seq == DelayPhase;
		}

		private static bool IsSubstatePhase(ushort seq)
		{
			return Array.IndexOf(SubstatePhases, (byte)seq) >= 0;
		}

		/// <summary>
		///     Whether this phase reads a behaviourId, args and tail at all. The client's default arm reads nothing, so an
		///     unlisted phase ends at the seq word exactly like the terminal ones; hence one test for both.
		/// </summary>
		private static bool CarriesCommand(ushort seq)
		{
			return Array.IndexOf(CommandPhases, (byte)seq) >= 0;
		}
	}

	/// <summary>
	///     The BehaviorControlCenterIIR body. <c>Seq</c> distinguishes the phases of one behaviour - 0x0004 begin, 0x000A
	///     complete, 0x0007 abort, 0x000F commit - and <c>BehaviorId</c> selects the command itself, from the client's own
	///     catalog (see <see cref="BehaviorId" />: 0x0F Combat:Melee, 0x07 BccAction, 0x12 UseDynel, 0x2A
	///     ObjectInteraction, ...).
	/// </summary>
	/// <remarks>
	///     <c>Seq</c>'s high byte is always zero because the client reads its low byte alone: ReadSubClass takes one u8 -
	///     the byte before it belongs to the base record - and switches on that. It is kept as a u16 because that is the
	///     field's width on our side of every emitter; nothing depends on the split. The corpus shows 2, 3, 4, 5, 7, 0xA,
	///     0xE, 0xF and 0x10; all but 0xE go straight to a command id, and 0xE reads the delay word first.
	///     <para>
	///         <c>DelayCentiseconds</c> is present for phase 0x0E and only for it, and the phase byte is what says so: the
	///         field is not optional data, it is a different record shape. ReadSubClass's case 0xe reads a u32 before the
	///         behaviourId, and the record's SetTime consumer scales it by 0.01 and adds it to two of the running Command's
	///         six timeline doubles (indices 3 and 4, vtable slot 0x8c) when index 3 is non-zero, deferring a command already
	///         in flight. The ten captured records are 0.50-0.71 s, on Magic:Attack casts and a treasure chest's
	///         ObjectInteraction. Reading them without the extra word makes them look like commands
	///         0x32/0x35/0x39/0x3A/0x47, none of which the client's classmap registers.
	///     </para>
	///     <para>
	///         <c>SubstateId</c> is present for phases 0x0B/0x0C and only for them, and is likewise a different record shape
	///         rather than optional data: those two arms of ReadSubClass read one u32 and return, so the record carries no
	///         command at all and is 18 bytes on the wire. Nine such records are captured. 0x0B enters a substate and 0x0C
	///         leaves it. BccIIR's vtable slot 2 (FUN_005E1C70) is the executor and the only other function to touch the
	///         field: its case 0xb calls FUN_005EB5C0(bcc, id) and its case 0xc FUN_005EB890, gated on one predicate and its
	///         negation. FUN_005EB5C0 scans the BCC's active-substate list for the id and logs "Tried to enter substate %d.
	///         Already in it!" on a hit, or "Tried to enter substate %d, but did not find it in this bcc (%d)" when the id
	///         does not resolve. The same function reads stats 0x14 and 0x15, whose client-side names are BCCState and BCC.
	///     </para>
	///     <para>
	///         The substate table is RDB 1010203, plain-text XML: <c>&lt;substate id="84" name="Charged" priority="17"&gt;</c>,
	///         where priority is what FUN_005EB5C0 orders the active list on. All four captured values resolve: 33
	///         DefensiveStance, 60 FrenzyStance, 83 Crouching, 84 Charged. The recurring 60 is a combat stance, which is why
	///         four unrelated actors carry it. <c>--report bcc-trees</c> prints the table.
	///     </para>
	///     <para>
	///         Scoping is per-BCC by the client's rule - its error says "in this bcc" - but the shipped data does not
	///         collide: over all 29 trees every shared substate id carries the same name and priority, while state id 11 is
	///         LoopingEmote in one tree and SneakHiding in another. The field therefore stays a plain uint rather than an
	///         enum: the consistency is a fact about this build's data, not a guarantee from the format, and the report is
	///         where it is re-checked.
	///     </para>
	/// </remarks>
	public sealed record BehaviorControlCenterIIR(uint Actor, ushort Seq, uint BehaviorId, byte[] Args, byte[] Tail, uint? DelayCentiseconds = null, uint? SubstateId = null);
}
