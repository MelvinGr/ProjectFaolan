using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The BehaviorControlCenterIIR (BccAck) echo-ack bodies, extracted from CombatService so they build without a
	// live client: retail acks a client behaviour request by echoing it back with field3 (the phase) spliced in and
	// a 12-byte pad (or an equip identity block). Decoded from the armed/unarmed combat + sell captures; byte-exact
	// in BehaviorAckTests / BehaviorPhaseFieldTests. CombatService keeps the FSM/send; this is the pure wire body.
	public static class BehaviorAckBody
	{
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
					tail.WriteUInt32(item.Data1).WriteUInt32(item.Data2).WriteUInt32(item.Data3).WriteUInt32(item.Data4);

				tail.WriteArray(NullIdentityFiller).WriteArray(NullIdentityFiller);
			}
			else
			{
				for (var i = 0; i < 6; i++)
					tail.WriteArray(NullIdentityFiller);
			}

			return BuildBehaviorEchoAckBody(rawPayload, instance, phase, tail.ToArray());
		}

		// Splices field3 (the behaviour "phase", tag 0x18 varint) into the request args right after field2, matching
		// retail's BccAck echo. If field2 is absent/malformed the phase is appended at the end.
		public static byte[] InsertBehaviorPhaseField(byte[] args, uint phase)
		{
			var phaseField = EncodeBehaviorPhase(phase);
			var insertAt = Math.Min(OffsetAfterField(args, 2), args.Length);
			var result = new byte[args.Length + phaseField.Length];
			Array.Copy(args, 0, result, 0, insertAt);
			phaseField.CopyTo(result, insertAt);
			Array.Copy(args, insertAt, result, insertAt + phaseField.Length, args.Length - insertAt);
			return result;
		}

		// field3 as protobuf: tag 0x18 (field 3, varint) + the base-128 varint of `value`.
		private static byte[] EncodeBehaviorPhase(uint value)
		{
			return new FaolanStream().WriteByte(0x18).WriteVarint(value).ToArray();
		}

		// Byte offset just after `fieldNumber`'s value in a protobuf message (the message end if absent/malformed).
		private static int OffsetAfterField(byte[] msg, int fieldNumber)
		{
			var i = 0;
			while (i < msg.Length)
			{
				var tag = msg[i++];
				var fn = tag >> 3;
				var wt = tag & 7;
				if (wt == 0)
				{
					while (i < msg.Length && (msg[i] & 0x80) != 0)
						i++;
					i++;
				}
				else if (wt == 2)
				{
					int len = 0, shift = 0;
					while (i < msg.Length)
					{
						var b = msg[i++];
						len |= (b & 0x7f) << shift;
						if ((b & 0x80) == 0)
							break;

						shift += 7;
					}

					i += len;
				}
				else
					return msg.Length; // unsupported wiretype -> append at end

				if (fn == fieldNumber)
					return i;
			}

			return msg.Length;
		}
	}
}
