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

			var orderedPackets = node
				.Packets
				.OrderBy(p => p.Sequence)
				.Select(p => p.Data)
				.ToList();

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

		// The instance baked into the captured KnubotCloseChatWindow body (hoxxling's clientInstance), re-addressed
		// to the live player on send.
		private const uint CloseChatWindowCapturedInstance = 0x01316E71;

		// A KnubotCloseChatWindow (0xC4AF39EB) body, captured verbatim. It closes the NPC chat window with an EMPTY
		// answer list (the remoteformat is id="0" with no options) - retail's "this NPC has nothing (more) to say"
		// reply. The remoteformat (incl. its key) is byte-identical across every capture, so it is a reusable
		// constant; only the player instance @offset 12 is re-addressed. See SendConversationClose.
		private static readonly byte[] KnubotCloseChatWindowBody =
		[
			0x00, 0x00, 0x00, 0x7C, 0xC4, 0xAF, 0x39, 0xEB, 0x00, 0x00, 0xC3, 0x50, 0x01, 0x31, 0x6E, 0x71, 0x00, 0x00,
			0x01, 0x00, 0x00, 0xC3, 0x50, 0x00, 0x01, 0x7E, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x5F, 0x3C, 0x72, 0x65,
			0x6D, 0x6F, 0x74, 0x65, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3D, 0x22, 0x30, 0x22, 0x20,
			0x63, 0x61, 0x74, 0x65, 0x67, 0x6F, 0x72, 0x79, 0x3D, 0x22, 0x31, 0x30, 0x30, 0x22, 0x20, 0x6B, 0x65, 0x79,
			0x3D, 0x22, 0x2E, 0x66, 0x7A, 0x54, 0x75, 0x45, 0x79, 0x69, 0x75, 0x37, 0x42, 0x26, 0x23, 0x39, 0x32, 0x3B,
			0x5A, 0x59, 0x5A, 0x4D, 0x6F, 0x71, 0x31, 0x2D, 0x22, 0x20, 0x6B, 0x6E, 0x75, 0x62, 0x6F, 0x74, 0x3D, 0x22,
			0x30, 0x22, 0x20, 0x20, 0x3E, 0x3C, 0x2F, 0x72, 0x65, 0x6D, 0x6F, 0x74, 0x65, 0x66, 0x6F, 0x72, 0x6D, 0x61,
			0x74, 0x3E
		];

		// Closes the NPC chat window gracefully (KnubotCloseChatWindow with an empty answer list), re-addressed to the
		// live player. Sent when a TalkToNpc has NO matching conversation node, so the client never hangs mid-dialogue
		// waiting for a reply that won't come - the "advance a step with no captured branch -> chat locks, can't ESC"
		// bug. (Conversation replay is coverage-bound: (Mode, Field3) is quest-state-dependent and the captures cover
		// only some states per NPC, so an un-captured branch is expected; closing the window is the right answer.)
		internal Task SendConversationClose(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;
			var body = (byte[])KnubotCloseChatWindowBody.Clone();
			SubstituteUInt32(body, CloseChatWindowCapturedInstance, id);

			return new PacketStream()
				.WriteHeader([0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x08], AppendVarint([0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10], id), 0x2000u)
				.WriteArray(body)
				.SendAsync(client);
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
