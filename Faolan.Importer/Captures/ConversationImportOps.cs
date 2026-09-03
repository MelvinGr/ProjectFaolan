using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Shared capture-import orchestration the modules (Faolan.Importer.Modules) drive - the upsert/wipe-rebuild
	// helpers that sit above the per-subsystem CaptureX importers/extractors.
	// NPC conversations: the content-addressed dialogue graph fold. One of the five domain halves of what was one 2,064-line ImportOps (#928 item 7).
	public static class ConversationImportOps
	{
		// Rebuilds the dialogue GRAPH from the whole corpus (#642): the harvested walks are columnized here, folded into
		// content-addressed nodes + explicit edges by ConversationGraph, and written whole.
		//
		// ⚠️ WHOLE-CORPUS ONLY, which the answer-path tree was not (#419 supported an upsert that merged one new dump
		// into what was stored). The fold is a global partition over every walk - which screens merge, and which need a
		// Discriminator, is decided by comparing them against each other - so folding one capture against a database
		// that no longer remembers the walks it came from cannot reproduce the same classification. The module
		// therefore advertises Overwrite only, and the merge rule the upsert existed for ("the most complete capture of
		// a screen wins") is applied inside the fold instead, across all captures at once.
		//
		// A row marked Verified is hand-reviewed and survives untouched unless `force`. That is what makes "import the
		// bulk, correct the tail by hand" a workflow rather than a treadmill. Edges and entries always rebuild: they
		// are wiring between screens, not content, and half a rebuilt graph is not a graph.
		public static async Task<ConversationImportResult> ImportConversations(DatabaseContext db, List<ConversationWalk> conversations, bool force = false)
		{
			ArgumentNullException.ThrowIfNull(conversations);

			var verified = force
				? []
				: (await db.ConversationNodes.Where(n => n.Verified).Select(n => n.Id).ToListAsync()).ToHashSet();

			// A conversation NPC's template must exist to be the FK target (#244), and since #646 it is also the
			// screen's IDENTITY - so a walk whose template is not imported is DROPPED rather than downgraded to a
			// name-only row. The world is pruned, so this can legitimately happen; the count is reported.
			var knownTemplates = (await db.NpcTemplates.Select(t => t.TemplateId).ToListAsync()).ToHashSet();
			var knownQuests = (await db.QuestDefinitions.Select(q => q.QuestId).ToListAsync()).ToHashSet();
			var knownObjects = (await db.WorldObjects.Select(o => o.Id).ToListAsync()).ToHashSet();

			// The NAME is now only a LABEL (#646), so it is stored as harvested - empty where the capture never named
			// the NPC - instead of being given the "#<handle>" stand-in identity it needed while it was the key. What
			// must be present is the TEMPLATE: a walk pointing at one we did not import cannot be a row, so it is
			// filtered out here and the fold reports the rest (a walk with NO template at all).
			var unknownTemplate = conversations.Count(e => e.NpcTemplateId is { } t && !knownTemplates.Contains(t));
			var harvested = conversations
				.Where(e => e.NpcTemplateId is not { } t || knownTemplates.Contains(t))
				.Select(e => new ConversationGraph.Harvested(e, [.. e.Packets.Select(BuildPacket)]))
				.ToList();

			var graph = ConversationGraph.Fold(harvested);
			if (unknownTemplate > 0)
				graph.Report.Add($"{unknownTemplate} walk(s) name an NPC template that is not imported - dropped (the world is pruned)");

			// The ids are DERIVED from each row's identity, so a re-harvest of the same corpus reproduces them exactly
			// and faolan.sql does not churn. Two rows hashing to one id is either the same screen twice (the fold
			// should already have merged it) or a genuine hash collision - both are import bugs, so name the pair and
			// fail rather than let one silently overwrite the other.
			var byId = new Dictionary<uint, string>();
			foreach (var n in graph.Nodes)
			{
				if (!byId.TryAdd(n.Id, $"{n.NpcName} ({n.NpcTemplateId}) {n.ContentKey}#{n.Discriminator}"))
					throw new InvalidOperationException($"Conversation node id 0x{n.Id:X8} is not unique: '{n.NpcName} ({n.NpcTemplateId}) {n.ContentKey}' collides with '{byId[n.Id]}'.");
			}

			// ⚠️ TravelDestinations FK a node and DO cascade, so the delete below takes them with it - and the nodes
			// then come straight back under the SAME derived ids, so nothing about the trip has actually stopped being
			// true. Snapshot them here and put them back after the rebuild (#763).
			//
			// This matters because the two halves of that table have different sources. `travel-wiring` re-harvests
			// the CAPTURED trips right after this, so those survived by luck; the `{teleportplayer}` directive trips
			// come from `rdb-dialogue-teleports`, which is OPT-IN and needs an RdbDir - so a default
			// `--skip characters` run destroyed four rows nothing in that run could rebuild, and the loss was silent.
			var travelBefore = await db.TravelDestinations.AsNoTracking().ToListAsync();

			// The rebuild drops the WIRING before the screens: an edge names two nodes, and the model deliberately does
			// not cascade its inbound end (see DatabaseContext), so deleting a node underneath a live edge is a foreign
			// key violation rather than a silent orphan.
			await db.ConversationEntries.ExecuteDeleteAsync();
			await db.ConversationEdges.ExecuteDeleteAsync();
			await db.ConversationNodes.Where(n => force || !n.Verified).ExecuteDeleteAsync();

			// ExecuteDelete goes straight to the database and does NOT tell the change tracker, so anything this
			// context already holds would collide on the identity map when the same (derived) ids are re-added.
			db.ChangeTracker.Clear();

			var inserted = 0;
			foreach (var n in graph.Nodes)
			{
				if (verified.Contains(n.Id))
					continue; // hand-reviewed: the stored screen IS the answer

				db.ConversationNodes.Add(new ConversationNode
				{
					Id = n.Id,
					NpcName = n.NpcName,
					ContentKey = n.ContentKey,
					Discriminator = n.Discriminator,
					NpcTemplateId = n.NpcTemplateId,
					Packets = n.Packets
				});
				inserted++;
			}

			await db.SaveChangesAsync();

			// The screens have to exist before anything can point at them, so the wiring is a second pass.
			var nodeIds = graph.Nodes.Select(n => n.Id).Concat(verified).ToHashSet();
			var edges = 0;
			foreach (var e in graph.Edges)
			{
				// A branch into or out of a screen that did not survive the fold is dangling wiring; dropping it is
				// what keeps the FK honest, and the count is reported rather than swallowed.
				if (!nodeIds.Contains(e.FromNodeId) || !nodeIds.Contains(e.ToNodeId))
					continue;

				db.ConversationEdges.Add(new ConversationEdge
				{
					Id = ConversationEdge.ComputeId(e.FromNodeId, e.OptionId, e.ConditionQuestId, e.ConditionQuestState),
					FromNodeId = e.FromNodeId,
					OptionId = e.OptionId,
					ToNodeId = e.ToNodeId,
					ConditionQuestId = e.ConditionQuestId,
					ConditionQuestState = e.ConditionQuestState,
					GrantQuestId = e.GrantQuestId is { } q && knownQuests.Contains(q)
						? q
						: null
				});
				edges++;
			}

			var entries = 0;
			foreach (var e in graph.Entries)
			{
				if (!nodeIds.Contains(e.NodeId))
					continue;

				db.ConversationEntries.Add(new ConversationEntry
				{
					Id = ConversationEntry.ComputeId(e.NpcTemplateId, e.Trigger, e.TriggerWorldObjectId, e.ConditionQuestId, e.ConditionQuestState, e.RequiresRepeatGreeting),
					NpcName = e.NpcName,
					NpcTemplateId = e.NpcTemplateId,
					Trigger = e.Trigger,
					TriggerWorldObjectId = e.TriggerWorldObjectId is { } w && knownObjects.Contains(w)
						? w
						: null,
					ConditionQuestId = e.ConditionQuestId,
					ConditionQuestState = e.ConditionQuestState,
					RepeatGreeting = e.RequiresRepeatGreeting,
					NodeId = e.NodeId
				});
				entries++;
			}

			await db.SaveChangesAsync();

			// Put the travel rows back on the screens that came back (#763). Keyed by the node id, which is DERIVED
			// from the screen's content - so a screen that still exists is the same screen, and the trip it sold is
			// still the trip it sells. A row whose screen did NOT survive the fold is genuinely dangling and is
			// dropped, counted, not silently swallowed.
			var travelRestored = 0;
			foreach (var t in travelBefore)
			{
				if (!nodeIds.Contains(t.FromNodeId))
					continue;

				db.TravelDestinations.Add(t);
				travelRestored++;
			}

			if (travelRestored > 0)
				await db.SaveChangesAsync();

			var travelLost = travelBefore.Count - travelRestored;
			if (travelLost > 0)
				graph.Report.Add($"{travelLost} travel destination(s) sat on a screen that did not survive the fold - dropped");

			var dropped = graph.Edges.Count - edges + (graph.Entries.Count - entries);
			if (dropped > 0)
				graph.Report.Add($"{dropped} edge(s)/entrie(s) point at a screen that did not survive the fold - dropped");

			return new ConversationImportResult(inserted, edges, entries, verified.Count, graph.Report);
		}

		// What a conversation rebuild produced: the screens, the branches between them, the roots that open them, and
		// the rows skipped as hand-verified. `Report` carries what the fold could not do cleanly - a content group it
		// had to discriminate, a branch whose option list was never captured - because a silent fold is exactly the
		// thing you would otherwise spend an afternoon on.
		public sealed record ConversationImportResult(int Nodes, int Edges, int Entries, int Protected, List<string> Report);

		// Upsert (never wipe): refresh from a clean slate so stale handle-keyed (un-named) nodes don't linger. One
		// ConversationPacket is stored FULLY STRUCTURED (#188): decoded into typed columns + ConversationScreen rows,
		// no raw Data blob. A packet that doesn't decode to a recognised sub-opcode is a REJECT (throws) rather than a
		// silent raw-replay - the runtime has no Data fallback, so an un-decodable packet must surface here.
		internal static ConversationPacket BuildPacket(byte[] data, int sequence)
		{
			if (ConversationScreenDecoder.Decode(data) is not { } cols)
				throw new InvalidOperationException($"Conversation packet #{sequence} did not decode to a recognised sub-opcode (no Data fallback exists).");

			var pkt = new ConversationPacket
			{
				Sequence = sequence,
				SubOpcode = cols.SubOpcode,
				Env = cols.Env,
				Field = cols.Field,
				Flag = cols.Flag,
				SenderInstance = cols.SenderInstance,
				ShotSize = cols.Camera?.ShotSize,
				ShotType = cols.Camera?.ShotType,
				ShotEmphasis = cols.Camera?.ShotEmphasis,
				ShotFov = cols.Camera?.ShotFov,
				ShotTilt = cols.Camera?.ShotTilt,
				Autoface = cols.Camera?.Autoface,
				HeadPaddingY = cols.Camera?.HeadPaddingY
			};
			if (cols.Screens != null)
			{
				for (var i = 0; i < cols.Screens.Count; i++)
					// Throws if a screen isn't byte-exact round-trippable; stores it with the session slots (the capturing
					// player's name/guild, the retail server label) blanked - the runtime fills them from the live player.
					pkt.Screens.Add(ConversationScreenColumns.ToStoredRow(cols.Screens[i], i));
			}

			return pkt;
		}
	}
}
