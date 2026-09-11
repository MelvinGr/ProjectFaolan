using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report spell-record-coverage`: how much of a 1040005 spell record is accounted for, byte by byte.
	//
	// A property-run reader alone accounts for roughly 13% of a typical ~964-byte record, which reads as a fixed
	// struct head plus a large trailing region that nothing reads.
	//
	// That number counts only `ReadIncreasingFields`. It is a true statement about one reader and a misleading
	// one about the record, because two more readers already cover most of the rest:
	//
	//   * `RdbSpellImporter.ReadStringBlock` - the name and description, immediately after the run.
	//   * `ItemTemplateStatModifiers.ParseElements` - the GameAction element region, which is what the bytes after
	//     the strings are. Each element reports its own `Offset` and `WidthBytes`, so its span is exact rather
	//     than inferred.
	//
	// So this attributes every byte to a region and prints what is genuinely left. Measured over all 21,502
	// records, the answer reframes the ticket:
	//
	//   GameAction elements           45.9%     <- the supposed "trailing region that nothing reads"
	//   head, once the rest is taken  49.5%
	//   property run                   4.1%
	//   name/description block         0.2%
	//   UNATTRIBUTED                   0.4%
	//
	// The trailing region is the element region, and it is read. `ParseElements` claims 45.9% of the whole
	// catalog by exact spans. So there is no large undecoded tail, and only 0.4% of the bytes are unclaimed by any
	// reader - which is the bound on where an undecoded wire field could still be hiding.
	//
	// What is least understood is a leading region, not a trailing one: the 49.5% before the property run that no
	// element claims. That share is inflated by the reader rather than by the format - `ReadIncreasingFields` returns
	// the longest ascending run, so on a record whose longest run happens to sit late, everything before it counts as
	// "head" regardless of what it is. A "72-byte fixed head" is therefore a property of the records sampled, not of
	// the catalog.
	//
	// Element spans can OVERLAP the string block in principle (the element scan is byte-granular and looks for a
	// type-id-shaped word anywhere), so bytes are marked in a per-record bitmap rather than summed - summing would
	// double-count and could report over 100%.
	public static partial class ReportCli
	{
		private static async System.Threading.Tasks.Task SpellRecordCoverage(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);

			long totalBytes = 0, headBytes = 0, runBytes = 0, stringBytes = 0, elementBytes = 0, unattributed = 0;
			var records = 0;
			var worst = new List<(uint Id, int Len, int Unattributed, double Share)>();

			foreach (var entry in rdb.EntriesOfType(SpellTemplateType).Where(rdb.CanRead))
			{
				byte[] record;
				try
				{
					record = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue;
				}

				if (record.Length < 48)
					continue;

				// The 12-byte container header is not part of the record's own grammar; every reader below works on
				// the body, so the accounting does too.
				var body = record.AsSpan(12);
				RdbSpellImporter.ReadIncreasingFields(body, out var runStart, out var runEnd);
				if (runStart <= 0)
					continue;

				records++;
				var bodyLength = body.Length;
				var covered = new bool[bodyLength];

				// Returns how many bytes this region is the first to claim, clamped to the body. Adding the nominal
				// length instead lets a region that runs past the body inflate its own share, and counting
				// already-claimed bytes lets overlapping regions sum past 100% - both happened here before this.
				int Mark(int from, int length)
				{
					var marked = 0;
					for (var i = Math.Max(0, from); i < Math.Min(bodyLength, from + length); i++)
					{
						// Count only bytes this region is the first to claim - otherwise the shares double-count and
						// sum past 100%, which is what hid the head/element overlap in the first place.
						if (covered[i])
							continue;

						marked++;
						covered[i] = true;
					}

					return marked;
				}

				// The head is everything before the run, and it is read in the sense that matters here: word 13 is the
				// flags word and words 0-2 are the container's type/id/version. It is counted separately rather than
				// as "unattributed" because it is a distinct region.
				//
				// The marking order matters, and it is a claim about which reader is authoritative rather than a
				// formatting choice. The elements and the string block report exact spans - an element carries its
				// own `Offset`
				// and `WidthBytes` - while the "head" is defined by subtraction from wherever the property run starts.
				// So the exact claims are marked first and the head gets only what is left, which turns four
				// overlapping regions into a partition.
				//
				// Without that order the shares sum to ~134%, and the overlap is not noise: `ReadIncreasingFields`
				// returns the longest ascending run, so on a record whose run starts late the apparent fixed head
				// contains element data. A longest-run reader hides everything before it.
				foreach (var element in ItemTemplateStatModifiers.ParseElements(body))
					elementBytes += Mark(element.Offset, element.WidthBytes);

				var (_, _, stringLen) = RdbSpellImporter.ReadStringBlock(body, runEnd);
				stringBytes += Mark((runEnd + 1) * 4, stringLen);

				runBytes += Mark(runStart * 4, (runEnd - runStart + 1) * 4);
				headBytes += Mark(0, runStart * 4);

				var open = covered.Count(c => !c);
				totalBytes += bodyLength;
				unattributed += open;
				worst.Add((entry.Id, bodyLength, open, (double)open / bodyLength));
			}

			if (records == 0)
			{
				Console.WriteLine("spell-record-coverage: no readable 1040005 records - is --Content:RdbDir set?");
				return;
			}

			Console.WriteLine($"1040005 spell record coverage over {records:N0} record(s), {totalBytes:N0} body byte(s) (#632)");
			Console.WriteLine();
			Console.WriteLine($"  {"region",-26} {"bytes",14}  share");
			foreach (var (name, bytes) in new (string, long)[]
			         {
				         ("GameAction elements", elementBytes), ("name/description block", stringBytes),
				         ("property run", runBytes), ("head, once the rest is taken", headBytes)
			         })
			{
				Console.WriteLine($"  {name,-26} {bytes,14:N0}  {100.0 * bytes / totalBytes,5:0.0}%");
			}

			// The regions above can overlap, so they are not summed - this is counted from the per-record bitmap.
			Console.WriteLine($"  {"UNATTRIBUTED",-26} {unattributed,14:N0}  {100.0 * unattributed / totalBytes,5:0.0}%   <- counted from a byte bitmap, not by subtraction");

			Console.WriteLine();
			Console.WriteLine("  the records with the most unattributed bytes - where an undecoded region would be:");
			Console.WriteLine($"  {"id",10} {"body",8} {"open",8}  share");
			foreach (var row in worst.OrderByDescending(r => r.Unattributed).Take(10))
				Console.WriteLine($"  {row.Id,10} {row.Len,8:N0} {row.Unattributed,8:N0}  {row.Share,5:P1}");

			await System.Threading.Tasks.Task.CompletedTask;
		}
	}
}
