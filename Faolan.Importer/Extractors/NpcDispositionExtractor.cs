using System;
using System.Collections.Generic;
using System.Linq;
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
	// 0 = friendly (green), 1 = hostile (red), 2 = neutral (yellow).
	//
	// ⚠️ The target handle is correlated to the NPC's TEMPLATE, not to its NAME (#731). A name is not an identity
	// here: 37 templates ship as "Halen van Dummy" - a training-dummy placeholder that wins over the real name -
	// and a dozen of those are real vendors. Keyed by name, every vote for any of them landed on ONE of them, and
	// the module's name -> community step then attributed the whole pile to whichever faction that one happened to
	// belong to. The spawn record carries the template id right next to the name, so this correlation never needed
	// the name at all.
	public static class NpcDispositionExtractor
	{
		// The sender class 0x5B9756FC as it appears in the rebuilt RpcHeader: a `0x0d` proto tag then the class id
		// LITTLE-ENDIAN (TsharkCaptureReader.TryEndpoint) - so the bytes 0d FC 56 97 5B.
		private static readonly byte[] SenderClassTag = [0x0d, 0xFC, 0x56, 0x97, 0x5B];

		// Every (npc template, observed cCategory) vote in this capture. The module aggregates a majority per
		// FACTION across all captures.
		public static IEnumerable<(uint TemplateId, uint Category)> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			// handle -> the NPC TEMPLATE spawned on it, IN ORDER. A handle with no template is a PLAYER, and players
			// have no disposition row.
			//
			// ⚠️ HANDLES ARE RECYCLED - 91 of them carry two different templates within one capture - so this is
			// replayed as the packets are walked rather than collapsed into one map up front. Taking the first (or
			// the last) spawn per handle attributes every poll in the OTHER window to the wrong NPC, which is how a
			// town crier ends up voting like the mob that later inherited its handle.
			var spawns = NpcSpawnExtractor.Extract(packets).Where(e => e.Handle != 0 && e.TemplateId != 0).ToArray();
			var templates = new Dictionary<uint, uint>();
			var nextSpawn = 0;

			var votes = new List<(uint, uint)>();

			for (var i = 0; i < packets.Count; i++)
			{
				// Roll the handle map forward to this packet: everything spawned at or before it is now current.
				while (nextSpawn < spawns.Length && spawns[nextSpawn].SourceIndex <= i)
				{
					templates[spawns[nextSpawn].Handle] = spawns[nextSpawn].TemplateId;
					nextSpawn++;
				}

				var p = packets[i];
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

				if (templates.TryGetValue(reply.Target.Id, out var templateId))
					votes.Add((templateId, reply.Category));
			}

			return votes;
		}
	}
}
