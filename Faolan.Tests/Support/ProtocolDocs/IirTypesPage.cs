using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Faolan.Core.Network.Constants;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// Generates docs/protocol/gen/iir-types.md from the compiled GameServer0X2000Opcodes enum. Each entry's VALUE is
	// a 32-bit BKDR-31 hash of the client's IIR type name; the client names carry _c/_t suffixes (and lowercase n3)
	// that our curated member names drop. So for each entry we RECOVER the client type string by recomputing BKDR-31
	// over a small set of candidates derived from the member name, and keep only the one that hashes to the value -
	// that recovered string is then a self-contained [V1] proof (the id IS the hash of that name). Nothing is parsed
	// from a hand-written doc; the enum + the hash function are the whole source.
	public static class IirTypesPage
	{
		public const string RelativePath = "docs/protocol/gen/iir-types.md";

		public static string Render()
		{
			var members = typeof(GameServer0X2000Opcodes)
				.GetFields(BindingFlags.Public | BindingFlags.Static)
				.Select(f => (f.Name, Value: Convert.ToUInt32(f.GetRawConstantValue())))
				.OrderBy(x => x.Value)
				.ToList();
			var notes = OpcodesSource.Notes("GameServer0X2000Opcodes");

			int verified = 0, commandOps = 0, unrecovered = 0;
			var rows = new List<(uint Value, string Member, string Recovered, string Kind)>();
			foreach (var (name, value) in members)
			{
				if (value < 0x10000)
				{
					// A small value can't be a 32-bit name hash - it is a carrier command/method opcode that lives
					// in this enum for routing. Flagged, not counted as an IIR type.
					rows.Add((value, name, "", "command"));
					commandOps++;
				}
				else if (TryRecoverName(name, value, out var recovered))
				{
					rows.Add((value, name, recovered, "hash"));
					verified++;
				}
				else
				{
					rows.Add((value, name, "", "hash-unrecovered"));
					unrecovered++;
				}
			}

			var sb = new StringBuilder();
			sb.AppendLine("# IIR carrier type sub-opcodes (generated)");
			sb.AppendLine();
			sb.AppendLine("> **GENERATED — do not edit.** Source: the compiled `GameServer0X2000Opcodes` enum in");
			sb.AppendLine("> `Faolan.Core.Network/Constants/Opcodes.cs`, read by reflection.");
			sb.AppendLine("> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.");
			sb.AppendLine();
			sb.AppendLine("These are the 32-bit type ids carried inside the GameServer `0x2000` (`DispatchPassBlob`) carrier —");
			sb.AppendLine("the sub-opcode after the RPC method opcode that says which IIR record type the blob holds.");
			sb.AppendLine();
			sb.AppendLine("## Verification [V1]");
			sb.AppendLine();
			sb.AppendLine("Each type id is the **BKDR-31 hash** (`h = h*31 + c`, Java `String.hashCode`) of the client's IIR");
			sb.AppendLine("type name. The **recovered name** column is the exact string whose BKDR-31 equals the id, found by");
			sb.AppendLine("recomputing the hash over candidates derived from our member name (± `_c`/`_t`, lowercase `n3`) —");
			sb.AppendLine("so a filled cell is a self-contained proof: `BKDR-31(name) == id`. This holds without parsing any");
			sb.AppendLine("external doc; the enum + the hash function are the whole source.");
			sb.AppendLine();
			sb.AppendLine($"- **{verified}** ids: client type name recovered + BKDR-31-verified.");
			if (unrecovered > 0)
				sb.AppendLine($"- **{unrecovered}** ids: 32-bit hash (V1-recovered from the client) whose exact source string our name doesn't reproduce.");
			if (commandOps > 0)
				sb.AppendLine($"- **{commandOps}** entries are carrier COMMAND opcodes (< 0x10000), not type-name hashes — flagged in the table.");
			sb.AppendLine();
			sb.AppendLine("| Type id | Recovered client name | Our enum name | Note |");
			sb.AppendLine("|---------|-----------------------|---------------|------|");
			foreach (var (value, member, recovered, kind) in rows)
			{
				notes.TryGetValue(member, out var note);
				var recCell = kind switch
				{
					"hash"    => $"`{recovered}` ✓",
					"command" => "— *(command opcode, not a hash)*",
					_         => "— *(source string not reproduced)*"
				};
				sb.AppendLine($"| `0x{value:X8}` | {recCell} | `{member}` | {Escape(note)} |");
			}

			sb.AppendLine();
			return sb.ToString();
		}

		private static string Escape(string note)
		{
			return string.IsNullOrEmpty(note)
				? ""
				: note.Replace("|", "\\|").Trim();
		}

		// Candidate client type strings, in the shapes observed in the exe (docs/re/aoc-opcode-ids.md): the member name,
		// with/without a trailing "IIR", each ± "_c"/"_t", and with a leading "N3" lowercased to "n3".
		private static bool TryRecoverName(string member, uint value, out string recovered)
		{
			var bases = new HashSet<string>
			{
				member
			};
			if (member.EndsWith("IIR", StringComparison.Ordinal))
			{
				bases.Add(member[..^3]);
				bases.Add(member[..^3] + "IIR");
			}

			foreach (var b in bases.ToList())
			{
				if (b.StartsWith("N3", StringComparison.Ordinal))
					bases.Add("n3" + b[2..]);
			}

			foreach (var b in bases)
			foreach (var suffix in new[] { "", "_c", "_t" })
			{
				var cand = b + suffix;
				if (OpcodesPage.Bkdr31(cand) == value)
				{
					recovered = cand;
					return true;
				}
			}

			recovered = "";
			return false;
		}
	}
}
