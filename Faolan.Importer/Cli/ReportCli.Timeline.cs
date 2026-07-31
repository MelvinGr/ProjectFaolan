using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;
using ProtoBuf;

namespace Faolan.Importer.Cli
{
	// `--report timeline` - ONE flat chronological account of a single capture: what the session did, in order,
	// with every id resolved from our own data.
	//
	// It replaces a Python script that re-derived the same wire grammar by hand (tools/capture/timeline.py, retired).
	// That is the whole reason this lives here: the offsets a timeline needs ARE the codecs, and a second
	// implementation of them drifts - the Python one shipped four wrong offsets (playfield, QuestIIR, PetIIR, the
	// item template) before they were caught by eye. Here the decode comes from WorldQuest / StatDamageCodec /
	// HateListCodec / AbilityGrantBody / the generated protobuf types, all of which golden tests already gate.
	//
	// Two things it can do that the script could not:
	//   - names come from the Opcodes.cs ENUMS, so they cannot go stale the way a tshark dump's `Method` column does
	//     (tshark_dumps_carry_stale_opcode_names) and there is no source-parsing regex to get wrong;
	//   - LDB text is decrypted INLINE from the key the wire itself carries, so dialogue and quest lines read as
	//     English in the timeline - including screens our own ConversationScreens table has never captured.
	//
	// It is deliberately NOT sectioned and has no brief/verbose modes: one event per line, always detailed. Reading a
	// session as it happened is the job, and an event and its consequence must not land under different headings.
	public static partial class ReportCli
	{
		// The service tier each port belongs to. 7010 is the only fixed one (UniverseAgent); the rest are handed to
		// the client in the preceding tier's payload, so these are the ports the corpus actually used.
		private static readonly Dictionary<int, string> TimelineTiers = new()
		{
			[7010] = "universe", [7011] = "playeragent", [7012] = "cs", [7001] = "chat", [7017] = "game"
		};

		// Per-frame traffic: movement, heartbeats, aggro polling, the stat/energy stream. Five of every six packets in
		// a gameplay capture, and none of them an event. Suppressed with a printed count - never silently.
		private static readonly HashSet<string> TimelineNoise =
		[
			"CharDCMoveIIR", "MoveTaskIIR", "RequestAggroStatus", "DispatchUpdateAggroStatus", "SetSprintInputState",
			"DispatchSetIsSprinting", "DispatchSyncCanSprint", "Ping", "DispatchPong", "UpdateGameServerStats",
			"DispatchGameServerStats", "StatIIR", "EnergyIIR", "RegenIIR", "DispatchSyncRegenDelayed",
			"UpdateQuestMarkerNPC", "DispatchSetQuestMarker"
		];

		private static readonly Regex RemoteFormat = new("<(remoteformat|localized)([^>]*)>", RegexOptions.Compiled);
		private static readonly Regex XmlAttr = new("(\\w+)=\"?([^\"\\s>]*)\"?", RegexOptions.Compiled);

		private static async Task Timeline(string dumpPath, string rdbDir)
		{
			if (!File.Exists(dumpPath))
			{
				Console.WriteLine($"timeline: no such dump: {dumpPath}");
				Console.WriteLine("  Pass --report-arg <capture>.tshark.json (dump a pcapng with tools/capture/tshark_dump.sh).");
				return;
			}

			var packets = TsharkCaptureReader.Read(dumpPath, true);
			if (packets.Count == 0)
			{
				Console.WriteLine($"timeline: {Path.GetFileName(dumpPath)} yielded no packets.");
				return;
			}

			await using var db = OpenDb();
			var look = await TimelineLookups.LoadAsync(db, rdbDir);
			var events = packets.Select(p => new TimelineEvent(p, look)).OrderBy(e => e.Ticks).ToList();
			var labels = ResolveEntities(events, look);

			var start = new DateTimeOffset(events[0].Ticks, TimeSpan.Zero);
			Console.WriteLine($"# {Path.GetFileName(dumpPath)}");
			Console.WriteLine($"# {start:u}   {(events[^1].Ticks - events[0].Ticks) / (double)TimeSpan.TicksPerSecond:F1}s   {packets.Count} packets");
			Console.WriteLine();

			var suppressed = new Dictionary<string, int>();
			foreach (var e in events)
			{
				if (TimelineNoise.Contains(e.Name))
				{
					suppressed[e.Name] = suppressed.GetValueOrDefault(e.Name) + 1;
					continue;
				}

				var seconds = (e.Ticks - events[0].Ticks) / (double)TimeSpan.TicksPerSecond;
				var detail = Describe(e, look, labels);
				var hex = Convert.ToHexString(e.Raw).ToLowerInvariant();
				if (hex.Length > 72)
					hex = hex[..72] + "\u2026";
				Console.WriteLine($"{seconds,8:F1} {e.Frame,7} {e.Tier,-11} {Dir(e.Direction)} {e.Name,-32} {detail}"
				                  + (hex.Length > 0 ? $"   [{hex}]" : ""));
			}

			TimelineFooter(events, look, suppressed);
		}

