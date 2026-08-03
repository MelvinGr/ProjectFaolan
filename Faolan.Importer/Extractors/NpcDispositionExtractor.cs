using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Harvests the retail overhead-name DISPOSITION (cCategory) per NPC from the captured RequestAggroStatus
	// responses - the SOLE correct hostility source (an NPC's Community is faction MEMBERSHIP, not hostility:
	// community 1 is the civilian/default faction shared by Citizens, vendors and guards, so the old
	// `Community > 0` aggro test wrongly made every townsperson hostile). See #256/aggro.
	//
	// The response is GameCharAgentProject (sender class 0x5B9756FC) -> GameCharInterfaceProject, opcode 0x2000,
	// server->client, with a body of EXACTLY [u32 0xC350][u32 targetInstance][u32 cCategory], cCategory being
	// 0 = friendly (green), 1 = hostile (red), 2 = neutral (yellow). The target handle is correlated to the NPC by
	// name via the same spawn-name map the conversation/shop extractors use.
	public static class NpcDispositionExtractor
	{
		// The sender class 0x5B9756FC as it appears in the rebuilt RpcHeader: a `0x0d` proto tag then the class id
		// LITTLE-ENDIAN (TsharkCaptureReader.TryEndpoint) - so the bytes 0d FC 56 97 5B.
		private static readonly byte[] SenderClassTag = [0x0d, 0xFC, 0x56, 0x97, 0x5B];

		// Every (npc name, observed cCategory) vote in this capture. The module aggregates a majority per name
		// across all captures.
		public static IEnumerable<(string Name, uint Category)> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var names = ConversationExtractor.BuildSpawnNameMap(packets);
			var votes = new List<(string, uint)>();

			foreach (var p in packets)
			{
				if (!p.IsDownload || p.WireOpcode != CapturedPacket.Wire(GameCharInterfaceProjectMethods.DispatchUpdateAggroStatus) || p.Bytes.Length < 12)
					continue;

				var headerLen = (int)p.Bytes.ReadU32Be(8);
				var bodyStart = 12 + headerLen;
				if (bodyStart > p.Bytes.Length)
					continue;

				// Confirm it's a RequestAggroStatus reply (GameCharAgentProject sender), so we don't pick up other
				// 0xC350-tagged replies that share the 0x2000 method index on another interface.
				var header = p.Bytes.AsSpan(12, headerLen);
				if (header.IndexOf(SenderClassTag) < 0)
					continue;

				UpdateAggroStatus reply;
				try
				{
					reply = UpdateAggroStatusCodec.Parse(p.Bytes.AsSpan(bodyStart));
				}
				catch (CodecParseException)
				{
					continue; // not the 12-byte disposition reply
				}

				if (reply.Target.Class != EntityClass.Character || reply.Category > 2)
					continue;

				if (names.TryGetValue(reply.Target.Id, out var name) && !string.IsNullOrEmpty(name))
					votes.Add((name, reply.Category));
			}

			return votes;
		}
	}
}
