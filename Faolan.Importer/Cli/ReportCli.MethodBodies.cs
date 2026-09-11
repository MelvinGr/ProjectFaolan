using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report method-bodies --report-arg <MethodNameSubstring>`: every captured frame of a named RPC method, with
	// its endpoints and its body - plus the per-offset stability census that is what actually decodes one.
	//
	// It is the companion to `--report method-census`. The census answers "does the corpus carry this?"; this answers
	// the next question every one of those tickets asks, which is "what is in it?". Written once rather than as a
	// report per message, because the work is identical every time: the client's own signature names the fields
	// (docs/re/aoc-client-rpc-methods.json), the body length says whether the field widths add up, and the offsets
	// that vary across frames say which named field is which.
	//
	// A method with a handful of frames cannot have its grammar settled by variance alone - an offset that is
	// constant over 6 frames is not a constant. The census column is there to be read against the signature, not
	// instead of it.
	public static partial class ReportCli
	{
		private static void MethodBodies(IReadOnlyList<NamedCapture> captures, string? filter)
		{
			if (string.IsNullOrWhiteSpace(filter))
			{
				Console.WriteLine("--report method-bodies needs --report-arg <MethodNameSubstring> (e.g. WayPoint).");
				return;
			}

			var matches = new List<(string Method, string Class, string Capture, bool Down, uint SenderInstance, uint ReceiverInstance, byte[] Body)>();
			var unreadable = new SortedDictionary<string, int>();

			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				var names = NamesFor(p.ReceiverClass, p.WireOpcode)
					.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
					.ToList();
				if (names.Count == 0)
					continue;

				// The endpoints come from the header, and a frame whose header will not parse is COUNTED rather than
				// dropped: a silent skip here reads as "the corpus does not carry this message", which is the one
				// conclusion this report exists to make reliable.
				Rpc rpc;
				try
				{
					rpc = Rpc.Parse(p.Bytes);
				}
				catch (Exception ex)
				{
					// The body does not need the header parsed - `CapturedPacket.Body` slices it by the frame's own
					// declared header length - so the frame is still shown, with its endpoints reported as unknown.
					// Dropping it would say "the corpus does not carry this message", which is false and is the one
					// conclusion this report exists to make reliable. See `--report frame-health`.
					var key = $"{string.Join("|", names)}: {ex.GetType().Name}: {ex.Message}";
					unreadable[key] = unreadable.GetValueOrDefault(key) + 1;
					matches.Add((string.Join("|", names), ClassName(p.ReceiverClass), capture.Name, p.IsDownload, 0, 0, p.Body));
					continue;
				}

				matches.Add((string.Join("|", names), ClassName(p.ReceiverClass), capture.Name, p.IsDownload,
					rpc.Sender.Instance, rpc.Receiver.Instance, p.Body));
			}

			Console.WriteLine($"method-bodies: {matches.Count} frame(s) matching \"{filter}\"");
			foreach (var (key, count) in unreadable)
				Console.WriteLine($"  ⚠️ {count} frame(s) have an UNPARSEABLE header (endpoints shown as 0x0; body is still exact): {key}");

			foreach (var group in matches.GroupBy(m => (m.Method, m.Class, m.Down)).OrderByDescending(g => g.Count()))
			{
				var bodies = group.Select(g => g.Body).ToList();
				var lengths = bodies.Select(b => b.Length).Distinct().OrderBy(l => l).ToList();

				Console.WriteLine();
				Console.WriteLine($"== {(group.Key.Down ? "s2c" : "c2s")} {group.Key.Class}.{group.Key.Method}"
				                  + $"  {group.Count()} frame(s) over {group.Select(g => g.Capture).Distinct().Count()} capture(s)");
				Console.WriteLine($"   body length(s): {string.Join(", ", lengths)}");
				Console.WriteLine($"   sender instance(s): {string.Join(", ", group.Select(g => $"0x{g.SenderInstance:X}").Distinct().Take(8))}");
				Console.WriteLine($"   receiver instance(s): {string.Join(", ", group.Select(g => $"0x{g.ReceiverInstance:X}").Distinct().Take(8))}");

				Console.WriteLine("   frames:");
				foreach (var m in group.Take(24))
					Console.WriteLine($"      {m.Capture[..Math.Min(62, m.Capture.Length)],-62} {Convert.ToHexString(m.Body)}");
				if (group.Count() > 24)
					Console.WriteLine($"      ... and {group.Count() - 24} more");

				// Which byte offsets move. A field that never varies is either a constant, a value that happens not to
				// have changed in this many frames, or padding - the signature decides which.
				if (lengths.Count == 1 && bodies.Count > 1)
				{
					var length = lengths[0];
					var varying = Enumerable.Range(0, length).Where(i => bodies.Select(b => b[i]).Distinct().Count() > 1).ToList();
					Console.WriteLine($"   offsets that VARY across the {bodies.Count} frames: "
					                  + (varying.Count == 0 ? "(none - every frame is byte-identical)" : string.Join(",", varying)));
					Console.WriteLine($"   offsets that are CONSTANT: {string.Join(",", Enumerable.Range(0, length).Except(varying))}");
				}
			}
		}
	}
}