		private static string Dir(string direction)
		{
			return direction.Length > 0 ? direction[..1] : "?";
		}

		// The only trailing block. NOT a section of the narrative - it is the part of the capture our own data could
		// not name, which is the finding a timeline run exists to surface, and it is silent when there is nothing.
		private static void TimelineFooter(List<TimelineEvent> events, TimelineLookups look, Dictionary<string, int> suppressed)
		{
			Console.WriteLine();
			if (suppressed.Count > 0)
			{
				var top = string.Join(", ", suppressed.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} x{k.Value}"));
				Console.WriteLine($"# {suppressed.Values.Sum()} per-frame packets suppressed: {top}{(suppressed.Count > 6 ? ", \u2026" : "")}");
			}

			var unnamedIir = events.Where(e => e.Iir != 0 && !look.IirNames.ContainsKey(e.Iir))
				.Select(e => $"0x{e.Iir:x8}").Distinct().Order().ToList();
			if (unnamedIir.Count > 0)
				Console.WriteLine($"# IIR types not in Opcodes.cs: {string.Join(", ", unnamedIir)}");

			var unnamedMethod = events.Where(e => e.Iir == 0 && e.Name.StartsWith("0x", StringComparison.Ordinal))
				.Select(e => e.Name).Distinct().Order().ToList();
			if (unnamedMethod.Count > 0)
				Console.WriteLine($"# methods not in Opcodes.cs: {string.Join(", ", unnamedMethod)}");
		}

		// handle -> label. Entity handles are SESSION-VOLATILE, so they cannot be looked up; they are resolved through
		// the capture itself, by matching a spawn body's POSITION against NpcSpawns/WorldObjects on this playfield.
		//
		// ⚠️ Three classes are excluded rather than matched, or they take the name of whatever they stood near:
		// the player, summoned PETS (they follow the player - one matched a "Brutal Pirate" 22u away), and OTHER
		// PLAYERS (a character id is far above the entity-handle range; one standing inside 2u of a vendor came out
		// labelled "Trader"). A non-exact match prints its distance, because an AMBUSH spawns where it was triggered
		// rather than at its home point - the quarry Bat Demon appears 10u off its row, and that gap is evidence.
		private const uint CharacterIdFloor = 0x1000000;

		private static Dictionary<uint, string> ResolveEntities(List<TimelineEvent> events, TimelineLookups look)
		{
			var labels = new Dictionary<uint, string>();
			uint mapId = 0;
			foreach (var e in events)
			{
				switch (e.Name)
				{
					case "PlayfieldFIIR" when e.Raw.Length >= 38:
						mapId = e.Raw.ReadU32Be(34);
						break;
					case "FullPlayerCharacterIIR" when e.Raw.Length >= 16:
						labels[e.Raw.ReadU32Be(12)] = "the player";
						break;
					case "PetIIR" when e.Raw.Length >= 29:
						labels[e.Raw.ReadU32Be(25)] = "pet";
						break;
				}
			}

			var npcs = look.NpcSpawns.GetValueOrDefault(mapId) ?? [];
			var objects = look.WorldObjects.GetValueOrDefault(mapId) ?? [];
			foreach (var e in events)
			{
				var isChar = e.Name is "SimpleCharFullUpdateIIR";
				if (!isChar && e.Name is not ("SimpleDynelFullUpdateIIR" or "DoorFullUpdateIIR" or "LootBagFullUpdateIIR"
					    or "TombStoneFullUpdateIIR" or "ClimbingDynelFullUpdateIIR"))
					continue;
				if (e.Raw.Length < 16)
					continue;

				var handle = e.Raw.ReadU32Be(12);
				if (labels.ContainsKey(handle))
					continue;
				if (handle >= CharacterIdFloor)
				{
					labels[handle] = "another player";
					continue;
				}

				var position = SpawnPosition(e.Raw);
				var table = isChar ? npcs : objects;
				if (position == null || table.Count == 0)
					continue;

				var (x, y, z) = position.Value;
				var best = table.MinBy(s => (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) + (s.Z - z) * (s.Z - z));
				var d = Math.Sqrt((best.X - x) * (best.X - x) + (best.Y - y) * (best.Y - y) + (best.Z - z) * (best.Z - z));
				if (!string.IsNullOrEmpty(best.Name) && d <= 25)
					labels[handle] = d < 2 ? best.Name : $"{best.Name} (+{d:F0}u)";
			}

			return labels;
		}

