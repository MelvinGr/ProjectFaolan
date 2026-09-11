using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     The echo-ack forms of <see cref="BehaviorControlCenterIIR" />, as value factories: retail acks a client behaviour
	///     request by echoing it back with field 3 (the phase) spliced in and a 12-byte pad, or an equip identity block.
	/// </summary>
	/// <remarks>
	///     Not a codec: the whole family is "take the client's own record and send it back changed", so the input is a
	///     parsed request rather than loose fields, and <see cref="BehaviorControlCenterCodec" /> is both the reader and the
	///     writer. Byte-exact in BehaviorAckTests / BehaviorPhaseFieldTests; CombatService owns the FSM and the send.
	/// </remarks>
	public static class BehaviorAcks
	{
		/// <summary>
		///     The behaviours whose echoed tail takes 12 zero bytes of padding. Pairing each c2s request with its s2c ack
		///     across every login capture, only the two hand-weapon attack commands pad an echoed tail: Combat:Melee (0x0F,
		///     +12 in 85 acks) and Combat:Unarmed (0x11, +12 in 3). Every other behaviour echoes the tail unchanged - +0
		///     across 25 acks for Transition (0x01), 87 for Magic:Attack (0x04), 174 for UseCharacter (0x16, talk), 66 for
		///     UseDynel (0x12, the bench-sit), 15 for ObjectInteraction (0x2A, the sit animation) and 13 for Equip/Mount
		///     (0x09/0x0A). The much larger 0x0F deltas (53, 94, ...) belong to the separate result-block ack for a targeted
		///     hit, not to this echo path.
		/// </summary>
		/// <remarks>
		///     Padding a behaviour that does not take a pad makes the ack 8-12 bytes too long and the client rejects the
		///     record outright: it parses to the end of what it expects, reads the leftovers as a fresh IIR and finds a type
		///     hash of 0 ("Entry with hash 0 was not found ... datasize left in stream=8, raw=70").
		///     <para>
		///         Combat:Melee, :Ranged and :Unarmed share one tail family, but this list stays at the measured two: the
		///         corpus holds no padded Combat:Ranged (0x10) echo to justify adding it.
		///     </para>
		///     <para>
		///         A replaced tail (<c>tailOverride</c>) is a different shape and always pads: the equip/unequip acks build a
		///         bespoke identity-block tail whose captured form ends in those 12 zero bytes (BehaviorAckTests, behaviour
		///         0x09). The pad follows the tail's provenance - echoed or replaced - not the behaviour id alone.
		///     </para>
		/// </remarks>
		private static readonly uint[] PaddedTailBehaviors = [(uint)BehaviorId.CombatMelee, (uint)BehaviorId.CombatUnarmed];

		/// <summary>The shortest request this can read: the header through the args length, with empty args.</summary>
		private const int MinimumRequestLength = 26;

		/// <summary>
		///     The cast-interrupt abort. When the client cancels an in-flight cast (CastSpell state 2, sent when the player
		///     moves mid-cast, ground-target AoE included), retail answers with a single state 0x0007 record that drops the
		///     cast bar, cancels the animation and pops the client's "Interrupted" floating text - there is no separate
		///     feedback packet, the client generates that from this record.
		/// </summary>
		/// <remarks>
		///     Byte-exact against NecroL80_..._cast-interupt (idx988 begin + idx1006 cancel -> idx1013): the begin's args
		///     with the phase field spliced in after field 2, carried at wire seq 0x0007, with the cancel's tail (which holds
		///     the client's interrupt marker) and its trailing c2s direction byte cleared to 0x00. Only the args are
		///     begin-specific and are held on the session as PendingCastArgs; the behaviour id and tail come from the cancel
		///     packet in hand, and the behaviour id is the same either way.
		/// </remarks>
		public static BehaviorControlCenterIIR CastInterruptAbort(byte[] beginArgs, byte[] cancelPayload, uint instance, uint phase)
		{
			var cancel = ParseRequest(cancelPayload);

			var tail = cancel.Tail.ToArray(); // a fresh copy - safe to mutate
			if (tail.Length > 0)
				tail[^1] = 0x00; // s2c direction byte (the c2s cancel carried 0x01)

			return new BehaviorControlCenterIIR(instance, AbortSeq, cancel.BehaviorId, InsertBehaviorPhaseField(beginArgs, phase), tail);
		}

		/// <summary>
		///     The same abort for a cast the server opened rather than the client - today only Rest.
		/// </summary>
		/// <remarks>
		///     This needs its own entry point because <see cref="InsertBehaviorPhaseField" /> assumes the begin came from the
		///     client: a c2s cast's <paramref name="beginArgs" /> already carry field 2 (the client's command id), so the
		///     splice only has to add field 3. A server-built begin (BehaviorAnimations.RestCast) carries field 3 - its phase
		///     - and no field 2 at all, so splicing would overwrite the phase with the command id, giving
		///     <c>08c0e80a 18c1843d 2808...</c> where retail sends <c>08c0e80a 10c5843d 1808 2808...</c>; the client will not
		///     pair that with the begin and the cast bar stays up.
		///     <para>
		///         Here the begin's own phase is kept and the cancel's command id goes to field 2, which is retail's frame 927
		///         in NecroL80_..._cast-general-abilities (begin 867 seq 0x0004 -> cancel 921 seq 0x0002 -> this). Round-tripping
		///         through <see cref="CommandMessage" /> is safe for these args specifically because this side built them from
		///         one, so there is no captured byte layout to preserve.
		///     </para>
		/// </remarks>
		public static BehaviorControlCenterIIR ServerCastInterruptAbort(byte[] beginArgs, byte[] cancelPayload, uint instance, uint clientCommandId)
		{
			var cancel = ParseRequest(cancelPayload);

			var tail = cancel.Tail.ToArray();
			if (tail.Length > 0)
				tail[^1] = 0x00;

			var message = beginArgs.FromProtobuf<CommandMessage>();
			message.CommandqueueClientId = clientCommandId;

			return new BehaviorControlCenterIIR(instance, AbortSeq, cancel.BehaviorId, message.ToProtobuf(), tail);
		}

		/// <summary>
		///     The echo-ack: echoes the request with field 3 (the phase) spliced in, padding the tail only for the behaviours
		///     that take a pad. <paramref name="tailOverride" /> replaces the echoed tail wholesale.
		/// </summary>
		public static BehaviorControlCenterIIR EchoAck(byte[] rawPayload, uint instance, uint phase, byte[]? tailOverride = null)
		{
			return Ack(ParseRequest(rawPayload), instance, phase, tailOverride, null);
		}

		/// <summary>
		///     The channel-complete ack: the same echo emitted as form 0x000A instead of following the request's own seq
		///     progression. It closes the cast bar at the end of a server-timed channel - a spell cast, and both directions of
		///     the mount toggle.
		/// </summary>
		/// <remarks>
		///     The form is stated outright rather than reached by patching the request's carrier bytes. A record whose seq is
		///     one of the phases that ends at the seq word (BehaviorControlCenterCodec's CommandPhases) may carry no behaviour
		///     id, args or tail behind it, so a patched carrier yields a body no grammar accepts and the codec throws instead
		///     of sending an ack, leaving the cast bar at 100% for ever.
		/// </remarks>
		public static BehaviorControlCenterIIR ChannelCompleteAck(byte[] rawPayload, uint instance, uint phase)
		{
			return Ack(ParseRequest(rawPayload), instance, phase, null, ChannelCompleteSeq);
		}

		/// <summary>
		///     The shared body of the two above: echo the request's behaviour and args under <paramref name="instance" />,
		///     splice the phase in, and pad the tail for the behaviours that take a pad. A null <paramref name="seq" /> means
		///     the request's own progression (<see cref="AckSeq" />).
		/// </summary>
		private static BehaviorControlCenterIIR Ack(BehaviorControlCenterIIR request, uint instance, uint phase, byte[]? tailOverride, ushort? seq)
		{
			var tail = tailOverride ?? request.Tail;
			if (tailOverride != null || PaddedTailBehaviors.Contains(request.BehaviorId))
				tail = [..tail, ..new byte[12]];

			return new BehaviorControlCenterIIR(instance, seq ?? AckSeq(request.Seq), request.BehaviorId, InsertBehaviorPhaseField(request.Args, phase), tail);
		}

		/// <summary>
		///     The targeted-hit ack: the echo-ack's header and args, with the echoed tail replaced by the result block naming
		///     the victim and the damage. It is what makes the client play the hit-react on the entity the block names, so an
		///     air swing must use <see cref="EchoAck" /> instead - naming the player here makes them visibly bleed when
		///     swinging at nothing. An air swing is a real captured shape: 130 of the 3687 captured attack tails carry zero
		///     targets and zero results.
		/// </summary>
		/// <remarks>
		///     Two deliberate differences from <see cref="EchoAck" />, both matching the wire: the seq is
		///     <c>requestSeq + 1</c> rather than <see cref="AckSeq" /> (every captured normal-attack request carries 0x0003,
		///     so the two agree in traffic), and the behaviour id is Combat:Melee rather than the request's own.
		///     <para>
		///         <paramref name="direction" /> is the zone the player swung from and is not optional in retail: all 515
		///         captured player-actor swings state a direction in their result record, including the 107 that name no
		///         direction ability (a combo step carries its direction in the catalog, not on the wire). Without it every
		///         swing reads as "Overhead".
		///     </para>
		/// </remarks>
		public static BehaviorControlCenterIIR HitResultAck(byte[] rawPayload, uint instance, uint phase, uint victimHandle, uint damage, SwingDirection direction)
		{
			var request = ParseRequest(rawPayload);

			return new BehaviorControlCenterIIR(instance, (ushort)(request.Seq + 1), (uint)BehaviorId.CombatMelee, InsertBehaviorPhaseField(request.Args, phase),
				HitResultBlock(victimHandle, damage, SwingDirectionCode(direction)));
		}

		/// <summary>
		///     The result block that closes a targeted-hit ack - 81 bytes in the one-target, one-result form:
		///     <code>
		///     [u32 targetCount][targetCount x (u32 class, u32 handle)]   the entities the swing reached
		///     [8 x u32]                                                  see MiddleWords
		///     [u32 resultCount][resultCount x 41-byte HitResult]         one per entity actually affected
		///     </code>
		/// </summary>
		/// <remarks>
		///     Verified over the corpus: this accounts for all 3687 captured s2c attack tails (Combat:Melee / :Ranged /
		///     :Unarmed), with Combat:Ranged adding one trailing 0x00 - which is why its tails read one byte longer than
		///     Melee's.
		///     <para>
		///         The middle is eight words usually and nine sometimes, selected by its own second word: 1 gives eight (3659
		///         tails), 2 or 3 give nine (28). A mis-sized middle puts the result count one word out and the length then
		///         fits nothing.
		///     </para>
		///     <para>
		///         The two counts move together in most captures (one extra target produces one extra result), which makes the
		///         lengths look like a single 49-byte stride. 49 is 8 + 41, not one record; do not model it as one.
		///     </para>
		///     <para>
		///         <c>Victim</c> is the entity that plays the hit-react and blood, never the attacker's own handle.
		///     </para>
		///     <para>
		///         <c>ResultType</c> is the swing's direction, not an opaque result code. Cross-tabulated over every captured
		///         tail that also names a direction ability in middle word 5 it is a perfect function of it, with no
		///         exceptions: Abilities 769094 "Upper Left Attack" -> 0xC9 (159 of 159), 769095 "Upper Right Attack" -> 0xCA
		///         (87 of 87), 769099 "Overhead Attack" -> 0xCB (253 of 253). The corpus also carries 0xCC and 0xCD on swings
		///         that name no direction ability; the exe pins 0xCC to Abilities 769096 "Lower Left Attack" and 0xCD to
		///         769097 "Lower Right Attack", so the alphabet is the full five-zone rose and
		///         <see cref="SwingDirectionCode" /> writes all five. The 0xCB default therefore means Overhead, and covers
		///         only the undirected callers: every other sender passes the direction it actually drew.
		///     </para>
		/// </remarks>
		internal static byte[] HitResultBlock(uint victimHandle, uint damage, byte resultType = 0xCB, uint resultK1 = 1, uint resultK2 = 2, bool ranged = false)
		{
			return HitResultBlock([], [new HitResult(victimHandle, damage, resultType, resultK1, resultK2)], ranged);
		}

		/// <summary>
		///     The words between the target list and the result count, in the eight-word form we send. Decoded against the
		///     client's reader (FUN_0084d5f0 reads the target list then words 0-1, FUN_01151170 the rest), so each word is
		///     named for the Command slot it lands in and for what 3,313 captured tails put there:
		///     <code>
		///     0    Command+0x164  a float, 0.02..0.93 over 322 distinct values, zero on 2,899 - the swing's charge
		///     1    Command+0x144  the selector: 1 (x3285), 2 (x19), 3 (x9); above 1 inserts one extra word (-> +0x148)
		///     2                   a bitfield: bit 4 -> +0x1A0, bit 5 -> +0x1A1, bit 6 -> +0x198; the corpus sets no
		///                         other bit (0x00 x2692, 0x20 x602, 0x30 x18, 0x70 x1)
		///     3    Command+0x19C  zero on 3,312 of 3,313; one sighting of 0x6BE
		///     4    Command+0x44   0xC9/0xCA/0xCB on 292 tails - the same alphabet as the result type, but independent
		///                         of it and of word 5 (all nine combinations occur), and never on a lower-arc swing
		///     5    Command+0xD0   the swing's direction as an Abilities id: 769094 Upper Left, 769095 Upper Right,
		///                         769099 Overhead. Zero on an NPC's swing, which has no ability to name.
		///     6/7  Command+0x188 / +0x18C  one pair behind a presence test (vtable slot 0x9C) and a pair getter
		///                         (slot 0xA0); four sightings, (0x003C7D4B, 2)
		///     </code>
		/// </summary>
		/// <remarks>
		///     Words 3, 4, 6 and 7 are never read by the client. The class is AttackCommandMelee_c (RTTI off the vtable that
		///     holds the reader); across all 68 of its virtual slots and every register-relative access in its own and its
		///     base's address band those four are stored, echoed back onto the c2s wire and copied, but consumed by nothing.
		///     Slot 0xA0 says the same for the 6/7 pair: Command_c and 34 sibling commands return (0, 0) from it, only the
		///     five Attack commands return the field, and the combat module contains no call through that slot. They are
		///     server-side values the client merely couriers.
		///     <para>Word 1 must stay 1: it is the selector, and any other value changes the block's own length.</para>
		/// </remarks>
		private static readonly uint[] MiddleWords = [0, 1, 0, 0, 0, 0, 0, 0];

		/// <summary>
		///     Builds the block. Combat:Ranged closes it with one extra 0x00 - not a field, but the only difference between a
		///     ranged and a melee tail of the same shape: 82 bytes against 81 with one result, 41 against 40 with none.
		/// </summary>
		internal static byte[] HitResultBlock(IReadOnlyList<uint> targets, IReadOnlyList<HitResult> results, bool ranged = false)
		{
			ArgumentNullException.ThrowIfNull(targets);
			ArgumentNullException.ThrowIfNull(results);

			using var s = new FaolanStream();
			s.WriteUInt32(targets.Count);
			foreach (var target in targets)
				s.WriteUInt32(EntityClass.Character).WriteUInt32(target);

			foreach (var word in MiddleWords)
				s.WriteUInt32(word);

			s.WriteUInt32(results.Count);
			foreach (var r in results)
			{
				s
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(r.VictimHandle)
					.WriteUInt32(0)
					.WriteByte(0x06)
					.WriteUInt32(r.ResultType)
					.WriteUInt32(0)
					.WriteUInt32(r.K1)
					.WriteUInt32(r.Damage)
					.WriteUInt32(r.K2)
					.WriteUInt32(0)
					.WriteUInt32(0);
			}

			if (ranged)
				s.WriteByte(0x00);

			return s.ToArray();
		}

		/// <summary>
		///     The wire code for the zone a swing came in on: <see cref="SwingDirection" /> rebased from 0 to 0xC9. That is
		///     the client's own arithmetic - FUN_005e3b30 is the same switch over the same order - and the first three members
		///     are pinned to their Abilities rows by the corpus. The evidence for the enum's order lives on
		///     <see cref="SwingDirection" />.
		/// </summary>
		public static byte SwingDirectionCode(SwingDirection direction)
		{
			return Enum.IsDefined(direction)
				? (byte)(FirstDirectionCode + (int)direction)
				: throw new ArgumentOutOfRangeException(nameof(direction), direction, "unknown swing direction");
		}

		private const byte FirstDirectionCode = 0xC9;

		/// <summary>One 41-byte result record: who was hit, for how much, and the still-undecoded result codes.</summary>
		internal readonly record struct HitResult(uint VictimHandle, uint Damage, byte ResultType = 0xCB, uint K1 = 1, uint K2 = 2);

		/// <summary>
		///     The equip/unequip (Interact 0x09) ack: the echo-ack with the tail's zero padding replaced by an identity block
		///     - the first 28 tail bytes echoed, then the moved item's Key1..4 quad twice plus the null filler twice (equip),
		///     or the null filler six times (unequip). Byte-exact against the capture; see BehaviorAckTests.
		/// </summary>
		public static BehaviorControlCenterIIR EquipMoveAck(byte[] rawPayload, uint instance, uint phase, Item item)
		{
			var requestTail = ParseRequest(rawPayload).Tail;

			using var tail = new FaolanStream();
			tail.WriteArray(requestTail[..Math.Min(28, requestTail.Length)]);
			if (item != null)
			{
				for (var i = 0; i < 2; i++)
					tail.WriteUInt32(item.Key.Key1).WriteUInt32(item.Key.Key2).WriteUInt32(item.Key.Key3).WriteUInt32(item.Key.Key4);

				tail.WriteArray(NullIdentity.Bytes).WriteArray(NullIdentity.Bytes);
			}
			else
			{
				for (var i = 0; i < 6; i++)
					tail.WriteArray(NullIdentity.Bytes);
			}

			return EchoAck(rawPayload, instance, phase, tail.ToArray());
		}

		/// <summary>
		///     Sets field 3 (the behaviour phase) on the request args, matching retail's ack, which returns the request args
		///     with the phase added.
		/// </summary>
		/// <remarks>
		///     The args are a <see cref="CommandMessage" />, so this is a deserialize / set / re-serialize rather than a byte
		///     splice: protobuf-net emits fields in ascending number order, so the phase lands where retail puts it for every
		///     input. The captured swing-1 request/ack pair pins it; see BehaviorPhaseFieldTests. CommandMessage is
		///     IExtensible, so any field the schema does not model survives the round-trip.
		/// </remarks>
		public static byte[] InsertBehaviorPhaseField(byte[] args, uint phase)
		{
			var message = args.FromProtobuf<CommandMessage>();
			message.CommandqueueServerId = phase;
			return message.ToProtobuf();
		}

		/// <summary>
		///     Decoupled relay: acks the raw behaviour request for <paramref name="instance" />, with the request's own mode
		///     as the phase (or 1). Returns null when the payload is too short to be a request at all - the guard lives here
		///     because the relaying call sites do not length-check the packet themselves.
		/// </summary>
		public static BehaviorControlCenterIIR? Relay(uint instance, byte[] rawPayload, uint mode)
		{
			if (rawPayload.Length < MinimumRequestLength)
				return null;

			return EchoAck(rawPayload, instance, mode != 0
				? mode
				: 1u);
		}

		/// <summary>The wire seq of the cast-interrupt abort.</summary>
		private const ushort AbortSeq = 0x0007;

		/// <summary>
		///     The wire seq of the channel-complete ack - retail's completion BCC (ice-strike idx481/534), the record that
		///     closes the cast bar. Not the form-4 begin, and not derived from the request's seq; see
		///     <see cref="ChannelCompleteAck" />.
		/// </summary>
		private const ushort ChannelCompleteSeq = 0x000A;

		/// <summary>
		///     The ack's seq for a given request seq.
		/// </summary>
		/// <remarks>
		///     Across every login capture the client only ever sends seq 0x0003 or 0x0002. Retail answers 0x0003 with 0x0004
		///     (739 cases), and 0x0002 with 0x000F / 0x0007 / 0x0004 / 0x0005 / 0x000A depending on context - never with
		///     0x0003. 0x0004 accounts for 49158 of all s2c BCC seqs while 0x0003 appears three times in the whole corpus, so
		///     a plain <c>seq + 1</c> on a 0x0002 request emits a value the client does not know and logs as
		///     "Control.IIR - Unknown Action Command: 3" (a warning, not a rejection - the record parses, the client just
		///     cannot act on it).
		///     <para>
		///         Which 0x0002 reply is right per context is not settled: pairing a request with the next s2c record for the
		///         same actor and behaviour gives a mix for every behaviour (0x16 is mostly 0x000F but also
		///         0x0005/0x0007/0x0004), which is too loose to derive a rule from. 0x0004 is the safe default - the dominant
		///         ack, a legitimate reply to a 0x0002 request in the captures, and what the proven 0x0003 case produces.
		///     </para>
		///     <para>
		///         The bench-sit records do not come through here: BehaviorAnimations.BenchSitState states its seqs outright.
		///     </para>
		/// </remarks>
		private static ushort AckSeq(ushort requestSeq)
		{
			// Retail's sit capture states the rule for both two-phase behaviours - the sit's 0x12 and the standup's
			// 0x01 - as a matched pair each time:
			//   c2s seq 0x0003 -> s2c 0x0004      (pkt 6805 -> 6807, 6829 -> 6837)
			//   c2s seq 0x0002 -> s2c 0x000F      (pkt 6811 -> 6813, 6830 -> 6838)
			// 0x0002 is the client confirming and 0x000F the commit, not seq + 1. The corpus-wide counts agree: 0x0002
			// requests are answered with 0x000F far more than anything else (70 of 118).
			return requestSeq switch
			{
				0x0002 => 0x000F,
				_      => (ushort)(requestSeq + 1)
			};
		}

		/// <summary>
		///     Parses a c2s behaviour request as it arrives on the listener: the carrier payload, i.e. [u32 bodyLen] followed
		///     by the record itself. Stripping the length and handing the rest to the codec keeps the ack path on the one
		///     reader for this message instead of re-deriving its field offsets.
		/// </summary>
		private static BehaviorControlCenterIIR ParseRequest(byte[] rawPayload)
		{
			return BehaviorControlCenterCodec.Parse(rawPayload.AsSpan(sizeof(uint)));
		}
	}
}
