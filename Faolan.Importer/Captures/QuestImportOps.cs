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
	// Quests: definitions, lines, completed-journal entries, reward options, objective goals, the region verification and the derived snapshot seeds. One of the five domain halves of what was one 2,064-line ImportOps (#928 item 7).
	public static class QuestImportOps
	{
		// Quest definitions harvested from the COMPLETED-quest journal (#390). ADDITIVE by design: these rows carry
		// only a title + journal sub-line, so they fill the catalog out for quests no capture session ever touched
		// without disturbing the far richer definitions the QuestFullUpdate path builds.
		//
		// ⚠️ Deliberately NOT routed through ImportQuestDefinitions, which does
		// `RemoveRange(existing.TextRefs); existing.TextRefs = def.TextRefs` - that would strip a complete quest's
		// summary/objective refs down to the two the completed journal happens to carry.
		public static async Task<(int Added, int Filled)> ImportCompletedQuestDefinitions(DatabaseContext db, CompletedQuestExtractor.Harvest harvest)
		{
			var existing = await db.QuestDefinitions.Include(q => q.TextRefs).ToDictionaryAsync(q => q.QuestId);
			var maps = await db.Maps.Select(m => m.Id).ToHashSetAsync();
			var areas = await db.QuestLines.Select(a => a.QuestLineId).ToHashSetAsync();
			int added = 0, filled = 0;

			foreach (var q in harvest.Quests)
			{
				// Maps and areas are FK targets - a placeholder beats dropping the quest (it's okay to insert
				// maps without a name).
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

		// Only ever ADDS a slot that is absent - an existing ref is authoritative, having come from a full-update.
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

		// Caster NPCs' spells (#232), upserted by (NpcTemplate type, spell id). The caster NAME is resolved to its NpcTemplate TYPE id
		// (types were upserted by ImportNpcSpawns just before) and only stored as the NpcTemplateId FK - a cast whose caster
		// has no captured NpcTemplate type (off-beach named NPCs) is DROPPED, since the loader reads spells via the FK
		// (NpcTemplate.Spells).
		//
		// ⚠️ The `playerNames` check is a BELT, not the braces (#616). It only knows characters in OUR database, so it
		// never sees a stranger who happened to stand near the capturing client on the live retail server. What keeps
		// those out is structural and upstream: their spawns carry player_stats, so NpcSpawnExtractor never gives them
		// a handle -> name entry for NpcSpellExtractor to join on (measured: all 317 player-backed spawns in the
		// corpus are excluded, NpcSpellExtractorPlayerFilterTests). Keep both - this one also catches the capture's
		// OWN player once imported, whose spawn is dropped by handle rather than by message.
		// The harvested pick-one-of-N reward lists (#815). Replaces a quest's set wholesale rather than merging into it:
		// the offer is one list, so a stale position left behind by a shorter re-harvest would be a reward the client
		// can select and the server cannot explain.
		//
		// ⚠️ A row whose quest or item we do not hold is DROPPED with a count, not inserted. Both are hard FKs and a
		// violation would abort the whole import over one un-harvested template, which is the wrong trade for a
		// capture-sourced table - the count is what keeps the drop from being silent.
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

		// Fills QuestLineRewards.Quantity and .WirePos from the journal records' obj3/obj4 elements (#893). The
		// template lists themselves are the catalog's and are NOT rewritten - they matched it on 1,553 of 1,553
		// captured records. The one exception is a LINE the catalog does not hold at all (3780 in the corpus): there
		// the wire is the only statement of the line's reward and its rows are inserted, with the reward type taken
		// from the item's own SearchCategory - which is what the catalog's `type` word is (QuestLineReward.RewardType).
		//
		// A wire element naming a template the catalog does NOT put on a line it does hold is counted and left alone:
		// that would be a real disagreement between client data and wire, and none has been seen.
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

		// @609 objective goals harvested from captured journals. ADDITIVE, and deliberately NOT routed through
		// ImportQuestDefinitions - that path rebuilds a quest's goal set and would fight the Seed* overlays.
		//
		// ⚠️ QuestGoalDefinition.GoalId is a GLOBAL primary key. A goal id already owned by a different quest is
		// SKIPPED rather than moved: attaching one to a second quest is what made an earlier attempt at this die with
		// a DbUpdateConcurrencyException mid-import.
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

				// 0 means the goal has no world marker - stored as NULL, not skipped.
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

					// Present already - typically from a Seed* overlay. The CAPTURE is the truth source for every wire
					// field, so refresh them; the server-side-only fields (TargetName/GoalType/QuestItemId) are left
					// alone. Skipping instead kept hand-written seed values that no capture agrees with, which is what
					// left the last ~116 regions unreproducible.
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
					// reserved[0] is NOT harvested: it is the captured character's SolvedTimes, not quest data (#550).
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
					// reserved[0] is NOT harvested - see the refresh path above (#550).
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

		// Quest journal-content definitions, refreshed per harvested quest; un-harvested quests untouched.
		// Quest AREAS - the grouping tier of the journal tree. `harvested` carries the wire words (the 4CC), which only
		// the completed-quest journal exposes (#390); `referenced` is every QuestLineId a quest definition points at.
		//
		// QuestDefinition.QuestLineId is a real FK, so a row must exist for EVERY referenced area before definitions are
		// saved - the captures only show areas a character actually completed a quest in, which is far short of the
		// areas quests live in. Anything unharvested gets a placeholder (Magic 0), exactly as Maps do; a missing row
		// would otherwise fail the FK check on rebuild.
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
					// Only ever fill IN - never clobber a harvested 4CC with a placeholder's 0. The same for the header:
					// a line the quests headed (QuestLineExtractor.WithHeadersFromQuests) carries Magic 0, and a line
					// the journal headed carries both - each writes only the half it has.
					if (wire == null)
						continue;

					if (wire.Magic != 0)
					{
						row.Magic = wire.Magic;
						row.TableField1 = wire.TableField1;
					}

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

					// The two MainQuest_c trailer words (#896) - only when a captured index block actually named this
					// line. The extractor leaves TrailerCategory at its SOLO default for a line no block covered, and
					// writing that over a harvested 4 or 8 would silently demote a raid/dungeon line to solo.
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
					TableField1 = wire?.TableField1 ?? 0,
					TitleRefId = wire?.TitleRefId ?? 0,
					TitleCategory = wire?.TitleCategory ?? 0,
					TitleKey = wire?.TitleKey ?? new ProtoDecryptionKey(),
					MapRefId = wire?.MapRefId ?? 0,
					MapCategory = wire?.MapCategory ?? 0,
					MapKey = wire?.MapKey ?? new ProtoDecryptionKey(),
					RegionRefId = wire?.RegionRefId ?? 0,
					RegionCategory = wire?.RegionCategory ?? 0,
					RegionKey = wire?.RegionKey ?? new ProtoDecryptionKey(),
					// A line no captured index block names keeps the SOLO default (#896), which is the column's own.
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
			// QuestDefinition.MapId is an FK to Maps, but a harvested quest can point its map marker at a playfield the
			// Maps catalog doesn't have - the catalog is built from captured MapChange packets, so a map nobody visited
			// in a capture is simply absent (e.g. 1000004, referenced by four Quest_c journal records). These used to be
			// SKIPPED, which threw away real journal content to satisfy an FK. A placeholder Map row carries the id and
			// nothing else; the maps module fills in the name if that playfield is ever captured.
			//
			// A NULL MapId needs none of this: it means the quest has no map marker at all, which is every quest that
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
				// Unreachable in practice now that every referenced map gets a placeholder above and a mapless quest is
				// NULL rather than 0 - kept as a guard so a future caller that hands us an unplaceholdered map fails
				// loudly on one row instead of aborting the whole import on the FK.
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

				// The area's ACGHash, off the captured journal (#544). Only overwrite when the harvest HAS one: the
				// quest index seeds it by hand for two quests, and an area no journal was captured for must not lose it.
				if (def.IndexMagic != 0)
					existing.IndexMagic = def.IndexMagic;

				// The series HEAD off the captured Quest_c record (#566), same rule: only overwrite when the harvest has
				// one. NB the other Snapshot* scalars deliberately do NOT refresh here - they land only when a row is
				// first added - so a column added later reaches an existing row through a line like this one or not at
				// all. That is why this one is here and why adding the column alone left all 108 rows at 0.
				if (def.ChainHeadQuestId != 0)
					existing.ChainHeadQuestId = def.ChainHeadQuestId;

				// The MONEY reward off the same record (@+0x20), same rule. 21 of the 99 captured quests carry one;
				// it was emitted as a constant 0 for years under the name `Accept`, so every value was discarded.
				if (def.MoneyReward != 0)
					existing.MoneyReward = def.MoneyReward;

				// ⛔ And the XP and the second slot pair (#893, found while measuring #859). These three were left to the
				// "lands only when the row is first added" rule above, and it cost a finding: quest 10830 is captured
				// twice with XpReward 715 / FieldF0 630 / FieldF8 60 on the wire and shipped 0 / 0 / 0, because its row
				// had been added by the completed-journal harvest first. #820 and #859 then measured "XpReward is zero
				// on every quest of the captured chain" against a database that had never received it. Same rule as
				// the money: only when the harvest has one, so a capture that carries none cannot zero another's.
				if (def.XpReward != 0)
					existing.XpReward = def.XpReward;
				if (def.FieldF0 != 0)
					existing.FieldF0 = def.FieldF0;
				if (def.FieldF8 != 0)
					existing.FieldF8 = def.FieldF8;
				// The record's presence and its structural words travel together: a row that now has a captured Quest_c
				// record is snapshot-eligible, and the flag/level words are the record's.
				if (def.HasSnapshotRecord)
				{
					existing.HasSnapshotRecord = true;
					existing.QuestLineFlags = def.QuestLineFlags;
					existing.Field118 = def.Field118;
					existing.FieldEc = def.FieldEc;
				}

				db.QuestTextRefs.RemoveRange(existing.TextRefs);
				existing.TextRefs = def.TextRefs;

				// Merge the harvested PRIMARY goal by GoalId into the existing rows WITHOUT clobbering the server-side-only
				// fields (TargetName/GoalType/QuestItemId) a prior Seed* set - only the wire fields refresh.
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
						// A goal the snapshot supplied in full stays full: Partial only ever CLEARS on an existing row,
						// so re-harvesting a quest whose secondary goal is now fully known upgrades it, and never the
						// other way round.
						g.Partial = false;
					}

					g.ObjectiveRefId = hg.ObjectiveRefId;
					g.ObjectiveCategory = hg.ObjectiveCategory;
					g.ObjectiveKey = hg.ObjectiveKey;

					// The goal's KIND TAG and required amount (#548). A full-update carries both for every goal - the
					// same values the snapshot path harvests where both exist (34 of 34 agree) - so refreshing them
					// here is the same "the capture is the truth source for every wire field" rule the goals module
					// applies, and it is what stops a full-update-only goal reading as "kill one unnamed thing".
					g.Separator = hg.Separator;
					g.RequiredCount = hg.RequiredCount;

					// The reserved (marker, 4CC-tag) pairs [1..6], lifted off the full-update body by the codec (#537). The same
					// words the snapshot path harvests, so a goal seen by both gets the same value from either - and a
					// goal seen ONLY in a full-update stops carrying zeros. Refreshed like the other wire fields.
					g.Extra1 = hg.Extra1;
					g.Extra2 = hg.Extra2;
					g.Extra3 = hg.Extra3;
					g.Extra4 = hg.Extra4;
					g.Extra5 = hg.Extra5;
					g.Extra6 = hg.Extra6;

					// The wire step ordinal and the scalar echoes the full-update carries for every goal (#866) -
					// the same values the snapshot path harvests where both exist, so a nonzero reading refreshes
					// and a zero one never clobbers what a snapshot supplied.
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

					// The TRAILING text record - fill-if-unset, for the same variant-spelling reason the extractor
					// documents (a snapshot row stores its own F0 variant's spelling; the byte-exact region
					// regeneration depends on it staying).
					if (g.TrailingCategory == 0 && hg.TrailingCategory != 0)
					{
						g.TrailingRefId = hg.TrailingRefId;
						g.TrailingCategory = hg.TrailingCategory;
						g.TrailingKey = hg.TrailingKey;
						g.TrailingLocalized = hg.TrailingLocalized;
						g.TrailingFlags = hg.TrailingFlags;
						g.TrailingToken = hg.TrailingToken;
					}

					// EVERY goal carries its own map pin (#540). "The marker block describes the primary goal only" was
					// an artefact of the old byte scan, which could see just the one block sitting before the first
					// objective record - so every secondary goal was persisted with no pin at all. A goal the wire pins
					// nowhere (quest 19314's twelve read 0/0/0) keeps whatever a snapshot already gave it.
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

		// Marks every quest whose @609 objective region, rebuilt from the rows just imported, reproduces the captured
		// bytes exactly. Only those may ride the login snapshot - the region still has undecoded fields, and shipping
		// one we cannot reproduce asserts the client (statlist.cpp:134).
		// Flags each quest whose @609 objective region we can rebuild EXACTLY as retail sent it - the gate that decides
		// what may ride the login snapshot, because a region the client cannot parse asserts it (statlist.cpp:134).
		//
		// Compared LIKE WITH LIKE: only against the captures whose record carries the same `XpReward` variant as the
		// record we would ship for that quest (see ObjectiveGoalExtractor.CapturedRegions for the evidence that F0
		// selects the region's spelling). Comparing against every capture regardless of variant failed a quest for
		// being a faithful rendering of the variant we DON'T emit: quest 10238 - the beach starter every new character
		// holds - rebuilds byte-exact in all 13 of its F0=150 captures and differs by 21 bytes from the 7 F0=175 ones,
		// which cost it the flag and left the login journal empty (#389).
		//
		// The safety property is unchanged and is the one that matters: the exact bytes we ship are bytes retail sent.
		// A quest with NO capture of our variant stays unflagged - unverifiable is not verified.
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
					// The goal's SolvedTimes is the CAPTURED CHARACTER's progress, not quest data (#550) - so it is
					// not on the row and cannot be rebuilt from one. Feeding this capture's own reading in keeps the
					// comparison honest: every OTHER word still has to match exactly, and a region that only
					// reproduces because progress was ignored would still fail.
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

				// Two conditions, and both matter:
				//   * at least ONE capture reproduces      - the bytes we ship are bytes retail sent, not a near-miss
				//     that happens to have no same-variant capture to contradict it;
				//   * every capture of OUR variant reproduces - no counter-example among the records we actually emit.
				// A quest whose region does not depend on the variant satisfies these exactly as it did under the old
				// all-captures rule (its same-variant set is a subset of a set that all matches), so nothing that was
				// verified before is weakened; a quest that only matches SOMEONE ELSE's variant still fails the second.
				def.ObjectiveRegionVerified = captures.Any(Reproduces) && captures.Where(c => c.Variant == def.FieldF0).All(Reproduces);

				if (def.ObjectiveRegionVerified)
					verified++;
			}

			await db.SaveChangesAsync();
			return (verified, total);
		}

		// DERIVE a Quest_c record for a quest that has everything the login snapshot needs EXCEPT the record itself
		// (#389). Run AFTER VerifyObjectiveRegions, because the objective region's verdict is the gate it reads.
		//
		// The candidates are quests whose region provably rebuilds, whose line carries its 4CC, and which have all
		// five header text refs and a level - i.e. every part of the snapshot portion is already proven EXCEPT the
		// dozen record scalars, which no capture ever carried for them. Seventeen quests qualify today.
		//
		// ⚠️ This SYNTHESISES wire data, which SnapshotTails.IsSnapshotEligible warns against ("never by flipping a
		// quest's flag by hand"). What makes it defensible is that the record's SHAPE is fixed and only its VALUES
		// are invented, and every invented value was proven harmless IN-CLIENT by sweeping it on a live login:
		//   * @+0x14 takes five observed values and the client renders all five without complaint (only the `Share`
		//     button changes, on bit 4) - so an inherited or defaulted flag word is cosmetic, not structural;
		//   * @+0x28 is the XP the journal prints, so 0 renders "XP: 0" - wrong, but only wrong on screen;
		//   * @+0xf0 / @+0xf8 changed nothing visible at any value tried.
		// The scalars that ARE structural are the constants: FieldEc 1, Field114 0, Field11c 0, and the chain head,
		// which QuestcRecords.ForQuest already falls back to the quest's own id for.
		//
		// A quest with NO captured sibling in its line gets flags 2 - the modal captured value (52 of 99) and the one
		// with bit 4 CLEAR, i.e. shareable. That is the permissive choice on the only bit whose meaning is known.
		public static async Task<int> SeedDerivedSnapshotRecords(DatabaseContext db)
		{
			ArgumentNullException.ThrowIfNull(db);

			// The flag word is a property of the LINE, so a captured sibling states it for the whole line.
			var lineFlags = await db
				.QuestDefinitions.Where(q => q.HasSnapshotRecord && q.QuestLineFlags != 0)
				.GroupBy(q => q.QuestLineId)
				.Select(g => new { Line = g.Key, Flags = g.Max(q => q.QuestLineFlags) })
				.ToDictionaryAsync(x => x.Line, x => x.Flags);

			// Verified against a capture, OR buildable from the goal rows (#827) - the same widening
			// `IsSnapshotEligible` makes, and it has to be the same or the two disagree about which quests ship: the
			// gate would admit a quest this never gave a record to, and it would fall out of the snapshot anyway.
			// Buildability is evaluated below (it needs the goals loaded), so the query only narrows on what SQL can.
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
				// range and the only value we can derive rather than guess.
				def.Field118 = def.Level;
				def.HasSnapshotRecord = true;
				seeded++;
			}

			await db.SaveChangesAsync();
			return seeded;
		}

		// Mirrors SnapshotTails.HeaderSlots, which is private to the codec layer.
		private static readonly QuestTextSlot[] SnapshotHeaderSlots =
			[QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.QuestLineTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

		// The modal captured @+0x14 (52 of 99) and the one with bit 4 clear - the shareable, permissive default.
		private const uint DefaultQuestLineFlags = 2;
	}
}