		// The spawn position, at the SAME named offsets the spawn builder writes and NpcSpawnExtractor reads
		// (SpawnPacketBuilder.Pos*Offset) - little-endian floats. Sharing the constants is the point: a timeline that
		// re-derived them would be one more place for them to drift.
		private static (float X, float Y, float Z)? SpawnPosition(byte[] raw)
		{
			return raw.Length < SpawnPacketBuilder.PosZOffset + 4
				? null
				: (raw.ReadF32Le(SpawnPacketBuilder.PosXOffset), raw.ReadF32Le(SpawnPacketBuilder.PosYOffset),
					raw.ReadF32Le(SpawnPacketBuilder.PosZOffset));
		}

		private static string Ent(Dictionary<uint, string> labels, uint handle)
		{
			return labels.TryGetValue(handle, out var name) ? $"{name} 0x{handle:x}" : $"0x{handle:x}";
		}

		// One line's worth of meaning. Every branch goes through a CODEC where one exists - that is the point of the
		// report living here rather than in a script - and states the id it could not resolve where one does not.
		//
		// A codec's Parse is written for the bodies its subsystem SENDS and may throw on a shape a capture happens to
		// carry (HateListCodec does, on a shorter body than it models). A diagnostic that reads arbitrary retail
		// traffic must not die on one message: the line degrades to its raw hex, which is still the evidence, and the
		// run continues. Swallowing it here is deliberate and is NOT a licence for a codec to be wrong - the raw hex
		// on the line is exactly what shows a decode is missing.
		private static string Describe(TimelineEvent e, TimelineLookups look, Dictionary<uint, string> labels)
		{
			try
			{
				return DescribeCore(e, look, labels);
			}
			catch (Exception ex)
			{
				return $"(undecoded: {ex.GetType().Name})";
			}
		}

