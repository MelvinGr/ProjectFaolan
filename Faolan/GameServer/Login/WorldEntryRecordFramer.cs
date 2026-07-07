using System;
using System.Buffers.Binary;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Frames world-entry IIR record PACKETS from scratch - no captured packet as a template (#210). Every real
	// world-entry record (the 0x737A6DF9 FullChar snapshot, the player + NPC FA015807 spawns, the world objects)
	// shares ONE RpcHeader - sender = world-system GameAgent/0x58, receiver = the player's GameInterface endpoint,
	// opcode = the IIR carrier (0x2000) - verified across all four pruned-map bursts. So any record body the server
	// builds from scratch can be put on the wire by wrapping it in that shared header + outer length/CRC; only the
	// lone IirType=0x00000000 control record uses a different endpoint pair and is excluded.
	//
	// The body construction lives elsewhere (FullCharSnapshotBuilder / FullCharSnapshotSubClass for the snapshot, the
	// SpawnPacketBuilder override engine for spawns); these framers just assemble the wire packet. Each is proven
	// byte-identical to its capture (WorldEntryRecordFramerTests).
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
		// length/CRC come from the proven framing path. No captured packet is consulted.
		internal static byte[] BuildWorldEntrySnapshot(INetworkClient client, byte[] rootBytes, byte[] tailsBytes)
		{
			ArgumentNullException.ThrowIfNull(rootBytes);
			ArgumentNullException.ThrowIfNull(tailsBytes);

			var instance = client.Session.Account.ClientInstance;
			var prefix = BuildSnapshotPrefix(rootBytes.Length);

			using var body = new FaolanStream();
			body.WriteArrayUInt32Length(s =>
			{
				s.WriteUInt32(IirMessageTypes.FullChar);
				s.WriteUInt32(SnapshotTypeTag);
				s.WriteUInt32(instance);
				s.WriteArray(prefix);
				s.WriteArray(rootBytes);
				s.WriteArray(tailsBytes);
			});
			return FrameWorldEntryRecord(client, body.ToArray());
		}

		// Assembles the complete world-entry player/NPC FA015807 spawn packet for `client` from a pre-built spawn body.
		// `spawnBody` is the `packet[bodyStart..]` form ([u32 bodyLen][IirType=0xFA015807][TypeTag][Handle][FieldA]
		// [transform + CharacterIIR + scaffold]) that the SpawnPacketBuilder override engine produces; this wraps it in
		// the shared world-entry header so the spawn no longer needs the captured packet's header (the burst-less path).
		internal static byte[] BuildWorldEntrySpawn(INetworkClient client, byte[] spawnBody)
		{
			ArgumentNullException.ThrowIfNull(spawnBody);
			return FrameWorldEntryRecord(client, spawnBody);
		}

		// Prepends the shared world-entry RpcHeader + outer length/CRC (the WriteHeader/WriteLengthHash path) to a
		// record body that ALREADY carries its own [u32 bodyLen] prefix - i.e. the `packet[bodyStart..]` form that
		// IirRecordCodec, SpawnPacketBuilder and WriteArrayUInt32Length all produce. This is the keystone that lets a
		// from-scratch record body reach the wire without a captured packet header.
		internal static byte[] FrameWorldEntryRecord(INetworkClient client, params ReadOnlySpan<byte> bodyWithLenPrefix)
		{
			var ps = new PacketStream();
			ps.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArray(bodyWithLenPrefix);
			return ps.WriteLengthHash().ToArray();
		}
	}
}
