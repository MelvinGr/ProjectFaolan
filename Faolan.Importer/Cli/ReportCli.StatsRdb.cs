using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Modules;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// `CHARACTER_HP_MANA_STAMINA` (RDB type 1000511) - the client's per-class BASE vitals curve, decoded from its own
		// parser (`HitsManaAndStamina_c::ReadFromRDB`, whose assert states the record size: 14 x int32).
		//
		//   [12-byte header][i32 version][i32 count][count x 14 x i32]
		//
		// Each record is keyed [race][class]; fields 2..4 are the L1 BASE health/mana/stamina (/100) and 5..7 their
		// per-level growth (/1000), so `base/100 + (level-1) * growth/1000` reproduces the captured snapshot sheet's stat
		// 1 / 507 / 505 exactly for every captured character. See docs/rdb/client-tweak-table.md. Gitea #3.
		// #444: the class attribute BASE. It lives in RDB type 1000020 - the ITEM-template type - as PLAINTEXT templates
		// (no TEA key), one per class per level, each NAMING itself "<Class> Class Feat <NNN>". Their stat records are
		// read by the ordinary item parser, so this only became readable once that scan went byte-granular (#441).
		//
		// ⚠️ What this report does NOT establish is the id -> LEVEL mapping. `templateId = base + (level - 1)` is
		// verified against all four client-observed anchors (Assassin L23 = 3074028+22, Herald of Xotli L24 =
		// 3130632+23, Necromancer L80 = 1173784+79, Demonologist L80 = 1126680+79), but the per-class BASE cannot be
		// recovered structurally: entries sharing the class name from other feat series are interleaved inside the id
		// range, so neither "lowest id" nor "longest monotonic contiguous window" finds it (the latter truncates the
		// Assassin's real 80 to 70). The name's <NNN> is not the level either - three consecutive Assassin templates
		// are all "Class Feat 021" - and no property field carries the level (field 33 is a per-CLASS code: Assassin
		// 23, Herald of Xotli 30, Necromancer 26, Demonologist 27).
		//
		// So a class's base needs ONE client observation to anchor it: log a character of that class in with the
		// ClientInterface + GameCode/ItemData channels on and read the template id it loads. This report gives the
		// candidate set and their values; the anchor gives the offset.
		private static void ClassAttributeRuns(string rdbDir, string? outDir)
		{
			using var rdb = OpenRdb(rdbDir);
			var csv = new StringBuilder("class,block,level,templateId,statId,amount\n");
			var byClass = ClassAttributeBaseModule.ScanRuns(rdb);

			foreach (var (cls, candidates) in byClass.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
			{
				var byLevel = ClassAttributeBaseModule.LevelsOf(candidates);
				var checks = ClassAttributeBaseModule.ClientObservations.Where(o => o.Class == cls)
					.Select(o => byLevel.GetValueOrDefault(o.Level).Id == o.TemplateId
						? $"L{o.Level}={o.TemplateId} ✓"
						: $"⚠ L{o.Level} resolved to {byLevel.GetValueOrDefault(o.Level).Id}, client observed {o.TemplateId}");
				Console.WriteLine($"{cls,-16} {byLevel.Count,3}/{ClassAttributeBaseModule.MaxLevel} levels from {candidates.Count,3} candidates   " +
								  string.Join("  ", checks));

				// Every candidate, by the block its NAME declares - so a block carrying more than its ten levels (a
				// stray from another feat series sharing the class name) is visible rather than silently truncated.
				foreach (var block in candidates.GroupBy(t => t.BlockFirstLevel).OrderBy(g => g.Key))
				{
					var ordered = block.OrderBy(t => t.AttributeSum).ThenBy(t => t.Id).ToList();
					Console.WriteLine($"    L{block.Key,2}-{block.Key + 9,2}  {ordered.Count,2} candidates  " +
									  $"{string.Join(" ", ordered.Select(t => t.Id))}");
					if (ordered.Count != 10)
						foreach (var t in ordered)
							Console.WriteLine($"        {t.Id}  [{Fmt(t.Mods)}]");
				}

				foreach (var t in candidates.OrderBy(x => x.BlockFirstLevel).ThenBy(x => x.AttributeSum))
					foreach (var mod in t.Mods)
						csv.Append($"{cls},{t.BlockFirstLevel},{byLevel.FirstOrDefault(kv => kv.Value.Id == t.Id).Key},{t.Id},{mod.StatId},{mod.WireAmount}\n");
			}

			if (outDir == null)
				return;

			Directory.CreateDirectory(outDir);
			var path = Path.Combine(outDir, "class-attribute-runs.csv");
			File.WriteAllText(path, csv.ToString());
			Console.WriteLine($"\nwrote {path}");
		}

		private static string Fmt(List<ItemTemplateStatModifiers.Modifier> mods)
		{
			return string.Join(" ", mods.Select(x => $"{(CharacterStatId)x.StatId}={x.WireAmount}"));
		}

		private static void RdbClassVitals(string rdbDir, string? filter)
		{
			const uint tableType = 1000511;
			using var rdb = OpenRdb(rdbDir);
			foreach (var entry in rdb.EntriesOfType(tableType))
			{
				var blob = rdb.ReadDecompressed(entry);
				if (blob.Length < 20)
				{
					Console.WriteLine($"entry {entry.Id}: {blob.Length} B - too short");
					continue;
				}

				var version = blob.ReadI32Le(12);
				var count = blob.ReadI32Le(16);
				var expected = 20 + count * 56;
				Console.WriteLine($"type={tableType} id={entry.Id}: {blob.Length} B, version={version}, {count} record(s)" +
								  (blob.Length == expected
									  ? " (size matches 20 + count x 56 exactly)"
									  : $" ⚠ expected {expected} B"));
				Console.WriteLine($"  {"race",4} {"class",5} {"hpBase",7} {"hpGrow",7} {"manaBase",9} {"manaGrow",9} {"stamBase",9} {"stamGrow",9}   trailing");
				for (var i = 0; i < count && 20 + (i + 1) * 56 <= blob.Length; i++)
				{
					var r = new int[14];
					for (var k = 0; k < 14; k++)
						r[k] = blob.ReadI32Le(20 + i * 56 + k * 4);

					var cls = ((ConanClass)r[1]).ToString();
					if (filter != null && !cls.Contains(filter, StringComparison.OrdinalIgnoreCase) && !r[1].ToString().Contains(filter))
						continue;

					Console.WriteLine($"  {r[0],4} {cls,5} {r[2] / 100.0,7:0.##} {r[5] / 1000.0,7:0.###} {r[3] / 100.0,9:0.##} {r[6] / 1000.0,9:0.###} " +
									  $"{r[4] / 100.0,9:0.##} {r[7] / 1000.0,9:0.###}   {string.Join(",", r[8..])}");
				}
			}
		}

		// Gitea #441: every keyed item's TEMPLATE-SCALING inputs next to its EXPLICIT [2][9] stat records, as CSV, so the
		// scaling formula can be FITTED on the items that carry both and then applied to the ones that carry none.
		//
		// Why this exists: only ~24% of items carry explicit stat records. The rest are "template-scaled" - a high-level
		// piece like Talons of Dark Terror has a rich in-client tooltip but ZERO records, so its armor/health never
		// reaches the server and a geared character's bars cap far below what the client draws (#3). The scaling inputs
		// are the property-table fields every one of them DOES carry: 54 = item level, 499 = quality tier, 496 = the
		// per-item budget that tracks quality (3250/3500 at tier 3, 4000/5000 at tier 4), 360, 41, 21, plus the search
		// CATEGORY (30000 armor / 30020 neck / 30023 cloak) which stands in for the slot.
		//
		// Writing BOTH sides in one row is the point: an item with an explicit Armor/HealthMax record is ground truth for
		// what the same inputs must produce, and there is no need to ask the client for a tooltip to fit against.
		// Every stat id the item [2][9] stat-modifier region carries, with how many templates carry it and its value
		// range - the whole region, NOT just RdbItemImporter's whitelist. The question it answers is the one that
		// keeps coming up when a consumer lands: "is this rating actually ON any item, or would importing it be dead
		// weight?" (#401 asked it of BonusArmor/BonusProtection.)
		private static void RegionStatCensus(string rdbDir)
		{
			using var db = OpenDb();
			var items = db.Items.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0).OrderBy(i => i.Id).ToList();
			using var rdb = OpenRdb(rdbDir);

			var counts = new Dictionary<uint, (int Items, long Min, long Max)>();
			var scanned = 0;
			foreach (var item in items)
			{
				var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };
				byte[]? template = null;
				foreach (var type in new uint[] { 1000020, 1000620 })
				{
					if (!rdb.TryGet(type, item.Id, out var e) || e.Size == 0 || !rdb.CanRead(e))
						continue;
					try
					{
						template = rdb.ReadDecrypted(e, key);
						break;
					}
					catch
					{
						// a wrong/stale key - skipped, like the sibling reports
					}
				}

				if (template is not { Length: >= 16 })
					continue;

				scanned++;
				foreach (var m in ItemTemplateStatModifiers.Parse(template.AsSpan(12)))
				{
					var amount = (long)m.WireAmount;
					counts[m.StatId] = counts.TryGetValue(m.StatId, out var prev)
						? (prev.Items + 1, Math.Min(prev.Min, amount), Math.Max(prev.Max, amount))
						: (1, amount, amount);
				}
			}

			Console.WriteLine($"region-stat-census: {scanned} templates read, {counts.Count} distinct stat ids");
			Console.WriteLine($"{"statId",-8} {"name",-32} {"items",7} {"min",12} {"max",12}");
			foreach (var (statId, row) in counts.OrderByDescending(k => k.Value.Items))
			{
				var name = Enum.IsDefined(typeof(CharacterStatId), statId)
					? ((CharacterStatId)statId).ToString()
					: "";
				Console.WriteLine($"{statId,-8} {name,-32} {row.Items,7} {row.Min,12} {row.Max,12}");
			}
		}

		private static void ItemStatScaling(string rdbDir, string? outPath)
		{
			using var db = OpenDb();
			var items = db.Items.Include(i => i.StatModifiers)
				.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0)
				.OrderBy(i => i.Id)
				.ToList();

			using var rdb = OpenRdb(rdbDir);
			uint[] fields = [54, 499, 496, 360, 41, 21, 644, 645, 74];
			CharacterStatId[] stats = [CharacterStatId.Armor, CharacterStatId.HealthMax, CharacterStatId.ManaMax, CharacterStatId.StaminaMax, CharacterStatId.Constitution];

			var rows = new List<string>
			{
				"id,name,category,quality,regionRecords," + string.Join(",", fields.Select(f => $"f{f}")) + "," + string.Join(",", stats.Select(s => $"explicit_{s}"))
			};
			int noEntry = 0, failed = 0, scanned = 0, withRegion = 0;
			foreach (var item in items)
			{
				var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };
				byte[]? template = null;
				foreach (var type in new uint[] { 1000020, 1000620 })
				{
					if (!rdb.TryGet(type, item.Id, out var e) || e.Size == 0 || !rdb.CanRead(e))
						continue;
					try
					{
						template = rdb.ReadDecrypted(e, key);
						break;
					}
					catch
					{
						// a wrong/stale key - counted below, not fatal to the sweep
					}
				}

				if (template == null)
				{
					noEntry++;
					continue;
				}

				if (template.Length < 16)
				{
					failed++;
					continue;
				}

				scanned++;
				var body = template.AsSpan(12);
				var region = ItemTemplateStatModifiers.Parse(body);
				if (region.Count > 0)
					withRegion++;

				var cells = new List<string>
				{
					item.Id.ToString(),
					"\"" + (item.Name ?? "").Replace("\"", "'") + "\"",
					item.SearchCategory.ToString(),
					item.Quality is { } q
						? ((uint)q).ToString()
						: "",
					region.Count.ToString()
				};
				foreach (var f in fields)
				{
					cells.Add(RdbItemImporter.TryReadPropertyField(body, f, out var v)
						? v.ToString()
						: "");
				}

				// The explicit side: the region record for each stat we care about, at RAW wire scale (the fit divides).
				cells.AddRange(stats.Select(s =>
				{
					var hit = region.FirstOrDefault(m => m.StatId == (uint)s);
					return hit.StatId == (uint)s
						? hit.WireAmount.ToString()
						: "";
				}));
				rows.Add(string.Join(",", cells));
			}

			var path = outPath ?? Path.Combine(Path.GetTempPath(), "item-stat-scaling.csv");
			File.WriteAllLines(path, rows);
			Console.WriteLine($"item-stat-scaling: {scanned} templates decrypted ({withRegion} with explicit records, {scanned - withRegion} template-scaled), {noEntry} without a readable/decryptable entry, {failed} too short.");
			Console.WriteLine($"wrote {path} ({rows.Count - 1} rows)");
		}

		// --report vitals-shape [--report-arg <capture-name-substring>]
		//
		// WHICH stat ids retail puts in a 0x96B8DC59 StatUpdateIIR, grouped by the RECORD SET the block carries and
		// by whose handle it is on (#586). We send all six vitals ids - HpCur/HpMax/StamCur/StamMax/ManaCur/ManaMax -
		// for every entity; the claim this measures is that retail sends only the three CURRENTS on a non-player
		// handle, and six only on the player's own.
		//
		// The player's handle is not labelled on the wire, so it is DERIVED rather than assumed: it is the handle the
		// capture's FullPlayerCharacter snapshot (0x737A6DF9) is about, which is the one entity a session has exactly
		// one of. A capture with no snapshot is reported separately rather than guessed at.
		private static void VitalsShape(IReadOnlyList<NamedCapture> captures, string? arg)
		{
			var byShape = new Dictionary<(bool Player, string Ids), int>();
			var examples = new Dictionary<(bool Player, string Ids), string>();
			var noSnapshot = 0;

			foreach (var c in captures)
			{
				if (!string.IsNullOrWhiteSpace(arg) && !c.Name.Contains(arg, StringComparison.OrdinalIgnoreCase))
					continue;

				// The session's own character handle, from its FullPlayerCharacter snapshot.
				uint? playerHandle = null;
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: FullPlayerCharacterCodec.Opcode } || p.Record.Length < 12)
						continue;

					// [type][entityClass][handle] - the same prologue StatUpdateIIR carries.
					playerHandle = p.Record.ReadU32Be(8);
					break;
				}

				if (playerHandle == null)
				{
					noSnapshot++;
					continue;
				}

				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: StatUpdateCodec.Opcode })
						continue;

					StatUpdateIIR update;
					try
					{
						update = StatUpdateCodec.Parse(p.Record);
					}
					catch (CodecParseException)
					{
						continue; // a body the codec cannot account for is not evidence about the id SET
					}

					var ids = string.Join(",", update.Stats.Records.Select(e => $"0x{(uint)e.StatId:X4}").OrderBy(x => x));
					var key = (update.EntityHandle == playerHandle, ids);
					byShape[key] = byShape.GetValueOrDefault(key) + 1;
					if (!examples.ContainsKey(key))
						examples[key] = c.Name;
				}
			}

			Console.WriteLine($"StatUpdateIIR (0x{StatUpdateCodec.Opcode:X8}) record sets, by whose handle they are on");
			if (noSnapshot > 0)
				Console.WriteLine($"  ({noSnapshot} capture(s) skipped - no FullPlayerCharacter snapshot, so no player handle to compare against)");
			Console.WriteLine();

			foreach (var player in new[] { true, false })
			{
				Console.WriteLine(player ? "--- the session's OWN character" : "--- every OTHER handle (NPCs, other players)");
				var rows = byShape.Where(kv => kv.Key.Player == player).OrderByDescending(kv => kv.Value).ToList();
				if (rows.Count == 0)
				{
					Console.WriteLine("    (none)");
					Console.WriteLine();
					continue;
				}

				foreach (var (key, n) in rows)
					Console.WriteLine($"    {n,7}  [{key.Ids}]   e.g. {examples[key][..Math.Min(46, examples[key].Length)]}");

				Console.WriteLine();
			}
		}
	}
}
