using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// RDB -> 3D-model asset graph (Gitea #275, Phase 0). With no --report-arg: a corpus summary (how many
		// MESHINDEXFILE manifests resolve to geometry, how their textures are split local vs CDN). With --report-arg
		// <meshIndexId>: print that model's graph (RenderMesh id, materials, texture refs) and, if --out <dir> is given,
		// extract its FCTX textures to DDS (+ --dump-mesh to also write the raw 1010001 geometry blob for the Phase 1 spike).
		private static void RdbAssets(string rdbDir, string? arg, string? outDir, bool dumpMesh)
		{
			using var rdb = OpenRdb(rdbDir);

			// Comma-separated ids => assemble them into one glb (character body + head + equipment).
			if (arg != null && arg.Contains(','))
			{
				var ids = arg
					.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(s => uint.TryParse(s, out var u)
						? u
						: 0u)
					.Where(u => u != 0)
					.ToList();
				var outPath = Path.Combine(outDir ?? ".", "assembled.glb");
				var res = RdbAssetExtractor.Assemble(rdb, ids, outPath);
				Console.WriteLine($"assembled {res.Used.Count}/{ids.Count} meshes: {res.Vertices} verts {res.Triangles} tris -> {outPath}");
				return;
			}

			if (uint.TryParse(arg, out var meshIndexId))
			{
				var result = RdbAssetExtractor.Extract(rdb, meshIndexId, outDir, dumpMesh);
				if (!result.Ok || result.Manifest is not { } m)
				{
					Console.WriteLine($"MESHINDEXFILE {meshIndexId} not found in RDB.");
					return;
				}

				Console.WriteLine($"MESHINDEXFILE {meshIndexId}");
				Console.WriteLine($"  source       : {m.Source}");
				Console.WriteLine($"  bounds       : {m.Bounds}");
				Console.WriteLine($"  renderMesh   : {Describe(m.RenderMesh)}{(m.RenderMeshDump != null ? $" -> {m.RenderMeshDump}" : "")}");
				if (m.Geometry is { } g)
					Console.WriteLine($"  geometry     : {g.Vertices} verts {g.Triangles} tris{(m.Glb != null ? $" -> {m.Glb}" : "")}");
				Console.WriteLine($"  collisionMesh: {Describe(m.CollisionMesh)}");
				Console.WriteLine($"  materials    : {m.Materials.Count}");
				foreach (var mat in m.Materials)
					Console.WriteLine($"    - {mat.Name}: [{string.Join(", ", mat.Textures)}]");
				Console.WriteLine($"  textures     : {m.Textures.Count}");
				foreach (var t in m.Textures)
					Console.WriteLine($"    - {t.Ref,-18} {t.Status}{(t.Output != null ? $" -> {t.Output}" : "")}");
				if (outDir != null)
					Console.WriteLine($"  written to   : {outDir}");
				return;
			}

			// Corpus summary.
			int manifests = 0, withGeometry = 0, geomLocal = 0, geomCdn = 0, texLocal = 0, texCdn = 0, texMissing = 0;
			foreach (var e in rdb.EntriesOfType(RdbAssetExtractor.MeshIndexFileType))
			{
				if (e.Size == 0 || !rdb.CanRead(e))
					continue;

				manifests++;
				MeshIndexFile mesh;
				try
				{
					mesh = MeshIndexFile.Parse(e.Id, rdb.ReadDecompressed(e));
				}
				catch
				{
					continue;
				}

				if (mesh.RenderMeshId is { } rmId)
				{
					withGeometry++;
					if (rdb.TryGet(RdbAssetExtractor.RenderMeshType, rmId, out var rm))
					{
						(rm.IsLocal
							? ref geomLocal
							: ref geomCdn)++;
					}
				}

				foreach (var t in mesh.UniqueTextures)
				{
					if (!rdb.TryGet(t.Type!.Value, t.Id, out var te))
						texMissing++;
					else if (te.IsLocal)
						texLocal++;
					else
						texCdn++;
				}
			}

			Console.WriteLine($"MESHINDEXFILE manifests (local) : {manifests}");
			Console.WriteLine($"  with a RenderMesh ref         : {withGeometry} (geometry local={geomLocal} cdn={geomCdn})");
			Console.WriteLine($"  texture refs: local={texLocal} cdn={texCdn} missing={texMissing}");
			Console.WriteLine("Pass --report-arg <meshIndexId> [--out <dir>] [--dump-mesh] to extract one model.");
		}

		// List all entries of a TYPE (--report-arg <type>) and dump each decompressed payload to --out as
		// <type>_<id>.bin for offline inspection. Used to crack new RDB record formats (e.g. CHARACTERCREATOR 1010700).
		private static void RdbDump(string rdbDir, string arg, string? outDir)
		{
			// `<type>` dumps every entry of that type; `<type>:<id>` dumps just one - which is the only usable form for
			// a huge type like 1000020 (98k item templates).
			var colon = arg.IndexOf(':');
			uint? onlyId = null;
			if (colon >= 0)
			{
				if (!uint.TryParse(arg[(colon + 1)..], out var parsedId))
				{
					Console.WriteLine($"rdb-dump: '{arg[(colon + 1)..]}' is not a numeric id (expected <type> or <type>:<id>)");
					return;
				}

				onlyId = parsedId;
				arg = arg[..colon];
			}

			if (!uint.TryParse(arg, out var type))
			{
				Console.WriteLine($"rdb-dump needs a numeric type in --report-arg, got '{arg}' (expected <type> or <type>:<id>)");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			if (outDir != null)
				Directory.CreateDirectory(outDir);

			foreach (var e in rdb.EntriesOfType(type).Where(rdb.CanRead))
			{
				if (onlyId != null && e.Id != onlyId)
					continue;

				var body = rdb.ReadDecompressed(e);
				if (body.Length == 0) // CDN miss
					continue;

				Console.WriteLine($"type={type} id={e.Id} decompressed={body.Length} B");
				if (outDir != null)
					File.WriteAllBytes(Path.Combine(outDir, $"{type}_{e.Id}.bin"), body);
			}
		}

		// Harvests the feat EFFECT element's kind -> width table empirically (#251), and CHECKS it against the width
		// the kind's key list predicts (#591).
		//
		// The element opens with a KIND tag (0xCF..) and its width VARIES with that kind, which is what defeats a
		// fixed-stride walk and left 3.1% of effect rows on stats the client cannot name. The width is now KNOWN
		// rather than fitted - an element is `8 + len(keyList)` words and the key lists come from the client
		// (GameActionTypes) - so what this measures is a CHECK on that, over shipped bytes.
		//
		// ⚠️ What it measures is an upper BOUND, not the width: in a record whose effect list holds exactly one
		// element, `recordLength - elementStart` also swallows anything the record carries AFTER the element list.
		// That is why the surplus dump matters - where the two disagree it has so far been trailing record content
		// (a list frame, a mesh name), not element bytes.
		// --report rdb-field-coverage [--report-arg <rdbType>]
		//
		// Every property-table field a record type carries, how many records carry it, and whether the importer
		// READS it. Written after #590, where a shipped field (the spell line, 128) turned out to be sitting in plain
		// sight in a table we already parsed - the question "what else is in here that we walk straight past?"
		// deserves a measurement rather than a guess.
		//
		// Defaults to the spell type (1040005); pass `<type>` for another, or `<type>:<field>[,<field>...]` to DRILL
		// into fields - each one's value distribution and named sample records, which is what turns "unread" into
		// "worth reading". Several fields drill in ONE pass over the type; a pass costs a full decompress of every
		// record (21,502 for spells), so drilling them one at a time is the same measurement for N times the wait.
		//
		// Append `x<field>` to CROSS-TABULATE the drilled fields against another one - `1040005:348x847` prints, for
		// each value of 847, the distribution of 348 among the records carrying both. That is the shape that answers
		// "does this field VARY WITH that one", which a pair of independent histograms cannot.
		//
		// ⚠️ It reads the ASCENDING property grammar. That is the established one for SPELL records; for 1000020 the
		// plaintext half is feats and the run is ascending there too, but an ITEM template's table strictly DESCENDS
		// (RdbItemImporter.TryReadPropertyField) and is not covered by this - so a 1000020 run measures the feats.
		//
		// With `--out <dir>` a drill also writes `rdb-<type>-field-<n>.tsv` - EVERY carrying record's id, name and
		// value, not the 14 samples. A histogram says what values exist; deciding whether an unread field is the
		// second source for a column we already fill needs the per-record join, and the 14-sample cap cannot do it
		// (#633).
		private static void RdbFieldCoverage(string rdbDir, string? arg, string? outDir)
		{
			var spec = (arg ?? "").Split(':', 2, StringSplitOptions.TrimEntries);
			var type = spec.Length > 0 && uint.TryParse(spec[0], out var t) ? t : SpellTemplateType;
			var drillSpec = spec.Length == 2 ? spec[1] : "";
			var againstSplit = drillSpec.Split('x', 2, StringSplitOptions.TrimEntries);
			var against = againstSplit.Length == 2 && uint.TryParse(againstSplit[1], out var a) ? a : 0u;
			var drills = againstSplit[0]
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(s => uint.TryParse(s, out var d) ? d : 0u)
				.Where(d => d != 0)
				.ToArray();
			// What each importer actually consumes today, so the report can say READ vs unread rather than just
			// enumerating. Keep in step with the importers; a field that moves here without moving there is exactly
			// the drift this report exists to surface. The SPELL half lives on SpellFieldsRead (shared with
			// `--report spell-tooltip`, which is the per-spell view of this census) - see its remarks for the drift
			// this arrangement is meant to make impossible.
			var read = type == SpellTemplateType
				? SpellFieldsRead
				: new Dictionary<uint, string> { [74] = "value", [104] = "bag", [499] = "quality", [972] = "primary attribute", [1331] = "resource bag" };

			using var rdb = OpenRdb(rdbDir);
			var records = 0;
			var counts = new Dictionary<uint, int>();
			var ranges = new Dictionary<uint, (uint Min, uint Max)>();
			// drilled[field][value] = how many records carry that value; crossed[field][(against, value)] likewise but
			// split by the -x- field's value on the SAME record.
			var drilled = drills.ToDictionary(f => f, _ => new Dictionary<uint, int>());
			var crossed = drills.ToDictionary(f => f, _ => new Dictionary<(uint Against, uint Value), int>());
			var samples = drills.ToDictionary(f => f, _ => new List<string>());
			// --out: every carrying record, not just the samples (id, name, value).
			var rows = outDir != null ? drills.ToDictionary(f => f, _ => new List<(uint Id, string Name, uint Value)>()) : null;

			foreach (var e in rdb.EntriesOfType(type))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != type)
					continue;

				records++;
				// The whole record's fields first: a cross-tab needs the -x- field's value on this record, and the
				// property run is ascending, so a field drilled BEFORE the one it is crossed against is not yet known
				// while walking. A repeated field is last-wins, matching the importers.
				var fields = new Dictionary<uint, uint>();
				foreach (var (field, value) in RdbSpellImporter.ReadIncreasingFields(body.AsSpan(12)))
				{
					counts[field] = counts.GetValueOrDefault(field) + 1;
					var (min, max) = ranges.TryGetValue(field, out var r) ? r : (uint.MaxValue, 0u);
					ranges[field] = (Math.Min(min, value), Math.Max(max, value));
					fields[field] = value;
				}

				foreach (var drill in drills)
				{
					if (!fields.TryGetValue(drill, out var value))
						continue;

					drilled[drill][value] = drilled[drill].GetValueOrDefault(value) + 1;
					if (against != 0 && fields.TryGetValue(against, out var againstValue))
					{
						var key = (againstValue, value);
						crossed[drill][key] = crossed[drill].GetValueOrDefault(key) + 1;
					}

					if (samples[drill].Count < 14 || rows != null)
					{
						var (name, _) = RdbFeatImporter.ReadNameAndDescription(body);
						rows?[drill].Add((e.Id, name, value));
						if (samples[drill].Count < 14)
						{
							var cross = against != 0 && fields.TryGetValue(against, out var av) ? $"   [{against}={av}]" : "";
							samples[drill].Add($"{e.Id} {name} = {value}{cross}");
						}
					}
				}
			}

			if (drills.Length > 0)
			{
				foreach (var drill in drills)
				{
					Console.WriteLine($"RDB type {type} field {drill}: {drilled[drill].Values.Sum()} records carry it, {drilled[drill].Count} distinct values");
					Console.WriteLine();
					Console.WriteLine("value distribution (top 20):");
					foreach (var (value, n) in drilled[drill].OrderByDescending(p => p.Value).Take(20))
						Console.WriteLine($"  {value,12} x{n}");

					if (against != 0)
					{
						Console.WriteLine();
						Console.WriteLine($"crossed with field {against} (top 10 values of {drill} per {against}):");
						foreach (var group in crossed[drill].GroupBy(p => p.Key.Against).OrderBy(g => g.Key))
						{
							var total = group.Sum(p => p.Value);
							Console.WriteLine($"  {against}={group.Key}  ({total} records, {group.Count()} distinct {drill})");
							foreach (var (key, n) in group.OrderByDescending(p => p.Value).Take(10))
								Console.WriteLine($"      {key.Value,12} x{n}");
						}
					}

					Console.WriteLine();
					Console.WriteLine("samples:");
					foreach (var line in samples[drill])
						Console.WriteLine($"  {line}");

					if (rows != null)
					{
						Directory.CreateDirectory(outDir!);
						var path = Path.Combine(outDir!, $"rdb-{type}-field-{drill}.tsv");
						File.WriteAllLines(path, rows[drill]
							.OrderBy(r => r.Id)
							.Select(r => $"{r.Id}\t{r.Name}\t{r.Value}")
							.Prepend("id\tname\tvalue"));
						Console.WriteLine($"  -> {path} ({rows[drill].Count} records)");
					}

					Console.WriteLine();
				}

				return;
			}

			Console.WriteLine($"RDB type {type}: {records} records, {counts.Count} distinct property fields");
			Console.WriteLine($"  read by the importer: {counts.Keys.Count(read.ContainsKey)}   unread: {counts.Keys.Count(f => !read.ContainsKey(f))}");
            Console.WriteLine();
			Console.WriteLine($"{"field",8} {"records",8} {"min",12} {"max",12}   what");
			foreach (var (field, n) in counts.OrderByDescending(p => p.Value))
			{
				var (min, max) = ranges[field];
				var what = read.TryGetValue(field, out var name) ? $"READ - {name}" : "";
				Console.WriteLine($"{field,8} {n,8} {min,12} {max,12}   {what}");
			}
		}

		private static void RdbId(string rdbDir, string arg)
		{
			if (!uint.TryParse(arg, out var id))
			{
				Console.WriteLine($"rdb-id needs a numeric --report-arg, got '{arg}'");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			var hits = rdb.Entries.Where(e => e.Id == id).OrderBy(e => e.Type).ToList();
			Console.WriteLine($"id {id}: {hits.Count} entr(ies)");
			foreach (var e in hits)
				Console.WriteLine($"  type={e.Type,-9} {(e.File == 0xff ? "CDN " : "local")} size={e.Size}");
		}

		// Decode a skeleton (RDB type 1010002) and print key bone world positions (#275 Phase 3 skeleton validation).
		// Emit a SKINNED glb for a skinned model (body/equipment): geometry + the character skeleton as a glTF skin.
		// --report-arg <meshIndexId>  --out <dir>  [--skeleton <id>, default auto-select by bone palette].
		private static void RdbRig(string rdbDir, string arg, string? outDir, string? skelArg)
		{
			using var rdb = OpenRdb(rdbDir);
			if (!uint.TryParse(arg, out var meshIndexId))
			{
				Console.WriteLine($"rdb-rig needs a numeric MESHINDEXFILE id in --report-arg, got '{arg}'");
				return;
			}

			uint? skeletonId = uint.TryParse(skelArg, out var s)
				? s
				: null; // null => auto-select by bone palette
			var glb = RdbAssetExtractor.BuildSkinnedModelGlb(rdb, meshIndexId, skeletonId);
			if (glb == null)
			{
				Console.WriteLine($"meshindex {meshIndexId} is not a readable skinned model (or no matching skeleton).");
				return;
			}

			Console.WriteLine($"skinned glb: meshindex={meshIndexId} skeleton={skeletonId?.ToString() ?? "auto"} bytes={glb.Length}");
			if (outDir != null)
			{
				Directory.CreateDirectory(outDir);
				var path = Path.Combine(outDir, $"rig_{meshIndexId}.glb");
				File.WriteAllBytes(path, glb);
				Console.WriteLine($"  -> {path}");
			}
		}

		// Scan every MESHINDEXFILE (type 1010030) manifest and print those whose .max source path contains the given
		// substring (case-insensitive). Used to locate race/sex character meshes (head/body/etc.) by name (#275).
		private static void RdbSearch(string rdbDir, string needle)
		{
			using var rdb = OpenRdb(rdbDir);
			needle = needle.ToLowerInvariant();
			var hits = 0;
			foreach (var e in rdb.EntriesOfType(RdbAssetExtractor.MeshIndexFileType))
			{
				if (e.Size == 0)
					continue;

				MeshIndexFile mesh;
				try
				{
					mesh = MeshIndexFile.Parse(e.Id, rdb.ReadDecompressed(e));
				}
				catch
				{
					continue;
				}

				var src = mesh.SourceFile;
				if (src == null || !src.ToLowerInvariant().Contains(needle))
					continue;

				var rm = mesh.RenderMeshId is { } rmId && rdb.TryGet(RdbAssetExtractor.RenderMeshType, rmId, out var rmE)
					? $"rm={rmId} {(rmE.File == 0xff ? "CDN" : "local")}"
					: "rm=(none)";
				Console.WriteLine($"meshIndex={e.Id,-9} skinned={mesh.Skinned,-5} {rm,-14} {src}");
				hits++;
			}

			Console.WriteLine($"-- {hits} manifest(s) matched '{needle}'.");
		}

		private static void RdbSkeleton(string rdbDir, string arg)
		{
			using var rdb = OpenRdb(rdbDir);
			if (!uint.TryParse(arg, out var id) || !rdb.TryGet(1010002, id, out var e))
			{
				Console.WriteLine($"skeleton {arg} not found");
				return;
			}

			var bones = SkeletonReader.Read(rdb.ReadDecompressed(e));
			Console.WriteLine($"skeleton {id}: {bones.Count} bones");
			foreach (var b in bones)
			{
				if (b.Name is "rig_bone_Hips" or "rig_bone_Neck" or "rig_bone_Head" or "rig_bone_Spine2" or "rig_bone_LeftHand" or "rig_bone_LeftFoot")
				{
					var w = b.WorldPosition;
					Console.WriteLine($"  {b.Name,-22} world=({w.X:0.000}, {w.Y:0.000}, {w.Z:0.000})");
				}
			}
		}

		// Decrypt one item's RDB template (type 1000020/1000620) with its stored Data1..4 TEA key and surface everything
		// that could be a worn-appearance mesh ref: every ASCII string, the property table, and every u32 that resolves
		// to a MESHINDEXFILE (1010030) / MonsterData (1010201) / RenderMesh (1010001) entry in the RDB (#275 gear).
		// The client's BALANCE-TWEAK table: RDB type 1000516, a single entry holding 294 named `double` constants
		// ("tweakables"). This is the game's own tuning sheet, and it is the authority for a pile of numbers we have so far
		// been guessing at - the out-of-combat regen attribute modifiers (TWK_OOCHealthRegenConModifier = 0.15,
		// TWK_OOCManaRegenIntModifier = 0.38, ...), the armour/protection targets per level
		// (TWK_TargetPerLevelArmor = 137.225), combat behaviour (TWK_GlancingHitBaseChance = 5,
		// TWK_InterruptChance = 30, TWK_OffhandDamagePercent = 0.5) and a set of spell/buff ids the server is expected
		// to cast itself (TWK_NanoExhaustedEffect, TWK_StaggeringBuffID, TWK_GM_Res_SpellID, ...).
		//
		// Grammar: [u32 type][u32 0][u32 1][u32 count] then count × ([u32 nameLen][nameLen ASCII][f64 value LE]).
		// It is NOT self-referential validation: TWK_NanoExhaustedEffect = 2960 is the exact spell id we independently
		// decoded as the energy-depletion "Exhausted" debuff, and TWK_Selection_Radius = 10 matches the interaction range.
		//
		// `--report-arg <substring>` filters by name (case-insensitive), e.g. `--report-arg regen`.
		private static void RdbTweaks(string rdbDir, string? filter)
		{
			const uint tweakType = 1000516;
			using var rdb = OpenRdb(rdbDir);
			var entries = rdb.EntriesOfType(tweakType).ToList();
			if (entries.Count == 0)
			{
				Console.WriteLine($"no RDB entries of type {tweakType} (the tweak table) - is this a full client install?");
				return;
			}

			foreach (var entry in entries)
			{
				var blob = rdb.ReadDecompressed(entry);
				if (blob.Length < 16)
				{
					Console.WriteLine($"tweak entry {entry.Id}: {blob.Length} B - too short");
					continue;
				}

				var count = blob.ReadU32Le(12);
				Console.WriteLine($"tweak table type={tweakType} id={entry.Id}: {blob.Length} B, declares {count} tweak(s)");
				var off = 16;
				var read = 0;
				var shown = 0;
				while (off + 4 <= blob.Length && read < count)
				{
					var len = (int)blob.ReadU32Le(off);
					if (len <= 0 || off + 4 + len + 8 > blob.Length)
						break;

					var name = Encoding.ASCII.GetString(blob, off + 4, len);
					var value = BitConverter.ToDouble(blob, off + 4 + len);
					off += 4 + len + 8;
					read++;
					if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
						continue;

					Console.WriteLine($"  {name,-52} {value.ToString("0.######", CultureInfo.InvariantCulture)}");
					shown++;
				}

				Console.WriteLine(read == count
					? $"  ({read} parsed{(filter != null ? $", {shown} matching '{filter}'" : "")})"
					: $"  ⚠ parsed only {read} of the declared {count} - the grammar breaks at byte {off}");
			}
		}
	}
}
