using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report feat-keys [--report-arg <mode>]`: hunt the per-template TEA keys for encrypted records in RDB type
	// 1000020.
	//
	// This is the trigger for `FeatKeyScanner`: without it, the question "do these keys ride the wire?" can only be
	// answered by assumption.
	//
	// The method is the scanner's: treat every 16-byte window of every captured packet body as a candidate key and
	// TEA-decrypt the target record's first block only. A correct key yields the `IOz1` container the plaintext opens
	// with (see FeatKeyScanner for why that, and not the template's own type word).
	//
	// ## Modes
	//
	//   (default)          the feat sample. It answers structurally rather than by scanning: every feat record is
	//                      plaintext, so there is no feat key to find.
	//   `<n>`              the same, with the sample size.
	//   `ids:a,b,c`        those exact records - a named hunt.
	//   `unkeyed[:n]`      n encrypted records we hold no key for, spread evenly across the catalog. This
	//                      is the coverage hunt: every key found decrypts a template we cannot currently read.
	//
	// ## Every run plants a control, and that is not optional
	//
	// This report's only possible answer is a negative, and a tool whose only answer is "nothing" can be completely
	// broken and look exactly like a true negative forever - there is no failing case to notice. So every run appends
	// two records whose keys are already held and refuses to report a negative unless they came back. A run that
	// loses its control has measured nothing.
	//
	// Cost is windows x targets and the corpus is ~10^9 windows, so this is a sampled run. A negative over a
	// sample is meaningful now that the instrument is known to work - which is exactly what could not be said before.
	public static partial class ReportCli
	{
		// The two Spellweaving ability templates whose keys are in the DB, harvested from captured 0x2052 grants.
		// Chosen because they are abilities: an item key could plausibly be found by the same path a future change
		// breaks, whereas these ride a message shape the scanner knows nothing about.
		private static readonly uint[] ControlIds = [3663271, 3364576];

		private static async Task FeatKeys(string capturesDir, string? rdbDir, string? arg)
		{
			var rdb = string.IsNullOrEmpty(rdbDir) ? RdbDir() : rdbDir;
			if (string.IsNullOrEmpty(rdb) || !Directory.Exists(rdb))
			{
				Console.WriteLine("feat-keys needs the client RDB: pass --Content:RdbDir <dir> (or set it in faolan.user.json).");
				return;
			}

			var mode = (arg ?? "").Trim();
			using var db = OpenDb();
			using var reader = OpenRdb(rdb);

			// Every 1000020 id we already hold a key for - the Items and Abilities catalogs. `unkeyed` is the
			// complement of this inside the encrypted half of the catalog.
			var keyed = (await db.Items.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0).Select(i => i.Id).ToListAsync()).ToHashSet();
			keyed.UnionWith(await db.Abilities.Where(a => a.Key.Key1 != 0 || a.Key.Key2 != 0 || a.Key.Key3 != 0 || a.Key.Key4 != 0).Select(a => a.Id).ToListAsync());

			// Walk the catalog once and bucket it. The encrypted/plaintext split is the RDBH magic, which is also the
			// question "is there a key to look for at all".
			var encrypted = new List<uint>();
			var encryptedBlocks = new Dictionary<uint, byte[]>();
			int catalogEntries = 0, plaintext = 0, unreadable = 0;
			foreach (var e in reader.EntriesOfType(1000020))
			{
				catalogEntries++;
				byte[] raw;
				try
				{
					raw = reader.ReadRawEntry(e);
				}
				catch (Exception)
				{
					unreadable++;
					continue;
				}

				if (!RdbReader.IsRdbh(raw) || raw.Length < 28)
				{
					plaintext++;
					continue;
				}

				encrypted.Add(e.Id);
				encryptedBlocks[e.Id] = raw[20..28];
			}

			Console.WriteLine($"feat-keys: catalog (RDB 1000020) holds {catalogEntries:N0} entries - {encrypted.Count:N0} RDBH-encrypted, "
			                  + $"{plaintext:N0} plaintext, {unreadable:N0} unreadable.");
			Console.WriteLine($"  we already hold a key for {keyed.Count:N0} record(s) ({keyed.Count(encryptedBlocks.ContainsKey):N0} of them encrypted catalog entries).");

			// `exhaustive` inverts the scan: harvest the corpus's candidate quads structurally, then test each
			// against every encrypted record. No sampling at all, so its answer is about the catalog rather than
			// about a draw from it.
			if (mode.StartsWith("exhaustive", StringComparison.OrdinalIgnoreCase))
			{
				await Exhaustive(capturesDir, reader, encrypted, encryptedBlocks, keyed, mode.EndsWith(":apply", StringComparison.OrdinalIgnoreCase), db);
				return;
			}

			var wanted = await SelectTargets(db, mode, encrypted, encryptedBlocks, keyed);
			if (wanted.Count == 0)
			{
				Console.WriteLine("  Nothing to scan. ⭐ For a NAMED or feat hunt that is the ANSWER rather than a dead end: a target");
				Console.WriteLine("  that is PLAINTEXT needs no key at all, which is the whole story for the feat half of #251.");
				return;
			}

			// The control, appended last so it never displaces a real target.
			var controls = ControlIds.Where(encryptedBlocks.ContainsKey).ToList();
			var targets = wanted.Concat(controls).Distinct().Select(id => new FeatKeyScanner.Target(id, encryptedBlocks[id])).ToList();
			Console.WriteLine($"  scanning {wanted.Count} target(s) + {controls.Count} CONTROL(s) ({string.Join(", ", controls)}) whose keys we already hold.");

			var files = Directory.GetFiles(capturesDir, "*.tshark.json").OrderBy(f => f).ToList();
			Console.WriteLine($"  {files.Count} capture dump(s). Every 16-byte window of every packet body, unfiltered and unaligned.");

			var hits = FeatKeyScanner.Scan(files, targets, Console.WriteLine);

			Console.WriteLine();
			var foundControls = controls.Where(c => hits.Any(h => h.TemplateId == c)).ToList();
			var found = hits.Select(h => h.TemplateId).Distinct().Except(controls).ToList();

			// The control is reported first and gates the conclusion. A run that lost it has measured nothing, and
			// saying so is the whole point of carrying one.
			if (foundControls.Count != controls.Count)
			{
				Console.WriteLine($"⛔ CONTROL LOST: {foundControls.Count} of {controls.Count} known-keyed record(s) came back.");
				Console.WriteLine("   This run measured NOTHING about the corpus - the instrument did not find a key that is");
				Console.WriteLine("   demonstrably present. Fix the scanner before reading anything else below.");
				return;
			}

			Console.WriteLine($"✅ control: all {controls.Count} known-keyed record(s) recovered, so a negative below is about the CORPUS.");

			if (found.Count == 0)
			{
				Console.WriteLine();
				Console.WriteLine($"NO NEW KEY. None of the {wanted.Count} sampled record(s) is opened by any 16-byte window of any");
				Console.WriteLine("captured packet body.");
				Console.WriteLine();
				Console.WriteLine("⚠️ CALIBRATE that before reading it as a strong negative (#897). A key only rides the wire when the");
				Console.WriteLine("   server SENDS you the thing, so a record nobody in the corpus was sent can never be opened here -");
				Console.WriteLine("   and an even sample of the catalog is almost entirely such records.");
				Console.WriteLine("   Recall itself is NOT the limit: on 20 known-keyed Items the scan now returns 20/20 (18 through");
				Console.WriteLine("   the protobuf fixed32 layout, 2 through the raw big-endian one). A null here is therefore about");
				Console.WriteLine("   the SAMPLE, not the instrument - draw targets the corpus plausibly carries if you want it to");
				Console.WriteLine("   mean more.");
				return;
			}

			Console.WriteLine();
			Console.WriteLine($"** {found.Count} NEW KEY(S) **");
			foreach (var h in hits.Where(h => found.Contains(h.TemplateId)).GroupBy(h => h.TemplateId).Select(g => g.First()))
				Console.WriteLine($"  record {h.TemplateId}: key {string.Join(",", h.Key.Select(k => $"0x{k:X8}"))} ({h.WordOrder}) in {h.Source} at offset {h.Offset}");
		}

		// Every encrypted record, against every candidate quad the corpus carries. The control is implicit and much
		// stronger here: the records we already hold keys for are in the target set, so the run reports how many of
		// them it recovers - if that number is low the instrument is broken and nothing else in the output counts.
		private static async Task Exhaustive(string capturesDir, RdbReader reader, List<uint> encrypted, Dictionary<uint, byte[]> encryptedBlocks, HashSet<uint> keyed,
			bool apply, Faolan.Core.Database.DatabaseContext db)
		{
			var files = Directory.GetFiles(capturesDir, "*.tshark.json").OrderBy(f => f).ToList();
			Console.WriteLine($"  EXHAUSTIVE: harvesting candidate quads from {files.Count} dump(s)...");
			var candidates = FeatKeyScanner.HarvestCandidates(files);
			Console.WriteLine($"  {candidates.Count:N0} distinct candidate quad(s). ⚠️ Most are NOT keys - the protobuf tag run matches any");
			Console.WriteLine($"  four-float message, so quaternions are in here too. They cost one trial decrypt each.");

			var targets = encrypted.Select(id => new FeatKeyScanner.Target(id, encryptedBlocks[id])).ToList();
			Console.WriteLine($"  testing each against ALL {targets.Count:N0} encrypted record(s) - {(long)candidates.Count * targets.Count:N0} trial decrypt(s).");

			var hits = FeatKeyScanner.ScanCandidates(candidates, targets, Console.WriteLine);
			var opened = hits.Select(h => h.TemplateId).Distinct().ToHashSet();
			var recovered = opened.Count(keyed.Contains);
			var brandNew = opened.Except(keyed).ToList();

			Console.WriteLine();
			Console.WriteLine($"  records opened: {opened.Count:N0}");
			Console.WriteLine($"  of which already keyed (the CONTROL): {recovered:N0} of the {keyed.Count(encryptedBlocks.ContainsKey):N0} we hold");
            Console.WriteLine($"  ⭐ NEW: {brandNew.Count:N0}");

			if (brandNew.Count == 0)
			{
				Console.WriteLine();
				Console.WriteLine("  The corpus carries no key for any record we do not already hold. Coverage is EXHAUSTED against");
				Console.WriteLine("  these dumps - what raises it is a new capture, not a better search.");
				return;
			}

			var byId = hits.GroupBy(h => h.TemplateId).ToDictionary(g => g.Key, g => g.First());

			// Validate before believing any of it. The scan's oracle is the first block only - ~45 bits, so a false
			// positive over ~10^9 trials is ~1e-5 rather than 0. This decrypts the whole record and inflates it, then
			// checks it opens with its own `[type][id]`: a key that survives that has produced a readable template,
			// which is what the key is wanted for.
			var good = new List<uint>();
			foreach (var id in brandNew.OrderBy(x => x))
			{
				if (!reader.TryGet(1000020, id, out var entry))
					continue;

				try
				{
					var plain = reader.ReadDecrypted(entry, byId[id].Key);
					if (plain.Length >= 8 && plain.ReadU32Le(0) == 1000020 && plain.ReadU32Le(4) == id)
						good.Add(id);
					else
						Console.WriteLine($"    ⚠️ record {id}: key opens the first block but the inflated record does not parse - REJECTED.");
				}
				catch (Exception e)
				{
					Console.WriteLine($"    ⚠️ record {id}: decrypt/inflate threw ({e.GetType().Name}) - REJECTED.");
				}
			}

			Console.WriteLine($"  validated (full decrypt + inflate + type/id check): {good.Count:N0} of {brandNew.Count:N0}");
			Console.WriteLine();
			foreach (var id in good)
			{
				var h = byId[id];
				Console.WriteLine($"    record {id}: key {string.Join(",", h.Key.Select(k => $"0x{k:X8}"))}");
				Console.WriteLine($"        carried by {h.WordOrder}  in {h.Source} @{h.Offset}");
			}

			// which message carries them is the actionable half: a key we do not hold, in a shape we can name,
			// says which parser is missing rather than merely that one is.
			Console.WriteLine();
			Console.WriteLine("  by carrying message:");
			foreach (var g in good.Select(id => byId[id].WordOrder).GroupBy(m => m).OrderByDescending(g => g.Count()))
				Console.WriteLine($"    {g.Count(),4}  {g.Key}");

			if (!apply)
			{
				Console.WriteLine();
				Console.WriteLine("  (read-only. `--report-arg exhaustive:apply` writes these keys into Items.)");
				return;
			}

			// Writing from a scan is the exception, not the shortcut, and it is only right where the wire gives no
			// other option. Most carriers name the item: an inventory entry, a shop ware, a spawn's worn gear, an
			// auction row and a socketed gem all carry `(template, key)` together, and those belong in
			// `ItemHashHarvester` where they keep paying on every future capture - which is where they now are.
			//
			// The equip/unequip BehaviorControlCenter ack does not: its tail carries the moved item's key quad twice
			// with no template id anywhere (`BehaviorAcks.EquipMoveAck` writes exactly that). There is nothing to
			// attribute the key by, so trial decryption against the catalog is not a lazy substitute for a parser -
			// it is the only mechanism that can identify the record at all.
			//
			// Every key written here has already survived a full decrypt + inflate + [type][id] check above, and it
			// goes through `CaptureItemImporter.UpsertAsync` so the never-downgrade rules apply exactly as they do to
			// a harvested one.
			var harvested = good.ToDictionary(id => id, id => new ItemHashHarvester.CapturedItem(byId[id].Key[0], byId[id].Key[1], byId[id].Key[2], byId[id].Key[3], 0));
			var (total, inserted) = await CaptureItemImporter.UpsertAsync(db, harvested);
			await db.SaveChangesAsync();
			Console.WriteLine();
			Console.WriteLine($"  APPLIED: {inserted} new Items row(s) of {total} validated key(s) written.");
		}

		// The target set for each mode. Kept apart from the scan so the selection is readable on its own - it is the
		// half that decides what a negative means.
		private static async Task<List<uint>> SelectTargets(Faolan.Core.Database.DatabaseContext db, string mode, List<uint> encrypted,
			Dictionary<uint, byte[]> encryptedBlocks, HashSet<uint> keyed)
		{
			if (mode.StartsWith("ids:", StringComparison.OrdinalIgnoreCase))
			{
				var ids = mode[4..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(t => uint.TryParse(t, out var v) ? v : 0u).Where(v => v != 0).Distinct().ToList();
				var present = ids.Where(encryptedBlocks.ContainsKey).ToList();
				Console.WriteLine($"  NAMED hunt: {ids.Count} id(s) given, {present.Count} of them encrypted catalog entries.");

				return present;
			}

			if (mode.StartsWith("unkeyed", StringComparison.OrdinalIgnoreCase))
			{
				var want = int.TryParse(mode.Contains(':') ? mode[(mode.IndexOf(':') + 1)..] : "", out var n) && n > 0 ? n : 200;
				var pool = encrypted.Where(id => !keyed.Contains(id)).OrderBy(id => id).ToList();

				// Spread, not the first N. The catalog is ordered by id and id ranges cluster by content pack, so
				// taking the head would sample one corner of the game and call it the catalog.
				var step = Math.Max(1, pool.Count / Math.Max(1, want));
				var picked = pool.Where((_, i) => i % step == 0).Take(want).ToList();
				Console.WriteLine($"  UNKEYED hunt: {pool.Count:N0} encrypted records with no key; sampling every {step:N0}th -> {picked.Count}.");

				return picked;
			}

			var count = int.TryParse(mode, out var c) && c > 0 ? c : 12;
			var featIds = (await db.Feats.Select(f => f.Id).ToListAsync()).ToHashSet();
			var featTargets = encrypted.Where(featIds.Contains).Take(count).ToList();
			Console.WriteLine($"  FEAT hunt: {featIds.Count:N0} feat ids in the DB, {featTargets.Count} of them encrypted catalog entries.");
			if (featTargets.Count == 0)
				Console.WriteLine("  ⭐ ZERO feat records are encrypted - every one is plaintext, so #251's feat half never needed a key.");

			return featTargets;
		}
	}
}
