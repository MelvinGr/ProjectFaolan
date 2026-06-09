using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Replays the captured conversation node matching the player's TalkToNpc args, re-addressed to the NPC
		// they actually clicked and to the live character instance. Returns true if a node matched (and was sent).
		// Runs for both world-burst (beach login) AND create-world sessions: the client picks (Mode, Field3) from
		// the NPC it clicked, so the DB lookup routes to that NPC's dialogue - far better than the single hardcoded
		// intro the create-world fallback sends for every NPC. The nodes live in the DB (ConversationNode/
		// ConversationPacket), imported from captures by Faolan.Importer.
		private async Task<bool> TryReplayConversation(INetworkClient client, uint typeId, uint mode, uint field3, uint clickedNpcHandle)
		{
			if (!client.Session.UsedWorldBurstReplay && !client.Session.IsCreateWorldSession)
				return false;

			// Route by the clicked NPC's stable NAME when we know it (resolved from the world-burst spawns this
			// session). NPC dynel handles are session-volatile - the burst we replay and the captured conversation
			// came from different sessions - so a handle/args-only lookup serves the WRONG NPC (Kalanthes's dialog
			// for Casilda). The name pins the right NPC; (Mode, Field3) still select the branch.
			ConversationNode node;
			if (client.Session.SpawnedNpcNamesByHandle.TryGetValue(clickedNpcHandle, out var npcName))
				node = await Database.GetConversationNodeByName(npcName, typeId, mode, field3);
			else
				// No name on file (e.g. an NPC not spawned via the world-burst replay): fall back to the handle/args
				// lookup. Create-world requires an exact per-NPC handle match (the scripted intro is the better
				// fallback there); login allows the args-only fallback.
				node = await Database.GetConversationNode(clickedNpcHandle, typeId, mode, field3, client.Session.IsCreateWorldSession);

			if (node == null)
				return false;

			var orderedPackets = node.Packets.OrderBy(p => p.Sequence).Select(p => p.Data).ToList();

			var liveInstance = client.Session.Account.ClientInstance;
			Logger.LogInformation("Conversation node (typeId={TypeId}, mode={Mode}, field3={Field3}) -> {Count} packets for NPC 0x{Npc:X}.", typeId, mode, field3, orderedPackets.Count, clickedNpcHandle);

			var oldInstanceVarint = EncodeVarint(node.CapturedInstance);
			var newInstanceVarint = EncodeVarint(liveInstance);

			foreach (var captured in orderedPackets)
			{
				var packet = (byte[])captured.Clone();

				// NPC handle + instance appear as raw uint32s in the bodies; the instance ALSO appears as a protobuf
				// varint in the receiver conversation id, which must be substituted in its varint form too.
				SubstituteUInt32(packet, node.CapturedNpcHandle, clickedNpcHandle);
				SubstituteUInt32(packet, node.CapturedInstance, liveInstance);
				if (oldInstanceVarint.Length == newInstanceVarint.Length)
					ReplaceAll(packet, oldInstanceVarint, newInstanceVarint);

				// PacketStream.SendAsync recomputes the length + CRC, so the byte-substitution doesn't need to.
				await new PacketStream(packet).SendAsync(client);
			}

			return true;
		}

		// Replaces every big-endian uint32 occurrence of oldVal with newVal after the 8-byte length+crc prefix.
		// The handle/instance are 4 bytes both ways, so every nested length prefix stays valid.
		private static void SubstituteUInt32(byte[] packet, uint oldVal, uint newVal)
		{
			if (oldVal == newVal)
				return;

			Span<byte> needle = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(needle, oldVal);

			for (var i = 8; i + 4 <= packet.Length; i++)
			{
				if (packet[i] == needle[0] && packet[i + 1] == needle[1] && packet[i + 2] == needle[2] && packet[i + 3] == needle[3])
				{
					BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(i, 4), newVal);
					i += 3;
				}
			}
		}
	}
}
