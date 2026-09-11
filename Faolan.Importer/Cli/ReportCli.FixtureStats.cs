using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report fixture-stats`: what would a representative selection of the corpus slice actually keep, and what
	// would it throw away?
	//
	// The candidate rule is "keep every body the codecs fail to parse or re-encode, plus K per shape class", with
	// `(IIR, body length)` as the shape class. The first clause is empty while `--report codec-sweep` is at 100%, so
	// the whole trade turns on the second, and this is the measurement that says what it costs.
	//
	// The headline is that length is a good proxy for some IIRs and a catastrophic one for others, and the
	// report prints the ratio rather than the total so that is visible per type instead of averaged away. A type
	// whose bodies outnumber its lengths by three orders of magnitude has all of its variety inside one shape class,
	// and one-per-class would keep a single body of it.
	//
	// Why a report and not a test: it measures the archive against a hypothetical, which is a decision aid rather
	// than an invariant. The invariants are `CorpusExtract`'s two floors, which is where a narrowing would have to be
	// argued.
	public static partial class ReportCli
	{
		// A second candidate class, printed beside the length one: the body with its volatile head masked. Entity
		// handles and positions differ on every capture of the same entity, which is what makes exact dedup useless -
		// nearly every spawn body is byte-distinct - but that is a statement about the volatile fields, not
		// about the grammar. Masking them is the honest version of "a shape signature", and the gap between the two
		// columns is how much of the corpus is genuinely distinct.
		private const int VolatileHeadBytes = 32;

		private static int FixtureStats(string archivePath)
		{
			var corpus = FixtureArchiveWriter.ReadCorpusForStats(archivePath);
			if (corpus.Count == 0)
			{
				Console.WriteLine($"fixture-stats: {archivePath} carries no corpus entries.");
				return 1;
			}

			var totalBytes = corpus.Sum(e => (long)e.Body.Length);
			Console.WriteLine($"fixture-stats: {corpus.Count} corpus bodies, {totalBytes / 1024.0 / 1024.0:F1} MB of body bytes in {archivePath}");
			Console.WriteLine();
			Console.WriteLine($"  {"IIR",-28} {"bodies",7} {"lengths",8} {"masked",8} {"per class",9}   one-per-length keeps");

			foreach (var group in corpus.GroupBy(e => e.Iir).OrderByDescending(g => g.Count()))
			{
				var bodies = group.ToList();
				var lengths = bodies.Select(e => e.Body.Length).Distinct().Count();
				var masked = bodies.Select(Masked).Distinct(new ByteArrayComparer()).Count();
				var kept = bodies.GroupBy(e => e.Body.Length).Select(g => g.First().Body.Length).Sum();
				var name = Enum.IsDefined(typeof(IirTypes), group.Key) ? ((IirTypes)group.Key).ToString() : $"0x{group.Key:X8}";

				Console.WriteLine($"  {name,-28} {bodies.Count,7} {lengths,8} {masked,8} {(double)bodies.Count / lengths,9:F1}"
				                  + $"   {kept / 1024.0,8:F0} KB of {bodies.Sum(e => e.Body.Length) / 1024.0:F0} KB");
			}

			Console.WriteLine();
			Console.WriteLine("  bodies    - what the archive carries today");
			Console.WriteLine("  lengths   - distinct body LENGTHS, i.e. the shape classes the ticket's rule would keep one of");
			Console.WriteLine("  masked    - distinct bodies once the first 32 bytes (subop + entity class + handle + env) are masked,");
			Console.WriteLine("              which is the honest floor on how much of the corpus is genuinely distinct");
			Console.WriteLine("  per class - bodies per length. ⛔ A LARGE number here is the warning: all of that type's variety");
			Console.WriteLine("              lives INSIDE one shape class, so one-per-length would keep one body of it.");
			return 0;
		}

		private static byte[] Masked(FixtureArchiveWriter.CorpusBody entry)
		{
			var copy = entry.Body.ToArray();
			for (var i = 0; i < Math.Min(VolatileHeadBytes, copy.Length); i++)
				copy[i] = 0;

			return copy;
		}

		private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
		{
			public bool Equals(byte[]? x, byte[]? y)
			{
				return x != null && y != null && x.AsSpan().SequenceEqual(y);
			}

			public int GetHashCode(byte[] obj)
			{
				var hash = new HashCode();
				hash.AddBytes(obj);
				return hash.ToHashCode();
			}
		}
	}
}
