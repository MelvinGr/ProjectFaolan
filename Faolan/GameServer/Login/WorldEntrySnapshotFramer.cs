using System;
using System.Buffers.Binary;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Builds the world-entry 0x737A6DF9 FullChar snapshot PACKET from scratch - RpcHeader + IIR record framing + the
	// 8-byte blob prefix - with the root + tails supplied by the caller (each independently proven generatable:
	// FullCharSnapshotBuilder.BuildRoot / BeachTailsBuilder.BuildFromScratch). This removes the snapshot's last
	// dependency on a captured burst packet as a template (#210): the live path (BuildBeachInventoryPacket) still
	// copies the captured packet's header and parses its prefix; this assembles both from the live session.
	//
	// Byte-identical to the captured beach snapshot when given that capture's root + tails + player instance
	// (WorldEntrySnapshotFramerTests). Framing reuses the shared world-entry conversation (WorldEntryGeneratedRecords):
	// sender = world-system GameAgent/0x58, receiver = the player's GameInterface endpoint.
	public partial class GameServerListener
	{
		// The snapshot record's TypeTag - the 0x0000C350 constant every 0x737A6DF9 carrier carries (= the IIR
		// conversation tag, shared with the 0x201C handshake bodies).
		private const uint SnapshotTypeTag = 0x0000C350;

		// The trailing bytes of the 8-byte blob prefix after [flag][BE32 blobLen]: the captured beach field5, constant
		// for the world-entry snapshot (the live splice path preserves these same bytes via SetSnapshotBlobLength).
		private static readonly byte[] SnapshotPrefixField5 = [0x28, 0x81, 0x06];

		// The 8-byte IIR blob prefix the client (n3InfoItemRemote::Construct) reads before the FullCharacterIIR root:
		// [u8 flag=0][u32 BE blobLen][field5]. blobLen = field5.Length + rootLength (the client reads EXACTLY blobLen
		// bytes as the protobuf blob = field5 + root, then expects the 0x18 tails version). Identical BE32 model to
		// SetSnapshotBlobLength, but built from scratch rather than patched onto a captured prefix.
		internal static byte[] BuildSnapshotPrefix(int rootLength)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(rootLength);

			var prefix = new byte[5 + SnapshotPrefixField5.Length];
			prefix[0] = 0x00; // flag
			BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1, 4), (uint)(SnapshotPrefixField5.Length + rootLength));
			SnapshotPrefixField5.CopyTo(prefix, 5);
			return prefix;
		}

		// Assembles the complete world-entry snapshot packet for `client` from a pre-built root + tails. The record
		// body is [IirType=0x737A6DF9][TypeTag=0xC350][Handle=instance][prefix][root][tails]; the RpcHeader + outer
		// length/CRC come from the proven WriteHeader/WriteLengthHash path. No captured packet is consulted.
		internal static byte[] BuildWorldEntrySnapshot(INetworkClient client, byte[] rootBytes, byte[] tailsBytes)
		{
			ArgumentNullException.ThrowIfNull(rootBytes);
			ArgumentNullException.ThrowIfNull(tailsBytes);

			var instance = client.Session.Account.ClientInstance;
			var prefix = BuildSnapshotPrefix(rootBytes.Length);

			var ps = new PacketStream();
			ps
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(IirMessageTypes.FullChar);
					s.WriteUInt32(SnapshotTypeTag);
					s.WriteUInt32(instance);
					s.WriteArray(prefix);
					s.WriteArray(rootBytes);
					s.WriteArray(tailsBytes);
				});
			return ps
				.WriteLengthHash()
				.ToArray();
		}
	}
}
