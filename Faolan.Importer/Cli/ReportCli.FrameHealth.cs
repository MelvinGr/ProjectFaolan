using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report frame-health`: how many captured frames our own strict `Rpc.Parse` REFUSES, and which methods they
	// are on.
	//
	// `Rpc.Parse` is deliberately strict - an unexpected tag throws, so a desynced stream is caught at the frame
	// boundary instead of three subsystems later (#610). That strictness is only trustworthy if we know what it
	// currently rejects: a refusal is either a frame we misread or a frame the dump mangled, and the two want
	// opposite fixes. `RpcHeaderTests` asserts the same property over the FIXTURE ARCHIVE, which is a slice; this is
	// the whole corpus, and the difference between the two populations is the point.
	//
	// A method that appears here is a method `--report method-bodies` cannot show, so the count is also the
	// correction to apply to any "the corpus carries N of these" claim from `--report method-census`, which reads the
	// dump's recorded opcode and never parses the header at all.
	public static partial class ReportCli
	{
		private static void FrameHealth(IReadOnlyList<NamedCapture> captures)
		{
			var refused = new Dictionary<(string Class, string Method, string Reason), (int Count, HashSet<string> Captures, byte[] First)>();
			var total = 0;
			var ok = 0;

			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				total++;
				try
				{
					Rpc.Parse(p.Bytes);
					ok++;
				}
				catch (Exception ex)
				{
					var key = (ClassName(p.ReceiverClass), string.Join("|", NamesFor(p.ReceiverClass, p.WireOpcode)), ex.Message);
					var row = refused.GetValueOrDefault(key, (0, [], p.Bytes));
					row.Captures.Add(capture.Name);
					refused[key] = (row.Count + 1, row.Captures, row.First);
				}
			}

			Console.WriteLine($"frame-health: {ok}/{total} captured frames parse strictly ({100.0 * ok / Math.Max(1, total):0.000}%), "
			                  + $"{total - ok} refused across {refused.Count} distinct (class, method, reason) triple(s)");

			if (refused.Count == 0)
			{
				Console.WriteLine("Every captured frame parses. Nothing to explain.");
				return;
			}

			Console.WriteLine();
			foreach (var (key, value) in refused.OrderByDescending(r => r.Value.Count))
			{
				Console.WriteLine($"  {value.Count,6} frame(s) over {value.Captures.Count,3} capture(s)  {key.Class}.{key.Method}");
				Console.WriteLine($"         {key.Reason}");
				Console.WriteLine($"         first frame ({value.First.Length} B): {Convert.ToHexString(value.First[..Math.Min(64, value.First.Length)])}");
			}
		}
	}
}
