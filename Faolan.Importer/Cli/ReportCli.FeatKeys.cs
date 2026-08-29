using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report feat-keys [--report-arg <mode>]`: hunt the per-template TEA keys for ENCRYPTED records in RDB type
	// 1000020 (#877/#897, the plan from #251).
	//
	// ⚠️ **`FeatKeyScanner` already existed and was called from NOWHERE.** #251 built the instrument and left it
	// unwired, so the question "do these keys ride the wire?" had been answered by *assumption* ever since. This is
	// that grep having a trigger.
	//
	// The method is the scanner's: treat every 16-byte window of every captured packet body as a candidate key and
	// TEA-decrypt the target record's FIRST BLOCK only. A correct key yields the `IOz1` container the plaintext opens
	// with (see FeatKeyScanner for why that, and not the template's own type word).
	//
	// ## Modes
	//
	//   (default)          the FEAT sample - kept for #877's original question. Now answers it structurally rather
	//                      than by scanning: all 19,777 feat records are PLAINTEXT, so there is no feat key to find.
	//   `<n>`              the same, with the sample size.
	//   `ids:a,b,c`        those exact records - a NAMED hunt (#895).
	//   `unkeyed[:n]`      ⭐ n encrypted records we hold NO key for, spread evenly across the catalog (#897). This
	//                      is the coverage hunt: every key found decrypts a template we cannot currently read.
	//
	// ## ⭐⭐ Every run plants a CONTROL, and that is not optional
	//
	// This report's only possible answer is a negative, and a tool whose only answer is "nothing" can be completely
	// broken and look exactly like a true negative forever - there is no failing case to notice. This one WAS, twice
	// over, for two tickets (see FeatKeyScanner's remarks). So every run appends two records whose keys we already
	// hold and refuses to report a negative unless they came back. A run that loses its control has measured nothing.
	//
	// ⚠️ Cost is windows x targets and the corpus is ~10^9 windows, so this is a sampled run. A negative over a
	// sample is meaningful now that the instrument is known to work - which is exactly what could not be said before.
	public static partial class ReportCli
	{
		// The two Spellweaving ability templates whose keys are in the DB, harvested from captured 0x2052 grants.
		// Chosen because they are ABILITIES: an item key could plausibly be found by the same path a future change
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
			// complement of this inside the ENCRYPTED half of the catalog.
			var keyed = (await db.Items.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0).Select(i => i.Id).ToListAsync()).ToHashSet();
			keyed.UnionWith(await db.Abilities.Where(a => a.Key.Key1 != 0 || a.Key.Key2 != 0 || a.Key.Key3 != 0 || a.Key.Key4 != 0).Select(a => a.Id).ToListAsync());

			// Walk the catalog ONCE and bucket it. The encrypted/plaintext split is the RDBH magic, which is also the
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

			// ⭐ The control is reported FIRST and gates the conclusion. A run that lost it has measured nothing, and
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
				Console.WriteLine("   and an even sample of the catalog is almost entirely such records. Measured against the keys we");
				Console.WriteLine("   already hold: of 400 keyed Items, only 87 (22%) have their quad present in today's dumps as 16");
				Console.WriteLine("   contiguous big-endian bytes; 0 in little-endian, and none of the four spot-checked appears as");
				Console.WriteLine("   protobuf varints either. So ~78% of what we DO hold is not byte-present in the corpus at all,");
				Console.WriteLine("   which bounds what any window scan over it can ever return.");
				return;
			}

			Console.WriteLine();
			Console.WriteLine($"** {found.Count} NEW KEY(S) **");
			foreach (var h in hits.Where(h => found.Contains(h.TemplateId)).GroupBy(h => h.TemplateId).Select(g => g.First()))
				Console.WriteLine($"  record {h.TemplateId}: key {string.Join(",", h.Key.Select(k => $"0x{k:X8}"))} ({h.WordOrder}) in {h.Source} at offset {h.Offset}");
		}

		// The target set for each mode. Kept apart from the scan so the selection is readable on its own - it is the
		// half that decides what a negative MEANS.
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

				// ⚠️ Spread, not the first N. The catalog is ordered by id and id ranges cluster by content pack, so
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
