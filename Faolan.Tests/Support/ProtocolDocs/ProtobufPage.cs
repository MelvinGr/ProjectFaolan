using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// Generates docs/protocol/gen/protobuf.md from the compiled protobuf-net message types (AocProtocol.g.cs), read
	// by reflection over their [ProtoContract]/[ProtoMember] attributes. The attributes are what actually drive the
	// wire, so the page can't disagree with what the codecs (de)serialize. The point of the page is to surface the
	// per-field encoding - especially ZigZag (signed varint) and FixedSize (little-endian fixed32/64) - because
	// mis-reading a ZigZag field as a plain varint silently halved every identity value in the character editor.
	public static class ProtobufPage
	{
		public const string RelativePath = "docs/protocol/gen/protobuf.md";

		public static string Render()
		{
			var types = typeof(ProtoScryScriptIIR).Assembly.GetTypes().Where(t => t.GetCustomAttribute<ProtoContractAttribute>() != null).OrderBy(t => FullName(t), StringComparer.Ordinal).ToList();

			var totalFields = types.Sum(t => Fields(t).Count);
			var zigzag = types.SelectMany(Fields).Count(f => f.DataFormat == DataFormat.ZigZag);
			var fixedSize = types.SelectMany(Fields).Count(f => f.DataFormat == DataFormat.FixedSize);

			var sb = new StringBuilder();
			sb.AppendLine("# Protobuf message schema (generated)");
			sb.AppendLine();
			sb.AppendLine("> **GENERATED — do not edit.** Source: the compiled protobuf-net types in");
			sb.AppendLine("> `Faolan.Core/Protobuf/AocProtocol.g.cs`, read by reflection over their");
			sb.AppendLine("> `[ProtoContract]`/`[ProtoMember]` attributes (the actual wire contract).");
			sb.AppendLine("> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test --project Faolan.Tests -- --filter-class '*ProtocolDocsTests'`.");
			sb.AppendLine();
			sb.AppendLine($"{types.Count} message types, {totalFields} fields. These are the protobuf messages recovered from");
			sb.AppendLine("`AgeOfConan.exe`; the human-readable schema is `Faolan.Core/Protobuf/aoc-client-protos.proto`,");
			sb.AppendLine("and `AocProtocol.g.cs` is protobuf-net-generated from it (the reflected source here).");
			sb.AppendLine();
			sb.AppendLine("## Field encoding — read this before decoding by hand [V1]");
			sb.AppendLine();
			sb.AppendLine("The **Wire** column is the on-the-wire encoding of each field. The two that bite:");
			sb.AppendLine();
			sb.AppendLine($"- **`zigzag`** ({zigzag} fields): a SIGNED varint (`(n << 1) ^ (n >> 31)`). Reading it as a plain");
			sb.AppendLine("  varint is wrong for negatives and halves positives — this is the character-editor `int_parameter` bug.");
			sb.AppendLine($"- **`fixed32`/`fixed64`** ({fixedSize} fields): **little-endian** fixed-width. Note protobuf fixed32 is LE");
			sb.AppendLine("  while raw (non-protobuf) body floats in this protocol are big-endian — do not confuse the two.");
			sb.AppendLine("- **`varint`**: base-128 LEB, unsigned. **length-delimited**: `[varint len][bytes]` (strings, bytes, sub-messages).");
			sb.AppendLine();

			foreach (var t in types)
			{
				sb.AppendLine($"## {FullName(t)}");
				sb.AppendLine();
				var fields = Fields(t);
				if (fields.Count == 0)
				{
					sb.AppendLine("*(no fields)*");
					sb.AppendLine();
					continue;
				}

				sb.AppendLine("| Tag | Name | Type | Wire | Required |");
				sb.AppendLine("|-----|------|------|------|----------|");
				foreach (var f in fields.OrderBy(f => f.Tag))
					sb.AppendLine($"| {f.Tag} | `{f.Name}` | `{f.ClrType}` | {f.Wire} | {(f.Required ? "yes" : "")} |");
				sb.AppendLine();
			}

			return sb.ToString();
		}

		private static List<FieldInfo> Fields(Type t)
		{
			var list = new List<FieldInfo>();
			foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
			{
				var m = p.GetCustomAttribute<ProtoMemberAttribute>();
				if (m == null)
					continue;

				var clr = FriendlyType(p.PropertyType);
				list.Add(new FieldInfo(m.Tag, m.Name ?? p.Name, clr, m.DataFormat, m.IsRequired, Wire(p.PropertyType, m.DataFormat)));
			}

			return list;
		}

		private static string FriendlyType(Type t)
		{
			var u = Nullable.GetUnderlyingType(t);
			if (u != null)
				return FriendlyType(u) + "?";
			if (t == typeof(uint))
				return "uint";
			if (t == typeof(int))
				return "int";
			if (t == typeof(ulong))
				return "ulong";
			if (t == typeof(long))
				return "long";
			if (t == typeof(byte[]))
				return "byte[]";

			if (t.IsGenericType)
				return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(FriendlyType)) + ">";

			return t.Name;
		}

		private static string FullName(Type t)
		{
			return t.DeclaringType != null
				? $"{t.DeclaringType.Name}.{t.Name}"
				: t.Name;
		}

		private static string Wire(Type clr, DataFormat fmt)
		{
			var t = Nullable.GetUnderlyingType(clr) ?? clr;
			if (t == typeof(float))
			{
				return fmt == DataFormat.FixedSize
					? "`fixed32` (LE)"
					: "`varint`?";
			}

			if (t == typeof(double))
			{
				return fmt == DataFormat.FixedSize
					? "`fixed64` (LE)"
					: "`varint`?";
			}

			if (t == typeof(string))
				return "`length-delimited` (string)";
			if (t == typeof(byte[]))
				return "`length-delimited` (bytes)";

			if (t.IsPrimitive || t.IsEnum)
			{
				return fmt switch
				{
					DataFormat.ZigZag => "**`zigzag`** (signed varint)",
					DataFormat.FixedSize => t == typeof(long) || t == typeof(ulong)
						? "`fixed64` (LE)"
						: "`fixed32` (LE)",
					_ => "`varint`"
				};
			}

			// A nested message / list.
			if (typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string))
				return "`length-delimited` (repeated)";

			return "`length-delimited` (message)";
		}

		private record FieldInfo(int Tag, string Name, string ClrType, DataFormat DataFormat, bool Required, string Wire);
	}
}
