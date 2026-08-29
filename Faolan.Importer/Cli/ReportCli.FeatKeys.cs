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
	// `--report feat-keys [--report-arg <targetCount>]`: hunt the per-template TEA keys for the ENCRYPTED FEAT
	// records in RDB type 1000020 (#877, the plan from #251).
	//
	// ⚠️ **`FeatKeyScanner` already existed and was called from NOWHERE.** #251 built the instrument and left it
	// unwired, so the question "do feat keys ride the wire?" has been answered by *assumption* ever since - the
	// coverage report says "feat keys absent from captures" on the strength of nobody having looked. This is that
	// grep having a trigger.
	//
	// The method is the scanner's: treat every 16-byte window of every captured packet body as a candidate key and
	// TEA-decrypt the target record's FIRST BLOCK only. A plaintext template opens [u32 1000020][u32 id], so a
	// correct key yields exactly those eight bytes and a wrong one effectively never will.
	//
	// ⚠️ Cost is windows x targets, and the corpus has hundreds of millions of windows - so the target count is
	// capped and configurable rather than "every feat". A NEGATIVE result over a sample is still meaningful: the
	// keys, if they ride at all, are not per-record-rare (an unlock event would carry the key for whatever was
	// unlocked, and the sample is drawn from the feats a captured character actually has).
	//
	// ⭐ `--report-arg ids:<id>[,<id>...]` points the same hunt at NAMED records instead of a feat sample, which is
	// what makes this a general instrument rather than a feat one. Nothing about the method is feat-specific: any
	// encrypted 1000020 record opens `[u32 1000020][u32 id]`, so the two-word oracle works for an ABILITY template
	// as well (#895, where the two unbuildable Spellweaving grants are exactly this shape).
	public static partial class ReportCli
	{
		private static async Task FeatKeys(string capturesDir, string? rdbDir, string? arg)
		{
			var rdb = string.IsNullOrEmpty(rdbDir) ? RdbDir() : rdbDir;
			if (string.IsNullOrEmpty(rdb) || !Directory.Exists(rdb))
			{
				Console.WriteLine("feat-keys needs the client RDB: pass --Content:RdbDir <dir> (or set it in faolan.user.json).");
				return;
			}

			var targetCount = int.TryParse(arg, out var n) && n > 0 ? n : 12;

			// `ids:a,b,c` hunts those exact records; anything else keeps the feat-sample behaviour.
			var explicitIds = (arg ?? "").StartsWith("ids:", StringComparison.OrdinalIgnoreCase)
				? arg![4..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(t => uint.TryParse(t, out var v) ? v : 0u).Where(v => v != 0).ToHashSet()
				: null;

			using var db = OpenDb();
			var featIds = explicitIds ?? (await db.Feats.Select(f => f.Id).ToListAsync()).ToHashSet();
			Console.WriteLine(explicitIds != null
				? $"feat-keys: hunting {explicitIds.Count} NAMED record(s): {string.Join(", ", explicitIds)}"
				: $"feat-keys: {featIds.Count:N0} feat ids in the DB.");
			if (explicitIds != null)
				targetCount = explicitIds.Count;

			using var reader = OpenRdb(rdb);
			var targets = new List<FeatKeyScanner.Target>();
			int encryptedFeats = 0, plaintextFeats = 0, catalogEntries = 0, unreadable = 0;
			var catalogIds = new HashSet<uint>();

			foreach (var e in reader.EntriesOfType(1000020))
			{
				catalogEntries++;
				catalogIds.Add(e.Id);
				if (!featIds.Contains(e.Id))
					continue;

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
					plaintextFeats++;
					continue;
				}

				encryptedFeats++;
				if (targets.Count < targetCount)
					targets.Add(new FeatKeyScanner.Target(e.Id, raw[20..28]));
			}

			// ⚠️ The FIRST thing to establish is whether the question is even well posed: a hunt for the keys of
			// records that are not there would return "no key found" and be read as evidence about the wire.
			var featsInCatalog = featIds.Count(catalogIds.Contains);
			Console.WriteLine($"  catalog (RDB 1000020) holds {catalogEntries:N0} entries; {featsInCatalog:N0} of the {featIds.Count:N0} target id(s) appear in it "
			                  + $"({100.0 * featsInCatalog / Math.Max(1, featIds.Count):0.0}%).");
			Console.WriteLine($"  of those: {encryptedFeats:N0} RDBH-encrypted, {plaintextFeats:N0} plaintext, {unreadable:N0} unreadable.");
			Console.WriteLine($"  scanning for the first {targets.Count}.");
			if (targets.Count == 0)
			{
				Console.WriteLine("  Nothing to scan - no encrypted record found among the targets. ⭐ For a NAMED hunt that is");
				Console.WriteLine("  the answer rather than a dead end: a target that is PLAINTEXT needs no key at all.");
				return;
			}

			var files = Directory.GetFiles(capturesDir, "*.tshark.json").OrderBy(f => f).ToList();
			Console.WriteLine($"  scanning {files.Count} capture dump(s). Every 16-byte window of every packet body, unfiltered.");

			var hits = FeatKeyScanner.Scan(files, targets, Console.WriteLine);

			Console.WriteLine();
			if (hits.Count == 0)
			{
				Console.WriteLine("NO KEY FOUND. Over this sample, no captured packet body contains a 16-byte window that opens");
				Console.WriteLine("any of these records. That is evidence the key does NOT ride the wire in the traffic we hold -");
				Console.WriteLine("not proof it never does, since no capture in the corpus performs a feat UNLOCK, which is where");
				Console.WriteLine("#251 predicted the key would travel.");
				return;
			}

			Console.WriteLine($"** {hits.Count} HIT(S) **");
			foreach (var h in hits)
				Console.WriteLine($"  record {h.TemplateId}: key {string.Join(",", h.Key.Select(k => $"0x{k:X8}"))} ({h.WordOrder}) in {h.Source} at offset {h.Offset}");
		}
	}
}
