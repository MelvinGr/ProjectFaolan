using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The BehaviorControlCenterIIR (BccAck) echo-ack bodies, extracted from CombatService so they build without a
	// live client: retail acks a client behaviour request by echoing it back with field3 (the phase) spliced in and
	// a 12-byte pad (or an equip identity block). Decoded from the armed/unarmed combat + sell captures; byte-exact
	// in BehaviorAckTests / BehaviorPhaseFieldTests. CombatService keeps the FSM/send; this is the pure wire body.
	public static class BehaviorAckBody
	{
		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR;

		// The 8-byte constant category/owner pair (== the 0x2052 grant's ConstantA/B halves) used as the equip-ack's
		// null identity filler.
		internal static readonly byte[] NullIdentityFiller = [0xB2, 0x90, 0x80, 0x5C, 0x29, 0xE6, 0x27, 0xCA];

		// The echo-ack for air swings: echoes the request with field3 (phase) spliced in + 12 zero bytes (or the tail
		// override / bare tail for the vendor trade-open ack, padTail=false).
		public static byte[] BuildBehaviorEchoAckBody(byte[] rawPayload, uint instance, uint phase, byte[] tailOverride = null, bool padTail = true)
		{
			// rawPayload = [bodyLen u32][subop u32][c350 u32][instance u32][seq u16][behaviorId u32][argsLen u32][args][tail]
			var seq = (ushort)((rawPayload[16] << 8) | rawPayload[17]);
			var behaviorId = BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(18, 4));
			var argsLen = (int)BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(22, 4));
			var echoedArgs = InsertBehaviorPhaseField(rawPayload[26..(26 + argsLen)], phase);
			var tail = tailOverride ?? rawPayload[(26 + argsLen)..];

			using var s = new FaolanStream();
			s
				.WriteUInt32(GameServer0X2000Opcodes.BehaviorControlCenterIIR)
				.WriteUInt32(0x0000C350u)
				.WriteUInt32(instance)
				.WriteUInt16(seq + 1)
				.WriteUInt32(behaviorId)
				.WriteUInt32(echoedArgs.Length)
				.WriteArray(echoedArgs)
				.WriteArray(tail);
			if (padTail)
				s.WriteArray(new byte[12]);
			return s.ToArray();
		}

		// The cast-INTERRUPT abort BccAck (#347). When the client cancels an in-flight cast (CastSpell state=2, sent
		// when the player moves mid-cast - the ground-target AoE included), retail answers with a single state=0x0007
		// BCC that drops the cast bar, cancels the animation and pops the client's "Interrupted" floating text (no
		// separate feedback packet - it is client-generated from this). Byte-exact against NecroL80_..._cast-interupt
		// (idx988 begin + idx1006 cancel -> idx1013): the begin's ARGS with the phase=Mode field spliced in after
		// field2, carried at wire state 0x0007, with the CANCEL's TAIL (which carries the client's interrupt marker)
		// and its trailing c2s direction byte cleared to 0x00. Only the args are begin-specific (held on the session
		// as PendingCastArgs); the behaviorId + tail come from the cancel packet in hand (behaviorId is identical
		// either way - it's the same cast behavior).
		public static byte[] BuildCastInterruptAbort(byte[] beginArgs, byte[] cancelPayload, uint instance, uint phase)
		{
			var echoedArgs = InsertBehaviorPhaseField(beginArgs, phase);

			var behaviorId = BinaryPrimitives.ReadUInt32BigEndian(cancelPayload.AsSpan(18, 4));
			var cancelArgsLen = (int)BinaryPrimitives.ReadUInt32BigEndian(cancelPayload.AsSpan(22, 4));
			var tail = cancelPayload[(26 + cancelArgsLen)..]; // the range copy is fresh - safe to mutate
			if (tail.Length > 0)
				tail[^1] = 0x00; // s2c direction byte (the c2s cancel carried 0x01)

			using var s = new FaolanStream();
			s
				.WriteUInt32(GameServer0X2000Opcodes.BehaviorControlCenterIIR)
				.WriteUInt32(0x0000C350u)
				.WriteUInt32(instance)
				.WriteUInt16(0x0007)
				.WriteUInt32(behaviorId)
				.WriteUInt32(echoedArgs.Length)
				.WriteArray(echoedArgs)
				.WriteArray(tail);
			return s.ToArray();
		}

		// Decoupled relay: acks the raw behaviour request for `instance` (was BehaviorAckBody.BuildBehaviorRelayBody
		// taking the live client). Null when the payload is too short. The phase is the request's own mode (or 1).
		public static byte[] BuildBehaviorRelayBody(uint instance, byte[] rawPayload, uint mode)
		{
			if (rawPayload.Length < 26)
				return null;

			return BuildBehaviorEchoAckBody(rawPayload, instance, mode != 0
				? mode
				: 1u);
		}

		// The equip/unequip (Interact 0x09) ack: the echo-ack with the tail's zero padding replaced by an identity
		// block - the first 28 tail bytes echoed, then the moved item's Data1..4 key quad twice + the null filler
		// twice (equip), or the null filler six times (unequip). Byte-exact against the capture (BehaviorAckTests).
		public static byte[] BuildEquipMoveAckBody(byte[] rawPayload, uint instance, uint phase, Item item)
		{
			var argsLen = (int)BinaryPrimitives.ReadUInt32BigEndian(rawPayload.AsSpan(22, 4));
			var reqTail = rawPayload[(26 + argsLen)..];

			using var tail = new FaolanStream();
			tail.WriteArray(reqTail[..Math.Min(28, reqTail.Length)]);
			if (item != null)
			{
				for (var i = 0; i < 2; i++)
					tail.WriteUInt32(item.Key.Key1).WriteUInt32(item.Key.Key2).WriteUInt32(item.Key.Key3).WriteUInt32(item.Key.Key4);

				tail.WriteArray(NullIdentityFiller).WriteArray(NullIdentityFiller);
			}
			else
			{
				for (var i = 0; i < 6; i++)
					tail.WriteArray(NullIdentityFiller);
			}

			return BuildBehaviorEchoAckBody(rawPayload, instance, phase, tail.ToArray());
		}

		// Sets field 3 (the behaviour "phase") on the request args, matching retail's BccAck echo, which returns the
		// request args with the phase added.
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
			var message = ((ReadOnlySpan<byte>)args).FromProtobuf<CommandMessage>();
			message.CommandqueueServerId = phase;
			return message.ToProtobuf();
		}
	}
}
