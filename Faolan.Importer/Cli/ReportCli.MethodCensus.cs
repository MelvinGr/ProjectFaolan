using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report method-census [--report-arg <substring>]`: every RPC METHOD the corpus carries, named, with its
	// direction, its endpoint classes and how many frames of it there are.
	//
	// It exists because "is there a capture of X?" is the first question on almost every protocol ticket, and it was
	// being answered by a one-off script each time - which is the drift `decode_by_scratch_script_drifts` warns
	// about, and which cannot answer the OTHER half: what the corpus does NOT contain. A method absent here is the
	// evidence that a feature was never captured, and that is a finding worth being able to cite.
	//
	// ⚠️ A frame is keyed by (receiver class, wire opcode), and the wire opcode is a RENDERING of `method_instance`,
	// not an identity (see `WireOpcode`): exact on the fifteen index-registered classes, LOSSY on the hash-registered
	// PlayerAgent/PlayerInterface pair, where `0x20EC` names two methods at once. Those rows print every candidate
	// name rather than picking one.
	public static partial class ReportCli
	{
		// Endpoint class -> the method enum registered on it. The client's own pairing: an "Agent" class is the
		// server object the client calls (c2s) and an "Interface" class is the client object the server dispatches
		// to (s2c), so the RECEIVER class alone decides which table names a frame.
		private static readonly (uint Type, string Name, Type Methods)[] MethodTables =
		[
			(Endpoints.GameAgentType, "GameAgent", typeof(GameAgentMethods)),
			(Endpoints.GameInterfaceType, "GameInterface", typeof(GameInterfaceMethods)),
			(Endpoints.GameCharAgentType, "GameCharAgent", typeof(GameCharAgentMethods)),
			(Endpoints.GameCharInterfaceType, "GameCharInterface", typeof(GameCharInterfaceMethods)),
			(Endpoints.GameCharAgentProjectType, "GameCharAgentProject", typeof(GameCharAgentProjectMethods)),
			(Endpoints.GameCharInterfaceProjectType, "GameCharInterfaceProject", typeof(GameCharInterfaceProjectMethods)),
			(Endpoints.CSPlayerAgentType, "CSPlayerAgent", typeof(CSPlayerAgentMethods)),
			(Endpoints.CSPlayerInterfaceType, "CSPlayerInterface", typeof(CSPlayerInterfaceMethods)),
			(Endpoints.PlayerAgentType, "PlayerAgent", typeof(PlayerAgentMethods)),
			(Endpoints.PlayerInterfaceType, "PlayerInterface", typeof(PlayerInterfaceMethods)),
			(Endpoints.UniverseAgentType, "UniverseAgent", typeof(UniverseAgentMethods)),
			(Endpoints.UniverseInterfaceType, "UniverseInterface", typeof(UniverseInterfaceMethods)),
			(Endpoints.TradepostAgentType, "TradepostAgent", typeof(TradepostAgentMethods)),
			(Endpoints.TradepostInterfaceType, "TradepostInterface", typeof(TradepostInterfaceMethods))
		];

		private static void MethodCensus(IReadOnlyList<NamedCapture> captures, string? filter)
		{
			var seen = new Dictionary<(uint Receiver, uint Wire, bool Download), (int Count, HashSet<string> Captures, int Bytes)>();
			foreach (var capture in captures)
				foreach (var p in capture.Packets)
				{
					var key = (p.ReceiverClass, p.WireOpcode, p.IsDownload);
					var row = seen.GetValueOrDefault(key, (0, [], 0));
					row.Captures.Add(capture.Name);
					seen[key] = (row.Count + 1, row.Captures, row.Bytes + p.Body.Length);
				}

			Console.WriteLine($"{seen.Values.Sum(v => v.Count)} RPC frames across {captures.Count} captures, {seen.Count} distinct (receiver, method, direction) triples");
			if (!string.IsNullOrWhiteSpace(filter))
				Console.WriteLine($"filtered to method names containing \"{filter}\"");

			Console.WriteLine();
			Console.WriteLine("dir  receiver-class            wire    method(s)                                     frames  captures  avg body");

			var rows = seen
				.Select(kv => (Names: NamesFor(kv.Key.Receiver, kv.Key.Wire), Class: ClassName(kv.Key.Receiver), kv.Key, kv.Value))
				.Where(r => string.IsNullOrWhiteSpace(filter) || r.Names.Any(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)))
				.OrderBy(r => r.Class).ThenBy(r => r.Key.Wire);

			foreach (var r in rows)
			{
				Console.WriteLine($"{(r.Key.Download ? "s2c" : "c2s"),-4} {r.Class,-24}  0x{r.Key.Wire:X4}  {string.Join(" | ", r.Names),-44}  {r.Value.Count,6}  {r.Value.Captures.Count,8}  {r.Value.Bytes / Math.Max(1, r.Value.Count),8}");
			}

			// The other half, and the reason this is a report: what the corpus does NOT contain. A method with zero
			// frames is not a gap in the tooling - it is a feature nobody ever captured, which is what decides whether
			// a ticket is "decode it" or "capture it".
			if (string.IsNullOrWhiteSpace(filter))
				return;

			Console.WriteLine();
			Console.WriteLine($"methods matching \"{filter}\" with NO frame in the corpus:");
			var present = seen.Keys.Select(k => (k.Receiver, k.Wire)).ToHashSet();
			var absent = 0;
			foreach (var (type, name, methods) in MethodTables)
				foreach (var member in Enum.GetValues(methods).Cast<Enum>())
				{
					if (!member.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
						continue;
					if (present.Contains((type, WireOpcode.Of(member))))
						continue;

					absent++;
					Console.WriteLine($"  {name}.{member} (0x{Convert.ToUInt32(member):X2}, wire 0x{WireOpcode.Of(member):X4})");
				}

			if (absent == 0)
				Console.WriteLine("  (none - every matching method appears)");
		}

		private static string ClassName(uint receiver)
		{
			return MethodTables.FirstOrDefault(t => t.Type == receiver) is { Name: { } name } ? name : $"0x{receiver:X8}";
		}

		// Every method on the receiver's table whose wire rendering matches. More than one is the hash-registered
		// pair's ambiguity, not an error, so all of them are printed.
		private static List<string> NamesFor(uint receiver, uint wire)
		{
			var table = MethodTables.FirstOrDefault(t => t.Type == receiver);
			if (table.Methods == null)
				return [$"?0x{wire:X4}"];

			var names = Enum.GetValues(table.Methods).Cast<Enum>().Where(m => WireOpcode.Of(m) == wire).Select(m => m.ToString()).ToList();
			return names.Count > 0 ? names : [$"?0x{wire:X4}"];
		}
	}
}
