using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report entity-classes`: which object-class word each dynel-family IIR actually carries on the wire.
	//
	// `EntityClass` (Faolan.Core.Network/Constants) is a small enumerated registry based at 50000, and it has holes -
	// 51323 and 51324 sit inside the world-object cluster with no name. A hole is not harmless: a class word is how a
	// message that names an entity says WHICH KIND it names, so an unnamed one turns up as a bare hex literal in
	// whatever decodes next (it did, in `DispatchAddWayPoint` - #868).
	//
	// The IIR type is already known per record, and the record carries the class word in its second u32, so the
	// mapping needs no reasoning at all: it just has to be read off the corpus and printed beside the names we hold.
	public static partial class ReportCli
	{
		private static void EntityClasses(IReadOnlyList<NamedCapture> captures, string? dumpType = null)
		{
			var dumped = 0;
			var seen = new Dictionary<(IirTypes Type, uint TypeTag), (int Count, HashSet<string> Captures)>();

			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				if (!p.IsIirCarrier || !p.IsDownload)
					continue;

				var subOpcode = (IirTypes)p.SubOpcode;
				if (Array.IndexOf(WorldObjectCodec.HandledTypes, subOpcode) < 0)
					continue;

				WorldObjectIIR record;
				try
				{
					record = WorldObjectCodec.Parse(p.Record);
				}
				catch (Exception)
				{
					continue;
				}

				// `--report-arg <IirTypeName>` also DUMPS the matching records - a class word says what kind of thing
				// an entity is, and the next question is always what one of them looks like.
				if (dumpType != null && subOpcode.ToString().Contains(dumpType, StringComparison.OrdinalIgnoreCase) && dumped++ < 20)
				{
					Console.WriteLine($"  [{capture.Name[..Math.Min(38, capture.Name.Length)]}] {subOpcode} handle 0x{record.Handle:X8} env {record.Env} "
					                  + $"transform {record.Transform.Length}b tail {record.Tail.Length}b");
					Console.WriteLine($"      tail: {Convert.ToHexString(record.Tail)}");
				}

				var key = (subOpcode, record.TypeTag);
				var row = seen.GetValueOrDefault(key, (0, []));
				row.Captures.Add(capture.Name);
				seen[key] = (row.Count + 1, row.Captures);
			}

			Console.WriteLine($"entity-classes: {seen.Count} distinct (IIR type, class word) pair(s) over {captures.Count} capture(s)");
			Console.WriteLine();
			Console.WriteLine("IIR type                          class word            named as        records  captures");

			foreach (var ((type, tag), (count, caps)) in seen.OrderBy(kv => kv.Key.TypeTag).ThenBy(kv => kv.Key.Type.ToString()))
				Console.WriteLine($"{type,-33} 0x{tag:X8} ({tag,6})  {NameOfEntityClass(tag),-14} {count,7}  {caps.Count,8}");

			Console.WriteLine();
			Console.WriteLine("⚠️ A pair with an UNNAMED class word is a registry hole, not a decode failure: the record parses");
			Console.WriteLine("   fine, we simply have no name for the kind of thing it is about.");
		}

		// The registry, by reflection over the constants, so this cannot drift from `EntityClass` itself.
		private static string NameOfEntityClass(uint tag)
		{
			return typeof(Faolan.Core.Network.Constants.EntityClass)
				       .GetFields()
				       .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(uint))
				       .FirstOrDefault(f => (uint)(f.GetRawConstantValue() ?? 0u) == tag)?.Name
			       ?? "(UNNAMED)";
		}
	}
}
