using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network.Protocol;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// Generates docs/protocol/gen/opcodes.md from the compiled method enums (reflection, not source parsing), so the
	// page can never disagree with what the server actually dispatches. Trailing `// ...` notes are lifted from
	// Opcodes.cs as best-effort prose enrichment only - they are never the source of the names or values.
	//
	// The RPC class ids are BKDR-31 hashes of the client class names, recomputed here and asserted against the
	// capture-verified table (the same guard Faolan.Wireshark/scripts/gen_opcode_names.py applies), so a typo'd or
	// renamed class fails the build instead of silently mismatching the dissector.
	public static class OpcodesPage
	{
		public const string RelativePath = "docs/protocol/gen/opcodes.md";

		// The class ids + the enum->class pairing come from `RpcInterfaces`, the one owner of both; restating them here
		// would be a second copy of a table that only stays in step by hand. What this page keeps is the assertion -
		// recomputing every id from its class name below - because verifying the table is this generator's job, and
		// owning it is not.
		// Kept as a name the two doc generators already call; the hash itself is Faolan.Core.Crypt.Bkdr31.
		public static uint Bkdr31(string name)
		{
			return Core.Crypt.Bkdr31.Of(name);
		}

		public static string Render()
		{
			foreach (var rpc in RpcInterfaces.All)
			{
				var actual = Bkdr31(rpc.Name);
				if (actual != rpc.Type)
					throw new InvalidOperationException($"BKDR-31({rpc.Name}) = 0x{actual:X8}, expected 0x{rpc.Type:X8}");
			}

			var classByEnum = RpcInterfaces.All.ToDictionary(r => r.Methods.Name, r => r.Name);
			var idByClass = RpcInterfaces.All.ToDictionary(r => r.Name, r => r.Type);
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
			sb.AppendLine("> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'`.");
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
				// An RPC table is keyed by the method constant, with the two bytes a dump shows for it alongside - the
				// column often called "Opcode", kept so in-repo `0x2NNN` references remain findable, but not the key.
				// The chat enums have no RpcHeader and no method field, so
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
