using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Network.Protocol;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// Generates docs/protocol/gen/opcodes.md from the COMPILED method enums (reflection, not source parsing), so the
	// page can never disagree with what the server actually dispatches. Trailing `// ...` notes are lifted from
	// Opcodes.cs as best-effort prose enrichment only - they are never the source of the names or values.
	//
	// The RPC class ids are BKDR-31 hashes of the client class names, recomputed here and asserted against the
	// capture-verified table (the same guard Faolan.Wireshark/scripts/gen_opcode_names.py applies), so a typo'd or
	// renamed class fails the build instead of silently mismatching the dissector.
	public static class OpcodesPage
	{
		public const string RelativePath = "docs/protocol/gen/opcodes.md";

		// Capture-verified RPC class ids (the dissector's table, resolved across ~109k endpoints).
		private static readonly (string Class, uint Id)[] ExpectedClassIds =
		[
			("UniverseInterface", 0x0C384054), ("GameCharInterfaceProject", 0x28377648), ("TradepostAgent", 0x3D744981), ("GameCharAgentProject", 0x5B9756FC), ("UniverseAgent", 0x604DDBA0),
			("GameInterface", 0x6C67C147), ("CSPlayerAgent", 0x6DD55614), ("PlayerInterface", 0x7D155738), ("PlayerAgent", 0x82F20484), ("GameCharInterface", 0x8B87F791),
			("GameCharAgent", 0xA9ECB95D), ("GameAgent", 0xB171CE13), ("CSPlayerInterface", 0xBBD560C8), ("TradepostInterface", 0xF2C2D5B5)
		];

		// Enum -> the client RPC class whose methods it catalogs. The RpcHeader RECEIVER class picks the table:
		// c2s receivers are *Agent classes, s2c receivers *Interface classes.
		private static readonly (string Enum, string Class)[] EnumToClass =
		[
			("UniverseAgentMethods", "UniverseAgent"), ("UniverseInterfaceMethods", "UniverseInterface"), ("PlayerAgentMethods", "PlayerAgent"), ("PlayerInterfaceMethods", "PlayerInterface"),
			("CSPlayerAgentMethods", "CSPlayerAgent"), ("CSPlayerInterfaceMethods", "CSPlayerInterface"), ("GameAgentMethods", "GameAgent"), ("GameInterfaceMethods", "GameInterface"),
			("GameCharAgentMethods", "GameCharAgent"), ("GameCharInterfaceMethods", "GameCharInterface"), ("GameCharAgentProjectMethods", "GameCharAgentProject"),
			("GameCharInterfaceProjectMethods", "GameCharInterfaceProject"), ("TradepostAgentMethods", "TradepostAgent"), ("TradepostInterfaceMethods", "TradepostInterface")
		];

		public static uint Bkdr31(string name)
		{
			uint h = 0;
			foreach (var c in name)
				h = unchecked(h * 31 + c);
			return h;
		}

		public static string Render()
		{
			foreach (var (cls, expected) in ExpectedClassIds)
			{
				var actual = Bkdr31(cls);
				if (actual != expected)
					throw new InvalidOperationException($"BKDR-31({cls}) = 0x{actual:X8}, expected 0x{expected:X8}");
			}

			var classByEnum = EnumToClass.ToDictionary(x => x.Enum, x => x.Class);
			var idByClass = ExpectedClassIds.ToDictionary(x => x.Class, x => x.Id);
			var notes = OpcodesSource.AllNotes();

			const string iirEnum = "IirTypes"; // the 32-bit IIR sub-opcodes live on iir-types.md

			// The RPC method catalogs are the `*Methods` enums; the two `ChatServer*Opcodes` are here too because chat
			// carries no RpcHeader and its numbers really are opcodes. IirTypes is the one carrier catalog, listed here
			// only as a stub that points at its own page.
			var enums = typeof(GameCharAgentMethods)
				.Assembly.GetTypes()
				.Where(t => t is { IsEnum: true, Namespace: "Faolan.Core.Enums" } &&
				            (t.Name.EndsWith("Opcodes", StringComparison.Ordinal) || t.Name.EndsWith("Methods", StringComparison.Ordinal) || t.Name == iirEnum))
				.OrderBy(t => t.Name, StringComparer.Ordinal)
				.ToList();

			var tabled = enums.Where(e => e.Name != iirEnum).ToList();
			var total = tabled.Sum(e => Members(e).Count);

			var sb = new StringBuilder();
			sb.AppendLine("# Opcode catalog (generated)");
			sb.AppendLine();
			sb.AppendLine("> **GENERATED — do not edit.** Source: the compiled enums in");
			sb.AppendLine("> `Faolan.Core/Enums/Opcodes.cs`, read by reflection.");
			sb.AppendLine("> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.");
			sb.AppendLine();
			sb.AppendLine($"{tabled.Count} enums, {total} entries. The 32-bit IIR sub-opcodes carried inside a PassBlob (`IirTypes`) are catalogued separately in [`iir-types.md`](iir-types.md).");
			sb.AppendLine();
			sb.AppendLine("## What is (and is not) verified here [V1]");
			sb.AppendLine();
			sb.AppendLine("- **Names + values** are the server's curated method catalogs — the same tables that drive the");
			sb.AppendLine("  Wireshark dissector, recovered from the client's RPC method registration. A value here IS the");
			sb.AppendLine("  `method_instance` the RpcHeader carries (field 4, one varint): the registration INDEX on the");
			sb.AppendLine("  index-registered classes, the 32-bit registration HASH on PlayerAgent/PlayerInterface. This is a");
			sb.AppendLine("  **V1** fact: the method set is complete and client-verified.");
			sb.AppendLine("- **Reads as** is what a hex dump, the Wireshark plugin and a `.tshark.json` show for it — the method");
			sb.AppendLine("  field's tag `0x20` then the varint's first group. A rendering, never an identity: on the hashed pair");
			sb.AppendLine("  it keeps 7 of 32 bits and names two methods at once (#613/#615).");
			sb.AppendLine("- **A named method does NOT mean the message BODY is decoded.** Body layouts are documented");
			sb.AppendLine("  per-message (and gated by golden tests) elsewhere; see [`coverage.md`](coverage.md) for which have one.");
			sb.AppendLine("- **Direction** is the RpcHeader receiver's role: `*Agent` classes receive client→server calls (C2S),");
			sb.AppendLine("  `*Interface` classes receive server→client dispatches (S2C). ChatServer chat carries no RpcHeader.");
			sb.AppendLine("- **Class id** = BKDR-31 hash of the client class name, recomputed + asserted here.");
			sb.AppendLine();

			foreach (var e in enums)
			{
				sb.AppendLine($"## {e.Name}");
				sb.AppendLine();

				if (e.Name == iirEnum)
				{
					sb.AppendLine("The PassBlob-carrier IIR type sub-opcodes (32-bit BKDR-31 hashes of the client IIR type names).");
					sb.AppendLine("Catalogued in [`iir-types.md`](iir-types.md).");
					sb.AppendLine();
					continue;
				}

				if (classByEnum.TryGetValue(e.Name, out var cls))
				{
					var dir = cls.EndsWith("Interface", StringComparison.Ordinal)
						? "S2C (server→client dispatch)"
						: "C2S (client→server call)";
					sb.AppendLine($"RPC class **{cls}** (id `0x{idByClass[cls]:X8}`) · {dir}");
				}
				else
				{
					// The ChatServer chat enums have no RpcHeader / no RPC class.
					var dir = e.Name.Contains("Interface", StringComparison.Ordinal)
						? "S2C"
						: "C2S";
					sb.AppendLine($"ChatServer chat — **no RpcHeader** (raw opcode) · {dir}");
				}

				sb.AppendLine();
				// An RPC table is keyed by the method CONSTANT, with the two bytes a dump shows for it alongside - the
				// column the codebase used to call "Opcode", kept so the 3,961 in-repo `0x2NNN` references remain
				// findable, but no longer the key (#613/#615). The chat enums have no RpcHeader and no method field, so
				// their numbers really are opcodes and there is nothing to render.
				var isRpc = e.Name.EndsWith("Methods", StringComparison.Ordinal);
				sb.AppendLine(isRpc ? "| method_instance | Reads as | Name | Note |" : "| Opcode | Name | Note |");
				sb.AppendLine(isRpc ? "|-----------------|----------|------|------|" : "|--------|------|------|");
				foreach (var (name, value) in Members(e))
				{
					notes.TryGetValue((e.Name, name), out var note);

					// A registration index fits two digits; a 32-bit hash needs eight. Padding every index to eight
					// would bury the number the enum is actually written in.
					var id = $"`0x{value.ToString(value <= 0xFF ? "X2" : "X8")}`";
					sb.AppendLine(isRpc
						? $"| {id} | `0x{WireOpcode.Of((uint)value):X4}` | `{name}` | {EscapeCell(note)} |"
						: $"| `0x{value:X4}` | `{name}` | {EscapeCell(note)} |");
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}

		private static string EscapeCell(string? note)
		{
			return string.IsNullOrEmpty(note)
				? ""
				: note.Replace("|", "\\|").Trim();
		}

		// (name, value) of every named constant, distinct even when two names share a value, ordered by value.
		private static List<(string Name, ulong Value)> Members(Type enumType)
		{
			return enumType
				.GetFields(BindingFlags.Public | BindingFlags.Static)
				.Select(f => (f.Name, Value: Convert.ToUInt64(f.GetRawConstantValue())))
				.OrderBy(x => x.Value)
				.ThenBy(x => x.Name, StringComparer.Ordinal)
				.ToList();
		}
	}
}