		private static string DescribeCore(TimelineEvent e, TimelineLookups look, Dictionary<uint, string> labels)
		{
			// Every IIR codec now takes the CONTENT (from the sub-opcode on) - #530 rule 2 moved the carrier's u32
			// length out of the codecs and onto the sender, which retired the split where half of them read the length
			// first. `raw` is still the framed bytes, for the branches that have no codec yet.
			var raw = e.Raw;
			var content = e.Content;
			switch (e.Name)
			{
				case "InitiateAuthentication" when raw.Length > 2:
					return $"account \"{Encoding.Latin1.GetString(raw, 2, Math.Min(raw.ReadU16Be(0), raw.Length - 2))}\"";
				case "PlayfieldFIIR" when raw.Length >= 38:
				{
					var pf = raw.ReadU32Be(34);
					return $"playfield {pf}" + (look.Maps.TryGetValue(pf, out var m) ? $" \"{m}\"" : "");
				}
				case "FullPlayerCharacterIIR":
					return $"login snapshot, {raw.Length} B";
				case "DispatchUpdateBindLocation" when raw.Length >= 4:
					return $"bind playfield {raw.ReadU32Be(0)}";
				case "QuestGoalIIR":
				{
					var goal = QuestGoalCodec.Parse(content);
					return $"quest {goal.QuestId} goal {goal.GoalId} -> {goal.Update}";
				}
				case "QuestJournalIIR":
					return Journal(QuestJournalCodec.Parse(content), look);
				case "QuestIIR":
				{
					var quest = QuestCodec.Parse(content);
					return $"quest {quest.QuestId} state {quest.State}"
					       + quest.State switch { QuestCodec.StateActive => " (active)", QuestCodec.StateCompleted => " (completed)", _ => "" };
				}
				case "DispatchAddSpecialAbility":
				{
					var ability = AddSpecialAbilityCodec.Parse(content);
					return $"ability {ability.Id} slot {ability.Slot}"
					       + (look.Abilities.TryGetValue(ability.Id, out var an) ? $" = {an}" : "");
				}
				case "PerformSpecialAction" when raw.Length >= 4:
				{
					var slot = raw.ReadU32Be(0);
					return $"use ability slot {slot}" + (look.AbilityBySlot.TryGetValue(slot, out var n) ? $" = {n}" : "");
				}
				case "MagicBuffIIR":
				{
					// applyFrom is the CASTER and applyTo is who the buff lands on - the carrier handle is the TARGET.
					// This line had them the other way round until #530 wave 4 merged the three MagicBuffIIR emitters
					// and settled which endpoint is which (the NPC-cast golden pins it: applyFrom = the caster's spawn).
					var buff = MagicBuffCodec.Parse(content);
					var wireId = MagicBuffIds.WireIdOf(buff.SpellBaseId, buff.Form);
					var name = look.Spells.TryGetValue(buff.SpellBaseId, out var sn) ? sn : $"spell {buff.SpellBaseId}";
					var at = buff.ApplyTo != 0 && buff.ApplyTo != buff.ApplyFrom ? $" -> {Ent(labels, buff.ApplyTo)}" : "";
					var end = buff.Form == MagicBuffIds.FormEnd ? " (end)" : "";
					return $"{Ent(labels, buff.ApplyFrom)} buff {name}{at}{end} [0x{wireId:x}]";
				}
				case "LookAtIIR":
				{
					var look2 = LookAtCodec.Parse(content);
					return $"{Ent(labels, look2.Subject)} " + (look2.Target != 0 ? $"selects {Ent(labels, look2.Target)}" : "deselects");
				}
				case "StatDamageIIR":
					return StatDelta(content, look, labels);
				case "DamageResultIIR":
				{
					var hit = DamageResultCodec.Parse(content);
					return $"{Ent(labels, hit.Target)} <- {Ent(labels, hit.Attacker)}, "
					       + $"{hit.Damage} ({hit.Damage / 100.0:F2} shown), school {(uint)hit.School}";
				}
				case "ChainKillIIR":
					return $"chain {ChainKillCodec.Parse(content).Count}";
				case "HateListUpdateIIR":
				{
					var hate = HateListUpdateCodec.Parse(content);
					return $"{Ent(labels, hate.Handle)} -> {Ent(labels, hate.Target)}";
				}
				case "CombatShieldIIR":
					return $"shield {CombatShieldCodec.Parse(content).Value}";
				case "PetIIR":
				{
					var pet = PetCodec.Parse(content);
					return $"{Ent(labels, pet.Pet)} " + (pet.Mode == PetCodec.ModeRemove ? "dismissed" : "summoned");
				}
				case "N3ToClientFadeIIR" when raw.Length >= 16:
					return $"despawn {Ent(labels, raw.ReadU32Be(12))}";
				case "N3ToClientQuitIIR" when raw.Length >= 16:
					return $"quit {Ent(labels, raw.ReadU32Be(12))}";
				case "SimpleDynelVisualChangeIIR" when raw.Length > 20:
					return $"{Ent(labels, raw.ReadU32Be(12))} visual state 0x{raw[20]:x2}";
				case "DispatchAddItem":
					return AddedItem(raw, look);
				case "DispatchUpdateCash" when raw.Length >= 12:
					return $"cash {raw.ReadU32Be(4)} (+{raw.ReadU32Be(8)})";
				case "DispatchOpenLootBag" or "DispatchCloseLootBag" or "TryLootAll" or "TryLootCash" when raw.Length >= 8:
					return $"bag {Ent(labels, raw.ReadU32Be(4))}";
				case "SimpleCharFullUpdateIIR" or "SimpleDynelFullUpdateIIR" or "DoorFullUpdateIIR"
					or "LootBagFullUpdateIIR" or "TombStoneFullUpdateIIR" or "ClimbingDynelFullUpdateIIR" when raw.Length >= 16:
				{
					var at = SpawnPosition(raw) is { } p ? $" at ({p.X:F1}, {p.Y:F1}, {p.Z:F1})" : "";
					return $"spawn {Ent(labels, raw.ReadU32Be(12))}{at}";
				}
				default:
					return look.Text(raw);
			}
		}

