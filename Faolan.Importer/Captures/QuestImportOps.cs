using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Capture-import orchestration for quests: definitions, lines, completed-journal entries, reward options,
	///     objective goals, the region verification and the derived snapshot seeds - the upsert helpers the modules in
	///     Faolan.Importer.Modules drive above the per-subsystem capture importers and extractors.
	/// </summary>
	public static class QuestImportOps
	{
		/// <summary>
		///     Quest definitions harvested from the completed-quest journal.
		/// </summary>
		/// <remarks>
		///     Additive by design: these rows carry only a title and journal sub-line, so they fill the catalog out for
		///     quests no capture session ever touched without disturbing the far richer definitions the QuestFullUpdate
		///     path builds. Deliberately not routed through ImportQuestDefinitions, which replaces a quest's text refs
		///     wholesale and would strip a complete quest down to the two refs the completed journal carries.
		/// </remarks>
		public static async Task<(int Added, int Filled)> ImportCompletedQuestDefinitions(DatabaseContext db, CompletedQuestExtractor.Harvest harvest)
		{
			var existing = await db.QuestDefinitions.Include(q => q.TextRefs).ToDictionaryAsync(q => q.QuestId);
			var maps = await db.Maps.Select(m => m.Id).ToHashSetAsync();
			var areas = await db.QuestLines.Select(a => a.QuestLineId).ToHashSetAsync();
			int added = 0, filled = 0;

			foreach (var q in harvest.Quests)
			{
				// Maps and quest lines are FK targets, so a placeholder row beats dropping the quest.
				if (q.MapId != 0 && maps.Add(q.MapId))
				{
					db.Maps.Add(new Map
					{
						Id = q.MapId,
						Name = ""
					});
				}

				if (q.QuestLineId != 0 && areas.Add(q.QuestLineId))
				{
					db.QuestLines.Add(new QuestLine
					{
						QuestLineId = q.QuestLineId
					});
				}

				if (!existing.TryGetValue(q.QuestId, out var def))
				{
					def = new QuestDefinition
					{
						QuestId = q.QuestId,
						QuestLineId = q.QuestLineId,
						MapId = q.MapId,
						JournalSequence = q.Sequence
					};
					db.QuestDefinitions.Add(def);
					existing[q.QuestId] = def;
					added++;
				}
				else
				{
					// Stable per quest across the whole corpus, so refreshing it is safe and fills older rows.
					def.JournalSequence = q.Sequence;
					filled++;
				}

				AddRefIfMissing(def, QuestTextSlot.Title, q.Title);
				AddRefIfMissing(def, QuestTextSlot.ListSubLine, q.SubLine);

				if (harvest.Areas.TryGetValue(q.QuestLineId, out var header))
				{
					AddRefIfMissing(def, QuestTextSlot.QuestLineTitle, header.Title);
					AddRefIfMissing(def, QuestTextSlot.RegionName, header.Region);
					AddRefIfMissing(def, QuestTextSlot.MapName, header.Map);
				}
			}

			await db.SaveChangesAsync();
			return (added, filled);
		}

		/// <summary>
		///     Fills JournalSequence from the quest index blocks' and MainQuestUpdate lists' pair lists
		///     (<see cref="LineOrderExtractor" />) - the line's whole order, sent for every quest a captured character
		///     held or was granted.
		/// </summary>
		/// <remarks>
		///     Runs after the completed journal's fill and only writes where the sequence is still 0: the two sources
		///     state the same order, and a disagreement is reported rather than resolved by overwrite, so this adds the
		///     steps the completed journal has not seen finished yet. A pair naming a quest with no row (a line newer
		///     than the catalog) is counted and skipped.
		/// </remarks>
		public static async Task<(int Filled, int Disagree, int NoRow)> ImportLineOrder(DatabaseContext db, IReadOnlyDictionary<uint, LineOrderExtractor.Step> order)
		{
			var existing = await db.QuestDefinitions.ToDictionaryAsync(q => q.QuestId);
			int filled = 0, disagree = 0, noRow = 0;
			foreach (var (questId, step) in order)
			{
				if (!existing.TryGetValue(questId, out var def))
				{
					noRow++;
					continue;
				}

				if (def.QuestLineId != step.QuestLineId)
				{
					disagree++;
					Console.WriteLine($"  ⚠️ line order: quest {questId} sits on line {def.QuestLineId} but a captured index block lists it under line {step.QuestLineId}");
					continue;
				}

				if (def.JournalSequence == 0)
				{
					def.JournalSequence = step.Sequence;
					filled++;
				}
				else if (def.JournalSequence != step.Sequence)
				{
					disagree++;
					Console.WriteLine($"  ⚠️ line order: quest {questId} is step {def.JournalSequence} in the completed journal but {step.Sequence} in a captured index block");
				}
			}

			await db.SaveChangesAsync();
			return (filled, disagree, noRow);
		}

		/// <summary>
		///     Only ever adds a slot that is absent - an existing ref is authoritative, having come from a full update.
		/// </summary>
		private static void AddRefIfMissing(QuestDefinition def, QuestTextSlot slot, CompletedQuestExtractor.TextRef text)
		{
			if (def.TextRefs.Any(r => r.Slot == slot))
				return;

			def.TextRefs.Add(new QuestTextRef
			{
				QuestId = def.QuestId,
				Slot = slot,
				RefId = text.RefId,
				Category = text.Category,
				KeyText = LdbKey.Decode(text.Key) ?? new ProtoDecryptionKey(),
				Flags = text.Flags,
				IsLocalized = text.IsLocalized
			});
		}

		/// <summary>
		///     The harvested pick-one-of-N reward lists. Replaces a quest's set wholesale rather than merging into it:
		///     the offer is one list, so a stale position left behind by a shorter re-harvest would be a reward the
		///     client can select and the server cannot explain.
		/// </summary>
		/// <remarks>
		///     A row whose quest or item is not held is dropped with a count, not inserted. Both are hard foreign keys
		///     and a violation would abort the whole import over one un-harvested template, which is the wrong trade
		///     for a capture-sourced table; the count is what keeps the drop from being silent.
		/// </remarks>
		public static async Task<(int Written, int SkippedQuest, int SkippedItem)> ImportQuestRewardOptions(DatabaseContext db, List<QuestRewardOffer> offers)
		{
			ArgumentNullException.ThrowIfNull(offers);

			var knownQuests = (await db.QuestDefinitions.Select(q => q.QuestId).ToListAsync()).ToHashSet();
			var knownItems = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();

			int written = 0, skippedQuest = 0, skippedItem = 0;
			foreach (var quest in offers.GroupBy(o => o.QuestId))
			{
				if (!knownQuests.Contains(quest.Key))
				{
					skippedQuest += quest.Count();
					continue;
				}

				var usable = quest.Where(o => knownItems.Contains(o.ItemTemplateId)).OrderBy(o => o.Pos).ToList();
				skippedItem += quest.Count() - usable.Count;
				if (usable.Count == 0)
					continue;

				await db.QuestRewardOptions.Where(o => o.QuestId == quest.Key).ExecuteDeleteAsync();
				foreach (var o in usable)
				{
					db.QuestRewardOptions.Add(new QuestRewardOption
					{
						QuestId = o.QuestId,
						Pos = o.Pos,
						ItemTemplateId = o.ItemTemplateId,
						Level = o.Level,
						Optional = o.Optional,
						Quantity = o.Quantity
					});
					written++;
				}
			}

			await db.SaveChangesAsync();
			return (written, skippedQuest, skippedItem);
		}

		/// <summary>
		///     Fills QuestLineRewards.Quantity and.WirePos from the journal records' obj3/obj4 elements.
		/// </summary>
		/// <remarks>
		///     The template lists themselves belong to the client catalog and are not rewritten; every captured record
		///     matches it. The exception is a line the catalog does not hold at all, where the wire is the only
		///     statement of the line's reward: those rows are inserted, with the reward type taken from the item's own
		///     SearchCategory, which is what the catalog's type word is (QuestLineReward.RewardType).
		///     A wire element naming a template the catalog does not put on a line it does hold is counted and left
		///     alone - that would be a real disagreement between client data and wire, and none has been seen.
		/// </remarks>
		public static async Task<(int Updated, int Inserted, int Unmatched)> ImportQuestLineRewardQuantities(DatabaseContext db, List<QuestLineRewardQuantity> quantities)
		{
			ArgumentNullException.ThrowIfNull(quantities);
			var rows = await db.QuestLineRewards.ToListAsync();
			var byLine = rows.GroupBy(r => r.QuestLineId).ToDictionary(g => g.Key, g => g.ToList());
			var lines = (await db.QuestLines.Select(l => l.QuestLineId).ToListAsync()).ToHashSet();
			var categories = await db.Items.ToDictionaryAsync(i => i.Id, i => i.SearchCategory);
			int updated = 0, inserted = 0, unmatched = 0;

			foreach (var lineGroup in quantities.DistinctBy(q => (q.QuestLineId, q.ItemTemplateId, q.Optional)).GroupBy(q => q.QuestLineId))
			{
				if (!lines.Contains(lineGroup.Key))
				{
					unmatched += lineGroup.Count();
					continue;
				}

				if (!byLine.TryGetValue(lineGroup.Key, out var lineRows))
				{
					// The catalog holds nothing for this line: the wire's list becomes the line's rows, in wire order.
					foreach (var q in lineGroup.OrderBy(q => q.WirePos))
					{
						db.QuestLineRewards.Add(new QuestLineReward
						{
							QuestLineId = q.QuestLineId,
							Pos = q.WirePos,
							ItemTemplateId = q.ItemTemplateId,
							RewardType = categories.GetValueOrDefault(q.ItemTemplateId),
							Optional = q.Optional,
							Quantity = q.Quantity,
							WirePos = q.WirePos
						});
						inserted++;
					}

					continue;
				}

				foreach (var q in lineGroup)
				{
					var match = lineRows.Where(r => r.ItemTemplateId == q.ItemTemplateId && r.Optional == q.Optional).ToList();
					if (match.Count == 0)
					{
						unmatched++;
						continue;
					}

					foreach (var row in match.Where(r => r.Quantity != q.Quantity || r.WirePos != q.WirePos))
					{
						row.Quantity = q.Quantity;
						row.WirePos = q.WirePos;
						updated++;
					}
				}
			}

			await db.SaveChangesAsync();
			return (updated, inserted, unmatched);
		}

		/// <summary>
		///     Objective goals harvested from captured journals. Additive, and deliberately not routed through
		///     ImportQuestDefinitions - that path rebuilds a quest's goal set and would fight the seed overlays.
		/// </summary>
		/// <remarks>
		///     QuestGoalDefinition.GoalId is a global primary key, so a goal id already owned by a different quest is
		///     skipped rather than moved; attaching one to a second quest fails the import with a concurrency
		///     exception mid-run.
		/// </remarks>
		public static async Task<int> ImportObjectiveGoals(DatabaseContext db, IReadOnlyList<ObjectiveGoalExtractor.Goal> goals)
		{
			var tracked = await db.QuestGoalDefinitions.ToDictionaryAsync(g => g.GoalId);
			var owner = tracked.ToDictionary(k => k.Key, k => k.Value.QuestDefinitionId);
			var quests = await db.QuestDefinitions.ToDictionaryAsync(q => q.QuestId, q => q.MapId);
			var maps = await db.Maps.Select(m => m.Id).ToHashSetAsync();
			int added = 0, noMap = 0, refreshed = 0;

			foreach (var g in goals)
			{
				if (!quests.TryGetValue(g.QuestId, out var questMapId))
					continue; // no definition to hang it on

				// 0 means the goal has no world marker - stored as null, not skipped.
				uint? mapId = g.MapId != 0
					? g.MapId
					: null;
				if (mapId == null)
					noMap++;

				if (owner.TryGetValue(g.GoalId, out var existingQuest))
				{
					if (existingQuest != g.QuestId)
					{
						Console.WriteLine($"  goal {g.GoalId} already belongs to quest {existingQuest}, not {g.QuestId} - skipped.");
						continue;
					}

					// Present already - typically from a seed overlay. The capture is the truth source for every wire
					// field, so refresh them; the server-side-only fields (TargetName, GoalType, QuestItemId) are left
					// alone. Skipping instead would keep hand-written seed values no capture agrees with.
					var row = tracked[g.GoalId];
					row.Sequence = g.Sequence;
					row.RegionOrder = g.RegionOrder;
					row.Separator = g.Separator;
					row.SubId = g.SubId;
					row.MapId = mapId;
					row.MarkerX = mapId == null
						? null
						: g.MarkerX;
					row.MarkerY = mapId == null
						? null
						: g.MarkerY;
					row.RequiredCount = g.RequiredCount;
					row.MarkerRadius = g.MarkerRadius;
					row.HideMapMarker = g.HideMapMarker;
					row.GoalIdEcho = g.GoalIdEcho;
					// reserved[0] is not harvested: it is the captured character's SolvedTimes, not quest data.
					row.Extra1 = g.Extra1;
					row.Extra2 = g.Extra2;
					row.Extra3 = g.Extra3;
					row.Extra4 = g.Extra4;
					row.Extra5 = g.Extra5;
					row.Extra6 = g.Extra6;
					row.ObjectiveRefId = g.Objective.RefId;
					row.ObjectiveCategory = g.Objective.Category;
					row.ObjectiveKey = LdbKey.Decode(g.Objective.Key) ?? new ProtoDecryptionKey();
					row.ObjectiveToken = g.Objective.Token;
					row.TrailingRefId = g.Trailing?.RefId ?? 0;
					row.TrailingCategory = g.Trailing?.Category ?? 0;
					row.TrailingKey = LdbKey.Decode(g.Trailing?.Key) ?? new ProtoDecryptionKey();
					row.TrailingFlags = g.Trailing?.Flags;
					row.TrailingToken = g.Trailing?.Token;
					row.TrailingLocalized = g.Trailing?.IsLocalized ?? false;
					refreshed++;
					continue;
				}

				if (mapId is { } m && maps.Add(m))
				{
					db.Maps.Add(new Map
					{
						Id = m,
						Name = ""
					});
				}

				db.QuestGoalDefinitions.Add(new QuestGoalDefinition
				{
					GoalId = g.GoalId,
					QuestDefinitionId = g.QuestId,
					Sequence = g.Sequence,
					RegionOrder = g.RegionOrder,
					Separator = g.Separator,
					SubId = g.SubId,
					MapId = mapId,
					MarkerX = mapId == null
						? null
						: g.MarkerX,
					MarkerY = mapId == null
						? null
						: g.MarkerY,
					MarkerRadius = g.MarkerRadius,
					HideMapMarker = g.HideMapMarker,
					GoalIdEcho = g.GoalIdEcho,
					// reserved[0] is not harvested - see the refresh path above.
					Extra1 = g.Extra1,
					Extra2 = g.Extra2,
					Extra3 = g.Extra3,
					Extra4 = g.Extra4,
					Extra5 = g.Extra5,
					Extra6 = g.Extra6,
					RequiredCount = g.RequiredCount,
					ObjectiveRefId = g.Objective.RefId,
					ObjectiveCategory = g.Objective.Category,
					ObjectiveKey = LdbKey.Decode(g.Objective.Key) ?? new ProtoDecryptionKey(),
					ObjectiveToken = g.Objective.Token,
					TrailingToken = g.Trailing?.Token,
					TrailingRefId = g.Trailing?.RefId ?? 0,
					TrailingCategory = g.Trailing?.Category ?? 0,
					TrailingKey = LdbKey.Decode(g.Trailing?.Key) ?? new ProtoDecryptionKey(),
					TrailingFlags = g.Trailing?.Flags,
					TrailingLocalized = g.Trailing?.IsLocalized ?? false
				});
				owner[g.GoalId] = g.QuestId;
				added++;
			}

			await db.SaveChangesAsync();
			if (noMap > 0)
				Console.WriteLine($"  {noMap} objective goals carry no world marker (MapId/MarkerX/MarkerY null).");

			if (refreshed > 0)
				Console.WriteLine($"  {refreshed} existing objective goals refreshed from the captures.");

			return added;
		}

		/// <summary>
		///     Quest lines - the grouping tier of the journal tree.
		/// </summary>
		/// <param name="harvested">The wire words (the 4CC), which only the completed-quest journal exposes.</param>
		/// <param name="referenced">Every QuestLineId a quest definition points at.</param>
		/// <remarks>
		///     QuestDefinition.QuestLineId is a real foreign key, so a row must exist for every referenced line before
		///     definitions are saved - the captures only show lines a character actually completed a quest in, far
		///     short of the lines quests live in. Anything unharvested gets a placeholder (Magic 0), exactly as Maps
		///     do; a missing row would fail the key check on rebuild.
		/// </remarks>
		public static async Task<int> ImportQuestLines(DatabaseContext db, IReadOnlyList<QuestLine> harvested, IReadOnlyCollection<uint> referenced)
		{
			var existing = await db.QuestLines.ToDictionaryAsync(a => a.QuestLineId);
			var byArea = harvested.ToDictionary(a => a.QuestLineId);
			var added = 0;

			foreach (var areaId in byArea.Keys.Concat(referenced).Distinct())
			{
				byArea.TryGetValue(areaId, out var wire);

				if (existing.TryGetValue(areaId, out var row))
				{
					// Only ever fill in - never clobber a harvested 4CC with a placeholder's 0. The same for the
					// header: a line headed from the quests (QuestLineExtractor.WithHeadersFromQuests) carries Magic 0
					// and a line headed from the journal carries both, so each writes only the half it has.
					if (wire == null)
						continue;

					if (wire.Magic != 0)
						row.Magic = wire.Magic;

					if (wire.TitleRefId != 0)
					{
						row.TitleRefId = wire.TitleRefId;
						row.TitleCategory = wire.TitleCategory;
						row.TitleKey = wire.TitleKey;
						row.MapRefId = wire.MapRefId;
						row.MapCategory = wire.MapCategory;
						row.MapKey = wire.MapKey;
						row.RegionRefId = wire.RegionRefId;
						row.RegionCategory = wire.RegionCategory;
						row.RegionKey = wire.RegionKey;
					}

					// The two MainQuest_c trailer words, only when a captured index block actually named this line. The
					// extractor leaves TrailerCategory at its solo default for a line no block covered, and writing
					// that over a harvested value would silently demote a raid or dungeon line to solo.
					if (wire.TrailerCategory != 0)
					{
						row.TrailerCategory = wire.TrailerCategory;
						row.TrailerFlag = wire.TrailerFlag;
					}

					continue;
				}

				db.QuestLines.Add(new QuestLine
				{
					QuestLineId = areaId,
					Magic = wire?.Magic ?? 0,
					TitleRefId = wire?.TitleRefId ?? 0,
					TitleCategory = wire?.TitleCategory ?? 0,
					TitleKey = wire?.TitleKey ?? new ProtoDecryptionKey(),
					MapRefId = wire?.MapRefId ?? 0,
					MapCategory = wire?.MapCategory ?? 0,
					MapKey = wire?.MapKey ?? new ProtoDecryptionKey(),
					RegionRefId = wire?.RegionRefId ?? 0,
					RegionCategory = wire?.RegionCategory ?? 0,
					RegionKey = wire?.RegionKey ?? new ProtoDecryptionKey(),
					// A line no captured index block names keeps the solo default, which is the column's own.
					TrailerCategory = wire is { TrailerCategory: not 0 } ? wire.TrailerCategory : 1,
					TrailerFlag = wire?.TrailerFlag ?? 0
				});
				added++;
			}

			await db.SaveChangesAsync();
			return added;
		}

		public static async Task ImportQuestDefinitions(DatabaseContext db, List<QuestDefinition> definitions, bool force = false)
		{
			// QuestDefinition.MapId is a foreign key to Maps, but a harvested quest can point its map marker at a
			// playfield the Maps catalog does not have: the catalog is built from captured MapChange packets, so a map
			// nobody visited in a capture is absent. A placeholder Map row carries the id and nothing else, and the
			// maps module fills in the name if that playfield is ever captured - skipping the quest instead would
			// throw away real journal content to satisfy a key.
			//
			// A null MapId needs none of this: it means the quest has no map marker at all, which is every quest that
			// comes from the client's catalog rather than a capture.
			var knownMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var placeholderMaps = 0;
			foreach (var mapId in definitions.Select(d => d.MapId).OfType<uint>().Distinct().Where(id => !knownMaps.Contains(id)))
			{
				db.Maps.Add(new Map
				{
					Id = mapId,
					Name = string.Empty
				});
				knownMaps.Add(mapId);
				placeholderMaps++;
			}

			if (placeholderMaps > 0)
			{
				await db.SaveChangesAsync();
				Console.WriteLine($"Quest definitions: added {placeholderMaps} placeholder Map row(s) for playfields not in the catalog.");
			}

			var skipped = 0;
			var protectedRows = 0;

			var added = 0;
			var updated = 0;
			foreach (var def in definitions)
			{
				// Unreachable in practice, since every referenced map gets a placeholder above and a mapless quest is
				// null rather than 0. Kept as a guard so a caller that hands over an unplaceholdered map fails loudly
				// on one row instead of aborting the whole import on the foreign key.
				if (def.MapId is { } defMapId && !knownMaps.Contains(defMapId))
				{
					Console.WriteLine($"Quest {def.QuestId}: skipped - map {defMapId} not in the Maps catalog.");
					skipped++;
					continue;
				}

				var existing = await db.QuestDefinitions.Include(q => q.TextRefs).Include(q => q.Goals).FirstOrDefaultAsync(q => q.QuestId == def.QuestId);
				if (existing == null)
				{
					db.QuestDefinitions.Add(def);
					added++;
					continue;
				}

				// Hand-reviewed: leave every field as stored. See QuestDefinition.Verified.
				if (existing.Verified && !force)
				{
					protectedRows++;
					continue;
				}

				existing.QuestLineId = def.QuestLineId;
				existing.GoalId = def.GoalId;
				existing.Level = def.Level;
				existing.MapId = def.MapId;
				existing.MarkerX = def.MarkerX;
				existing.MarkerY = def.MarkerY;

				// The line's ACGHash, off the captured journal. Only overwrite when the harvest has one: the quest
				// index seeds it by hand for a couple of quests, and a line no journal was captured for must not lose
				// it.
				if (def.IndexMagic != 0)
					existing.IndexMagic = def.IndexMagic;

				// The series head off the captured Quest_c record, same rule: only overwrite when the harvest has one.
				// The other snapshot scalars deliberately do not refresh here - they land only when a row is first
				// added - so a column added later reaches an existing row through a line like this one or not at all.
				if (def.ChainHeadQuestId != 0)
					existing.ChainHeadQuestId = def.ChainHeadQuestId;

				// The money reward off the same record (at +0x20), same rule; a minority of captured quests carry one.
				if (def.MoneyReward != 0)
					existing.MoneyReward = def.MoneyReward;

				// The XP and the second slot pair, same rule as the money: only when the harvest has one, so a capture
				// that carries none cannot zero another's. They must refresh here rather than only on insert - a quest
				// whose row was added by the completed-journal harvest first would otherwise ship zeros no matter how
				// many captures state the real values.
				if (def.XpReward != 0)
					existing.XpReward = def.XpReward;
				if (def.FieldF0 != 0)
					existing.FieldF0 = def.FieldF0;
				if (def.FieldF8 != 0)
					existing.FieldF8 = def.FieldF8;
				// The record's presence and its structural words travel together: a row with a captured Quest_c record
				// is snapshot-eligible, and the flag and level words are the record's.
				if (def.HasSnapshotRecord)
				{
					existing.HasSnapshotRecord = true;
					existing.QuestLineFlags = def.QuestLineFlags;
					existing.BandLevel = def.BandLevel;
					existing.FieldEc = def.FieldEc;
				}

				db.QuestTextRefs.RemoveRange(existing.TextRefs);
				existing.TextRefs = def.TextRefs;

				// Merge the harvested primary goal by GoalId into the existing rows without clobbering the
				// server-side-only fields (TargetName, GoalType, QuestItemId) a prior seed set: only the wire fields
				// refresh.
				foreach (var hg in def.Goals)
				{
					var g = existing.Goals.FirstOrDefault(x => x.GoalId == hg.GoalId);
					if (g == null)
					{
						existing.Goals.Add(g = new QuestGoalDefinition
						{
							GoalId = hg.GoalId,
							Sequence = hg.Sequence,
							RegionOrder = hg.RegionOrder,
							Partial = hg.Partial
						});
					}
					else if (!hg.Partial)
					{
						// A goal the snapshot supplied in full stays full: Partial only ever clears on an existing row,
						// so re-harvesting a quest whose secondary goal is now fully known upgrades it, never the
						// other way round.
						g.Partial = false;
					}

					g.ObjectiveRefId = hg.ObjectiveRefId;
					g.ObjectiveCategory = hg.ObjectiveCategory;
					g.ObjectiveKey = hg.ObjectiveKey;

					// The goal's kind tag and required amount. A full update carries both for every goal, and where a
					// snapshot carries them too the two agree, so refreshing them here follows the same "the capture is
					// the truth source for every wire field" rule the goals module applies - and it is what stops a
					// full-update-only goal reading as "kill one unnamed thing".
					g.Separator = hg.Separator;
					g.RequiredCount = hg.RequiredCount;

					// The reserved (marker, 4CC-tag) pairs [1..6], lifted off the full-update body by the codec. The
					// same words the snapshot path harvests, so a goal seen by both gets the same value from either and
					// a goal seen only in a full update does not carry zeros. Refreshed like the other wire fields.
					g.Extra1 = hg.Extra1;
					g.Extra2 = hg.Extra2;
					g.Extra3 = hg.Extra3;
					g.Extra4 = hg.Extra4;
					g.Extra5 = hg.Extra5;
					g.Extra6 = hg.Extra6;

					// The wire step ordinal and the scalar echoes the full update carries for every goal - the same
					// values the snapshot path harvests where both exist, so a nonzero reading refreshes and a zero one
					// never clobbers what a snapshot supplied.
					if (hg.Sequence != 0)
						g.Sequence = hg.Sequence;
					if (hg.GoalIdEcho != 0)
						g.GoalIdEcho = hg.GoalIdEcho;
					if (hg.SubId != 0)
						g.SubId = hg.SubId;
					if (hg.MarkerRadius != 0)
						g.MarkerRadius = hg.MarkerRadius;
					if (hg.HideMapMarker)
						g.HideMapMarker = true;

					// The trailing text record - fill-if-unset, for the same variant-spelling reason the extractor
					// documents: a snapshot row stores its own F0 variant's spelling, and the byte-exact region
					// regeneration depends on it staying.
					if (g.TrailingCategory == 0 && hg.TrailingCategory != 0)
					{
						g.TrailingRefId = hg.TrailingRefId;
						g.TrailingCategory = hg.TrailingCategory;
						g.TrailingKey = hg.TrailingKey;
						g.TrailingLocalized = hg.TrailingLocalized;
						g.TrailingFlags = hg.TrailingFlags;
						g.TrailingToken = hg.TrailingToken;
					}

					// Every goal carries its own map pin, not just the primary one. A goal the wire pins nowhere (all
					// three words read 0) keeps whatever a snapshot already gave it.
					if (hg.MapId is > 0 || hg.MarkerX is > 0 || hg.MarkerY is > 0)
					{
						g.MapId = hg.MapId;
						g.MarkerX = hg.MarkerX;
						g.MarkerY = hg.MarkerY;
					}
				}

				updated++;
			}

			try
			{
				await db.SaveChangesAsync();
			}
			catch (DbUpdateConcurrencyException ex)
			{
				foreach (var e in ex.Entries)
				{
					Console.WriteLine($"  CONFLICT entity={e.Entity.GetType().Name} state={e.State} keys=" +
					                  string.Join(",", e.Properties.Where(pr => pr.Metadata.IsPrimaryKey()).Select(pr => $"{pr.Metadata.Name}={pr.CurrentValue}")));
				}

				throw;
			}

			Console.WriteLine($"Quest definitions (journal content): {definitions.Count} harvested, {added} new, {updated} updated, {skipped} skipped (map not in catalog)"
			                  + (protectedRows > 0 ? $", {protectedRows} left as hand-verified." : "."));
		}

		/// <summary>
		///     Flags each quest whose objective region, rebuilt from the rows just imported, reproduces the captured
		///     bytes exactly. That is the gate deciding what may ride the login snapshot: the region still has
		///     undecoded fields, and a region the client cannot parse asserts it (statlist.cpp:134).
		/// </summary>
		/// <remarks>
		///     Compared like with like: only against the captures whose record carries the same XpReward variant as the
		///     record that would be shipped for that quest (see ObjectiveGoalExtractor.CapturedRegions for the evidence
		///     that F0 selects the region's spelling). Comparing against every capture regardless of variant fails a
		///     quest for being a faithful rendering of the variant that is not emitted.
		///     The safety property is the one that matters: the exact bytes shipped are bytes retail sent. A quest with
		///     no capture of the emitted variant stays unflagged - unverifiable is not verified.
		/// </remarks>
		public static async Task<(int Verified, int Total)> VerifyObjectiveRegions(DatabaseContext db,
			IReadOnlyDictionary<uint, List<(uint Variant, List<QuestObjectiveGoal> Goals)>> capturedByQuest)
		{
			var defs = await db.QuestDefinitions.Include(q => q.Goals).ToDictionaryAsync(q => q.QuestId);
			int verified = 0, total = 0;

			foreach (var (questId, captures) in capturedByQuest)
			{
				if (!defs.TryGetValue(questId, out var def) || def.Goals.Count == 0)
					continue;

				total++;

				bool Reproduces((uint Variant, List<QuestObjectiveGoal> Goals) c)
				{
					// The goal's SolvedTimes is the captured character's progress, not quest data, so it is not on the
					// row and cannot be rebuilt from one. Feeding this capture's own reading in keeps the comparison
					// honest: every other word still has to match exactly, and a region that only reproduces because
					// progress was ignored still fails.
					return QuestObjectiveRegionCodec
						.Build(QuestObjectiveRegions.ForQuest(def, c.Goals[0].Instance,
							g => c.Goals.FirstOrDefault(cg => cg.GoalId == g.GoalId)?.Reserved is { Length: > 0 } r
								? r[0]
								: 0))
						.SequenceEqual(QuestObjectiveRegionCodec.Build(new QuestObjectiveRegion
						{
							Goals = c.Goals
						}));
				}

				// Two conditions, and both matter: at least one capture must reproduce, so the bytes shipped are bytes
				// retail sent rather than a near-miss with no same-variant capture to contradict it; and every capture
				// of the emitted variant must reproduce, so there is no counter-example among the records actually
				// emitted. A quest that only matches another variant's records fails the second.
				def.ObjectiveRegionVerified = captures.Any(Reproduces) && captures.Where(c => c.Variant == def.FieldF0).All(Reproduces);

				if (def.ObjectiveRegionVerified)
					verified++;
			}

			await db.SaveChangesAsync();
			return (verified, total);
		}

		/// <summary>
		///     Derives a Quest_c record for a quest that has everything the login snapshot needs except the record
		///     itself. Must run after <see cref="VerifyObjectiveRegions" />, whose verdict is the gate it reads.
		/// </summary>
		/// <remarks>
		///     The candidates are quests whose region provably rebuilds, whose line carries its 4CC, and which have all
		///     five header text refs and a level - every part of the snapshot portion already proven except the record
		///     scalars, which no capture ever carried for them.
		///     This synthesises wire data, which SnapshotTails.IsSnapshotEligible otherwise warns against. What makes
		///     it defensible is that the record's shape is fixed and only its values are invented, and each invented
		///     value was swept in-client on a live login: @+0x14 takes five observed values and the client renders all
		///     five (only the Share button changes, on bit 4), so a defaulted flag word is cosmetic rather than
		///     structural; @+0x28 is the XP the journal prints, so 0 only renders wrongly on screen; and @+0xf0 /
		///     @+0xf8 changed nothing visible at any value tried. The structural scalars are constants: FieldEc 1,
		///     BandTolerance 0, Field11c 0, and the chain head, which QuestcRecords.ForQuest already falls back to the
		///     quest's own id for.
		///     A quest with no captured sibling in its line gets flags <see cref="DefaultQuestLineFlags" />.
		/// </remarks>
		public static async Task<int> SeedDerivedSnapshotRecords(DatabaseContext db)
		{
			ArgumentNullException.ThrowIfNull(db);

			// The flag word is a property of the line, so a captured sibling states it for the whole line.
			var lineFlags = await db
				.QuestDefinitions.Where(q => q.HasSnapshotRecord && q.QuestLineFlags != 0)
				.GroupBy(q => q.QuestLineId)
				.Select(g => new { Line = g.Key, Flags = g.Max(q => q.QuestLineFlags) })
				.ToDictionaryAsync(x => x.Line, x => x.Flags);

			// Verified against a capture, or buildable from the goal rows - the same widening `IsSnapshotEligible`
			// makes, and it has to stay the same or the two disagree about which quests ship. Buildability is
			// evaluated below, since it needs the goals loaded, so the query only narrows on what SQL can.
			var candidates = await db
				.QuestDefinitions.Include(q => q.Goals)
				.Include(q => q.TextRefs)
				.Include(q => q.Line)
				.Where(q => !q.HasSnapshotRecord && q.Level != 0)
				.ToListAsync();

			var seeded = 0;
			foreach (var def in candidates)
			{
				// Exactly the other arms of IsSnapshotEligible, so this can never flag a quest the gate would reject.
				if (def.Goals.Count == 0 || def.Line is not { Magic: not 0 })
					continue;

				if (!def.ObjectiveRegionVerified && !SnapshotTails.IsObjectiveRegionBuildable(def))
					continue;

				var slots = def.TextRefs.Select(t => t.Slot).ToHashSet();
				if (!SnapshotHeaderSlots.All(slots.Contains))
					continue;

				def.QuestLineFlags = lineFlags.TryGetValue(def.QuestLineId, out var f)
					? f
					: DefaultQuestLineFlags;
				def.FieldEc = 1;

				// @+0x118 runs level..level+5 across the captured records; the level itself is the low end of that
				// range and the only value that can be derived rather than guessed.
				def.BandLevel = def.Level;
				def.HasSnapshotRecord = true;
				seeded++;
			}

			await db.SaveChangesAsync();
			return seeded;
		}

		/// <remarks>Mirrors SnapshotTails.HeaderSlots, which is private to the codec layer.</remarks>
		private static readonly QuestTextSlot[] SnapshotHeaderSlots =
			[QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.QuestLineTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

		/// <remarks>
		///     The modal captured @+0x14, and the value with bit 4 clear - shareable, the permissive choice on the only
		///     bit whose meaning is known.
		/// </remarks>
		private const uint DefaultQuestLineFlags = 2;
	}
}
