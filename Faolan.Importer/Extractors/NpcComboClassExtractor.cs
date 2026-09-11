using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// which NPC template swings which `combo_npc_*` row, harvested from the captured combat acks.
	//
	// A `Combat:{Melee,Ranged,Unarmed}` BehaviorControlCenter record carries a `CommandMessage` in its args whose
	// field 7 is the `Combos` catalog id (see `--report combo-swings` for how that was pinned). Resolving the record's
	// actor handle against the capture's own FA015807 spawns gives the template that swung it, and the client's own
	// `Combos.NpcClass` then states the archetype - so the module that consumes this never parses a combo name.
	//
	// HANDLES are RECYCLED, so the handle -> template map is replayed as the packets are walked rather than
	// collapsed up front: 91 handles in the corpus carry two different templates inside one capture, and taking the
	// first (or the last) attributes every swing in the other window to the wrong NPC. Same rule, same reason, as
	// `NpcDispositionExtractor`.
	public static class NpcComboClassExtractor
	{
		// Every (npc template, combo id) swing in this capture, s2c only - the AI drives an NPC combo, so there is no
		// c2s request behind one, and a c2s record is a player asking.
		public static IEnumerable<(uint TemplateId, uint ComboId)> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var spawns = NpcSpawnExtractor.Extract(packets).Where(e => e.Handle != 0 && e.TemplateId != 0).ToArray();
			var templates = new Dictionary<uint, uint>();
			var nextSpawn = 0;

			var swings = new List<(uint, uint)>();

			for (var i = 0; i < packets.Count; i++)
			{
				while (nextSpawn < spawns.Length && spawns[nextSpawn].SourceIndex <= i)
				{
					templates[spawns[nextSpawn].Handle] = spawns[nextSpawn].TemplateId;
					nextSpawn++;
				}

				var p = packets[i];
				if (!p.IsDownload || p is not { IsIirCarrier: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
					continue;

				if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc))
					continue; // the dump cut the record short - not a grammar failure

				if (bcc.BehaviorId is not ((uint)BehaviorId.CombatMelee or (uint)BehaviorId.CombatRanged or (uint)BehaviorId.CombatUnarmed))
					continue;

				var args = bcc.Args.FromProtobuf<CommandMessage>();
				if (!args.ShouldSerializeComboId() || args.ComboId == 0)
					continue;

				if (templates.TryGetValue(bcc.Actor, out var templateId))
					swings.Add((templateId, args.ComboId));
			}

			return swings;
		}
	}
}