		// The whole J-panel journal, through QuestJournalCodec.
		//
		// ⚠️ This line used to fall through to the generic `look.Text(raw)`, which matches the FIRST <remoteformat> tag
		// in a body and stops - so all four journals in the necromancer play2 capture printed the same thing, the
		// container title "[Destiny 01] Shipwrecked", while 10 to 16 records per message were dropped in silence
		// (#557). A journal carries three header strings plus TWO per quest (title cat 50301, sub-line cat 50303) plus
		// two more for the highlighted entry, and it is re-sent whole on every change, so it grows as the chain
		// advances: 11 records at frame 875, 17 by frame 6115.
		//
		// The sub-line is deliberately NOT printed. It is the multi-sentence objective blurb, and six of them would
		// bury the line the reader came for; the title is what identifies a quest. `>` marks the entry the journal
		// highlights - the quest just accepted or advanced.
		private static string Journal(QuestJournalIIR journal, TimelineLookups look)
		{
			var areas = new List<QuestJournalArea>(1 + journal.MoreAreas.Count) { journal.Area };
			areas.AddRange(journal.MoreAreas);

			var parts = new List<string>();
			foreach (var area in areas)
			{
				var where = string.Join(", ", new[] { look.Line(area.MapName), look.Line(area.RegionName) }
					.Where(s => s.Length > 0).Distinct());
				var entries = area.Entries.Select(e =>
					(e.QuestId == journal.Current.QuestId ? ">" : "") + $"{e.QuestId} \"{look.Line(e.Title)}\"");
				parts.Add($"\"{look.Line(area.AreaTitle)}\"" + (where.Length > 0 ? $" ({where})" : "")
				          + $" {area.Entries.Count} quest(s): " + string.Join(", ", entries));
			}

			return string.Join(" | ", parts);
		}

		// The stat delta and its cause, through StatDamageCodec - now by NAME rather than by index into a bag of
		// seventeen words (#530 wave 4). Like combat damage the delta is display-scaled x100, which makes a
		// damage-over-time tick read -7.07.
		private static string StatDelta(byte[] content, TimelineLookups look, Dictionary<uint, string> labels)
		{
			var msg = StatDamageCodec.Parse(content);
			var stat = look.Stats.TryGetValue(msg.StatId, out var sn) ? sn : $"stat {msg.StatId}";
			var cause = msg.SpellBaseId == 0
				? ""
				: look.Spells.TryGetValue(msg.SpellBaseId, out var cn) ? $" via {cn}" : $" via spell {msg.SpellBaseId}";
			return $"{Ent(labels, msg.Handle)} {stat} {msg.Delta / 100.0:+0.00;-0.00} from {Ent(labels, msg.Caster)}{cause}";
		}

		// The granted item, through the GENERATED protobuf rather than a hand walk: item_data.template1 is the
		// template. The stat list is deliberately not consulted - its stat 23 is a Static Instance id, the tempting
		// wrong answer, and fields 1-3 of item_data are the LDB name-key quads, which is what a raw tag scan finds.
		//
		// The body is AddItemBody.WriteAddItemBody's: [container][charId][itemPos][u32 len][entry][feedback]. The
		// entry must be sliced to its DECLARED length - a trailing feedback word rides after it, and handing those
		// extra bytes to the deserializer is a ProtoException, not a tolerated overrun.
		private static string AddedItem(byte[] raw, TimelineLookups look)
		{
			if (raw.Length < 16)
				return "";
			try
			{
				var length = (int)raw.ReadU32Be(12);
				if (length <= 0 || 16 + length > raw.Length)
					return "";

				var entry = Serializer.Deserialize<ProtoInventoryGameEntry>(new ReadOnlyMemory<byte>(raw, 16, length));
				var template = entry?.ItemData?.Template1 ?? 0;
				var slot = raw.ReadU32Be(8);
				return $"slot {slot}, template {template}"
				       + (look.Items.TryGetValue(template, out var n) ? $" = \"{n}\"" : "");
			}
			catch (Exception ex)
			{
				return $"(item undecoded: {ex.GetType().Name})";
			}
		}
	}
}
