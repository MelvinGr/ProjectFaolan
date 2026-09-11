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
	// Harvests the retail overhead-name disposition (cCategory) per NPC from the captured RequestAggroStatus
	// responses - the only correct hostility source. An NPC's Community is faction membership, not hostility:
	// community 1 is the civilian/default faction shared by Citizens, vendors and guards, so a `Community > 0` aggro
	// test makes every townsperson hostile.
	//
	// The response is GameCharAgentProject (sender class 0x5B9756FC) -> GameCharInterfaceProject, opcode 0x2000,
	// server->client, with a body of exactly [u32 0xC350][u32 targetInstance][u32 cCategory], cCategory being
	// 0 = friendly (green), 1 = hostile (red), 2 = neutral (yellow).
	//
	// The target handle is correlated to the NPC's template, not to its name. A name is not an identity
	// here: 37 templates ship as "Halen van Dummy" - a training-dummy placeholder that wins over the real name -
	// and a dozen of those are real vendors. Keyed by name, every vote for any of them landed on one of them, and
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
			// handle -> the NPC template spawned on it, in order. A handle with no template is a player, and players
			// have no disposition row.
			//
			// HANDLES are RECYCLED - 91 of them carry two different templates within one capture - so this is
			// replayed as the packets are walked rather than collapsed into one map up front. Taking the first (or
			// the last) spawn per handle attributes every poll in the other window to the wrong NPC, which is how a
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

		/// <summary>
		///     The NPC templates this capture saw attack something - an independent hostility signal for a creature the
		///     client never happened to poll.
		/// </summary>
		/// <remarks>
		///     The poll harvest above can only speak for creatures a player's client asked about, and its faction
		///     fallback only for factions some member of which was asked about. 154 templates - 645 placements - came
		///     out of it with no disposition at all, and a null disposition is read as friendly by the world model, so
		///     every one of them stood around green and passive. The beach crocodiles (14049) are the reported case:
		///     they never aggro because nothing ever told us they were hostile.
		///     <para>
		///         A creature that SWINGS at somebody is hostile, and that is an observation rather than an inference -
		///         the same corpus, a different record. Handles are recycled (91 within a single capture), so the
		///         handle -> template map is rolled forward packet by packet exactly as the poll pass does; taking the
		///         first or last spawn per handle would credit one creature's attacks to another.
		///     </para>
		/// </remarks>
		public static IEnumerable<uint> ExtractAttackers(IReadOnlyList<CapturedPacket> packets)
		{
			ArgumentNullException.ThrowIfNull(packets);
			var spawns = NpcSpawnExtractor.Extract(packets).Where(e => e.Handle != 0 && e.TemplateId != 0).ToArray();
			var templates = new Dictionary<uint, uint>();
			var nextSpawn = 0;
			var attackers = new HashSet<uint>();

			for (var i = 0; i < packets.Count; i++)
			{
				while (nextSpawn < spawns.Length && spawns[nextSpawn].SourceIndex <= i)
				{
					templates[spawns[nextSpawn].Handle] = spawns[nextSpawn].TemplateId;
					nextSpawn++;
				}

				var p = packets[i];
				if (p is not { IsDownload: true, IsIirCarrier: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
					continue;
				if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
					continue;
				if (bcc.BehaviorId is not ((uint)BehaviorId.CombatMelee or (uint)BehaviorId.CombatRanged or (uint)BehaviorId.CombatUnarmed))
					continue;

				if (templates.TryGetValue(bcc.Actor, out var templateId))
					attackers.Add(templateId);
			}

			return attackers;
		}
	}
}
