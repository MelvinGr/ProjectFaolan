using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The ECHO-ACK forms of `BehaviorControlCenterIIR`, as VALUE factories - retail acks a client behaviour request by
	// echoing it back with field3 (the phase) spliced in and a 12-byte pad (or an equip identity block).
	//
	// Not a codec: the whole family is "take the client's own record and send it back changed", so the input is a
	// PARSED request rather than fields, and `BehaviorControlCenterCodec` is both the reader and the writer. That is
	// also what removed the four const offsets this file used to carry (16/18/22/26): the request is a
	// BehaviorControlCenterIIR like any other, so it is parsed, not indexed.
	//
	// Byte-exact in BehaviorAckTests / BehaviorPhaseFieldTests. CombatService keeps the FSM and the send.
	public static class BehaviorAcks
	{
		// The behaviours whose ECHOED tail takes 12 zero bytes of padding. This is NOT universal, which is what the code
		// used to assume: measured across every login capture, pairing each c2s request with its s2c ack and diffing the
		// tail lengths, only the two attack behaviours pad an echoed tail -
		//   0x0F NormalAttack: +12 in 85 acks   .   0x11 UnarmedAttack: +12 in 3
		// while every other behaviour echoes the tail UNCHANGED: +0 across 25 acks for 0x01, 87 for 0x04, 174 for 0x16
		// (talk), 66 for 0x12 (the bench-sit hold state), 15 for 0x2A (the sit animation), 13 for 0x09/0x0A, and so on.
		// (0x0F also shows much larger deltas - 53, 94, ... - but those are the separate result-block ack variant for a
		// targeted hit, not this echo path.)
		//
		// Padding unconditionally made every non-attack ack 8-12 bytes too long, and the client REJECTS the record
		// outright: it parses to the end of what it expects, then reads the leftovers as a fresh IIR and finds a type
		// hash of 0 - "Entry with hash 0 was not found ... datasize left in stream=8, raw=70". Standing up off a bench
		// was the case that surfaced it (behaviourId 1, 32-byte tail where retail sends 20), which only became reachable
		// once sitting itself started working.
		//
		// A REPLACED tail (tailOverride) is a different shape and always pads: the equip/unequip acks build a bespoke
		// identity-block tail whose captured form ends in those 12 zero bytes (BehaviorAckTests, behaviour 0x09). So the
		// pad follows the tail's PROVENANCE - echoed or replaced - not the behaviour id alone.
		private static readonly uint[] PaddedTailBehaviors = [0x0F, 0x11];

		// The shortest request this can read: the header through the args length, with empty args.
		private const int MinimumRequestLength = 26;

		// The cast-INTERRUPT abort (#347). When the client cancels an in-flight cast (CastSpell state=2, sent when the
		// player moves mid-cast - the ground-target AoE included), retail answers with a single state=0x0007 record that
		// drops the cast bar, cancels the animation and pops the client's "Interrupted" floating text (no separate
		// feedback packet - it is client-generated from this). Byte-exact against NecroL80_..._cast-interupt (idx988
		// begin + idx1006 cancel -> idx1013): the begin's ARGS with the phase=Mode field spliced in after field2,
		// carried at wire seq 0x0007, with the CANCEL's TAIL (which carries the client's interrupt marker) and its
		// trailing c2s direction byte cleared to 0x00. Only the args are begin-specific (held on the session as
		// PendingCastArgs); the behaviorId + tail come from the cancel packet in hand (behaviorId is identical either
		// way - it's the same cast behaviour).
		public static BehaviorControlCenterIIR CastInterruptAbort(byte[] beginArgs, byte[] cancelPayload, uint instance, uint phase)
		{
			var cancel = ParseRequest(cancelPayload);

			var tail = cancel.Tail.ToArray(); // a fresh copy - safe to mutate
			if (tail.Length > 0)
				tail[^1] = 0x00; // s2c direction byte (the c2s cancel carried 0x01)

			return new BehaviorControlCenterIIR(instance, AbortSeq, cancel.BehaviorId, InsertBehaviorPhaseField(beginArgs, phase), tail);
		}

		// The echo-ack: echoes the request with field3 (phase) spliced in, and pads the tail only for the behaviours
		// that take a pad (see above). `tailOverride` replaces the echoed tail wholesale.
		public static BehaviorControlCenterIIR EchoAck(byte[] rawPayload, uint instance, uint phase, byte[]? tailOverride = null)
		{
			var request = ParseRequest(rawPayload);
			var tail = tailOverride ?? request.Tail;
			if (tailOverride != null || PaddedTailBehaviors.Contains(request.BehaviorId))
				tail = [..tail, ..new byte[12]];

			return new BehaviorControlCenterIIR(instance, AckSeq(request.Seq), request.BehaviorId, InsertBehaviorPhaseField(request.Args, phase), tail);
		}

		// The TARGETED-HIT ack (#19/#228): the echo-ack's header and args, with the echoed tail REPLACED by the
		// 132-byte result block naming the victim and the damage. It is what makes the client play the hit-react on the
		// entity the block names, which is why an air swing must get `EchoAck` instead - naming the player there made
		// them visibly bleed when swinging at nothing.
		//
		// ⚠️ Two deliberate differences from `EchoAck`, both preserving the bytes the server has always sent:
		//   * the seq is `requestSeq + 1`, not `AckSeq(requestSeq)`. Every captured normal-attack request carries
		//     0x0003, so the two agree in traffic; changing it here would be a wire change, not a refactor.
		//   * the behaviourId is the NormalAttack constant rather than the request's own.
		// The result block is the SAME 132 bytes `BehaviorAnimations.NpcAttackAck` sends the other way round - it was
		// written out twice, once here and once there, which is the duplicate-layout pattern #530 keeps finding.
		public static BehaviorControlCenterIIR HitResultAck(byte[] rawPayload, uint instance, uint phase, uint victimHandle, uint damage)
		{
			var request = ParseRequest(rawPayload);

			return new BehaviorControlCenterIIR(instance, (ushort)(request.Seq + 1), NormalAttackBehaviorId, InsertBehaviorPhaseField(request.Args, phase),
				HitResultBlock(victimHandle, damage));
		}

		// The single-entry result block that closes a targeted-hit ack:
		//   [0][0][count=1] [6 x u32 entry = 0] [1][class][victim][0] [u8 0x06][resultType][0][k1][damage][k2][0][0]
		// `victim` is the entity that plays the hit-react/blood - it was once the ATTACKER's own handle on the player
		// path, which is #228 ("attack hits myself"). The result codes are capture-derived and not semantically
		// decoded; 0xCB is the value the captured BccAck result block carries (it was once written as 0xCD).
		internal static byte[] HitResultBlock(uint victimHandle, uint damage, byte resultType = 0xCB, uint resultK1 = 1, uint resultK2 = 2)
		{
			using var s = new FaolanStream();
			s
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(1) // [0][0][count=1]
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0)
				.WriteUInt32(0) // 6 x u32 entry (zero in the single-hit case)
				.WriteUInt32(1)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(victimHandle)
				.WriteUInt32(0)
				.WriteByte(0x06)
				.WriteUInt32(resultType)
				.WriteUInt32(0)
				.WriteUInt32(resultK1)
				.WriteUInt32(damage)
				.WriteUInt32(resultK2)
				.WriteUInt32(0)
				.WriteUInt32(0);

			return s.ToArray();
		}

		// The NormalAttack behaviorId an attack ack carries, whatever the request said.
		internal const uint NormalAttackBehaviorId = 0x0000000F;

		// The equip/unequip (Interact 0x09) ack: the echo-ack with the tail's zero padding replaced by an identity
		// block - the first 28 tail bytes echoed, then the moved item's Data1..4 key quad twice + the null filler
		// twice (equip), or the null filler six times (unequip). Byte-exact against the capture (BehaviorAckTests).
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

		// Sets field 3 (the behaviour "phase") on the request args, matching retail's ack, which returns the request
		// args with the phase added.
		//
		// The args are a CommandMessage, so this is a deserialize / set / re-serialize. It used to be a byte SPLICE:
		// hand-encode `18 <varint>` and memcpy it in right after field 2, located by OffsetAfterField - a hand-written
		// partial protobuf parser that walked tags and varints and gave up on wiretypes 1 and 5 (`return msg.Length;
		// // unsupported wiretype`). protobuf-net emits fields in ascending number order, so the phase lands exactly
		// where the splice put it, for every input, with no parser to maintain (Gitea #386). The captured
		// swing-1 request/ack pair pins it - see BehaviorPhaseFieldTests.
		//
		// CommandMessage is IExtensible, so any field the schema does not model survives the round-trip.
		public static byte[] InsertBehaviorPhaseField(byte[] args, uint phase)
		{
			var message = args.FromProtobuf<CommandMessage>();
			message.CommandqueueServerId = phase;
			return message.ToProtobuf();
		}

		// Decoupled relay: acks the raw behaviour request for `instance`. Null when the payload is too short to be a
		// request at all - the caller's own guard, kept here because two of the three call sites relay a packet they
		// have not length-checked themselves. The phase is the request's own mode (or 1).
		public static BehaviorControlCenterIIR? Relay(uint instance, byte[] rawPayload, uint mode)
		{
			if (rawPayload.Length < MinimumRequestLength)
				return null;

			return EchoAck(rawPayload, instance, mode != 0
				? mode
				: 1u);
		}

		// The wire seq of the cast-interrupt abort.
		private const ushort AbortSeq = 0x0007;

		// The ack's seq. This was `seq + 1`, which is right for the common case and wrong for the other one.
		//
		// Across every login capture the client only ever SENDS seq 0x0003 or 0x0002, and retail's ack is 0x0004 in 739
		// of the 0x0003 cases (= +1, so that half was fine). For 0x0002 it replies 0x000F / 0x0007 / 0x0004 / 0x0005 /
		// 0x000A depending on context - and NEVER 0x0003. Meanwhile 0x0004 accounts for 49158 of all s2c BCC seqs, and
		// 0x0003 appears 3 times in the entire corpus. So `seq + 1` on a 0x0002 request emitted a value the client does
		// not know, and it said so: "Control.IIR - Unknown Action Command: 3" (a warning, not a rejection - the record
		// parses, the client just cannot act on it, which left standing up half-working).
		//
		// What the RIGHT 0x0002 reply is per context is NOT settled: pairing a request with the next s2c record for the
		// same actor+behaviour gives a mix for every behaviour (0x16 -> mostly 0x000F but also 0x0005/0x0007/0x0004), so
		// that pairing is too loose to derive a rule and I am not inventing one. 0x0004 is the safe default: it is the
		// overwhelmingly dominant ack, it IS a legitimate reply to a 0x0002 request in the captures, and it is what the
		// proven 0x0003 -> 0x0004 case produces anyway.
		//
		// The bench-sit records do not come through here - BehaviorAnimations.BenchSitState builds SeqBegin /
		// SeqObjectHold explicitly.
		private static ushort AckSeq(ushort requestSeq)
		{
			// Retail's sit capture shows the rule cleanly for BOTH two-phase behaviours - the sit's 0x12 and the
			// standup's 0x01 - as a matched pair each time:
			//   c2s seq 0x0003 -> s2c 0x0004      (pkt 6805 -> 6807, 6829 -> 6837)
			//   c2s seq 0x0002 -> s2c 0x000F      (pkt 6811 -> 6813, 6830 -> 6838)
			// so 0x0002 is the client CONFIRMING and 0x000F is the commit, not seq+1. That also matches the corpus-wide
			// counts, where 0x0002 requests are answered with 0x000F far more than anything else (70 of 118).
			return requestSeq switch
			{
				0x0002 => 0x000F,
				_      => (ushort)(requestSeq + 1)
			};
		}

		// A c2s behaviour request as it arrives on the listener: the CARRIER payload, i.e. [u32 bodyLen] then the record
		// itself. Stripping the length and handing the rest to the codec is what makes the ack path share the server's
		// one reader for this message rather than re-deriving its field offsets.
		private static BehaviorControlCenterIIR ParseRequest(byte[] rawPayload)
		{
			return BehaviorControlCenterCodec.Parse(rawPayload.AsSpan(sizeof(uint)));
		}
	}
}
