using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Regenerates `CreateEditorScene.cs` - the create-editor pedestals as TYPED DATA - from the captured initial
	// burst (#364). The same shape `FAOLAN_UPDATE_DOCS` has for the generated protocol pages: normally this only
	// CHECKS that the committed file still reproduces the capture, and rewrites it when `FAOLAN_UPDATE_SCENE=1`.
	//
	//     FAOLAN_UPDATE_SCENE=1 dotnet test --project Faolan.Tests -- --filter-class '*CreateEditorSceneGeneratorTests'
	//
	// Why generate rather than hand-write: the emitted values are a parse of the capture, so hand-editing them would
	// be editing a measurement. The byte-exact gate in CreateEditorSceneCharTests is what makes that safe - a wrong
	// emit cannot pass it.
	//
	// ⚠️ Presence is decided by `ShouldSerializeX()` where protobuf-net generated one, NOT by "is it non-default".
	// An optional protobuf field that retail explicitly sent as 0 is indistinguishable from an unset one through the
	// getter, and dropping it would change the bytes.
	public class CreateEditorSceneGeneratorTests
	{
		private static readonly string OutputPath = TestPaths.Repo("Faolan.Core.Network", "Constants", "CreateEditorScene.cs");

		[Fact]
		public void TheCommittedScene_IsWhatTheCaptureGenerates()
		{
			var generated = Generate();

			if (Environment.GetEnvironmentVariable("FAOLAN_UPDATE_SCENE") == "1")
			{
				File.WriteAllText(OutputPath, generated);
				return;
			}

			Assert.True(File.Exists(OutputPath), $"{OutputPath} is missing - regenerate with FAOLAN_UPDATE_SCENE=1");
			Assert.Equal(File.ReadAllText(OutputPath).ReplaceLineEndings("\n"), generated.ReplaceLineEndings("\n"));
		}

		private static string Generate()
		{
			var s = new StringBuilder();
			s.Append("""
				using System.Collections.Generic;
				using System.Numerics;
				using Faolan.Core.Network.Codecs.Spawn;
				using Faolan.Core.Protobuf;

				namespace Faolan.Core.Network.Constants
				{
					// AUTO-GENERATED (#364) from the captured create-editor initial burst - the five archetype pedestals and the
					// live avatar preview, as TYPED DATA. Regenerate with:
					//
					//     FAOLAN_UPDATE_SCENE=1 dotnet test --project Faolan.Tests -- --filter-class '*CreateEditorSceneGeneratorTests'
					//
					// Replaces the inline hex bodies `CreateFlowService` used to carry and `CreateEditorCharBuilder.Reconstruct`
					// parsed back out of themselves - the same treatment #258 gave the access-window table and #651 the editor
					// avatar's stat sheet. The record was never opaque (SimpleCharFullUpdateCodec parses and re-encodes it
					// byte-exact), so once the grammar is typed the only thing the blob still carried was these values.
					//
					// ⚠️ Some pre-IIR words are the retail server's own UNINITIALISED MEMORY, not content: the slots they sit in
					// resolve to `PvPAnnihilationMatchesWonSeasonal`, `PvPCTFMatchesLostSeasonal`, `Onslaught2SpecialsKilled` and
					// the like, and a create-editor pedestal has no seasonal PvP record. They are reproduced because the gate is
					// byte-exactness against retail and dropping them would be a behavioural change needing an in-client check -
					// NOT because they mean anything. Do not read them as data, and do not carry them to a new record.
					// See Gitea #364.
					public static class CreateEditorScene
					{

				""");

			var records = BurstCharRecords();
			var names = new List<string>();
			foreach (var (name, record) in records)
			{
				var identifier = Identifier(name);
				names.Add(identifier);
				var value = SimpleCharFullUpdateCodec.Parse(record);
				s.Append($"\t\t// \"{name}\" - {record.Length} captured bytes.\n");
				s.Append($"\t\tpublic static SimpleCharFullUpdateIIR {identifier}()\n\t\t{{\n\t\t\treturn ");
				s.Append(Emit(value, 3));
				s.Append(";\n\t\t}\n\n");
			}

			// The fixed pedestals, in the order retail streams them. The live avatar preview is deliberately NOT in
			// this list: it carries the session's own client instance as its handle, so it is not a constant.
			s.Append("\t\t// The five fixed pedestals, in the order retail streams them. `LiveAvatarPreview` is not here on\n");
			s.Append("\t\t// purpose - it takes the session's client instance as its handle, so it is not a constant of the scene.\n");
			s.Append("\t\tpublic static IReadOnlyList<System.Func<SimpleCharFullUpdateIIR>> Pedestals =>\n\t\t[\n");
			foreach (var n in names.Where(n => n != "LiveAvatarPreview"))
				s.Append($"\t\t\t{n},\n");

			s.Append("\t\t];\n\n");
			s.Append("\t\t// Every record of the scene, pedestals first - the order the burst streams them.\n");
			s.Append("\t\tpublic static IReadOnlyList<System.Func<SimpleCharFullUpdateIIR>> All =>\n\t\t[\n");
			foreach (var n in names)
				s.Append($"\t\t\t{n},\n");

			s.Append("\t\t];\n\t}\n}\n");

			return s.ToString();
		}

		private static string Identifier(string name)
		{
			var cleaned = new string([.. name.Where(char.IsLetterOrDigit)]);
			return cleaned.StartsWith("Character", StringComparison.Ordinal) && cleaned.Length > "Character".Length
				? "LiveAvatarPreview"
				: cleaned;
		}

		// C# object-initialiser syntax for `value`, emitting only what is actually SET.
		private static string Emit(object value, int depth)
		{
			var type = value.GetType();
			var pad = new string('\t', depth);
			var inner = new string('\t', depth + 1);
			var parts = new List<string>();

			foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
			{
				if (p.GetIndexParameters().Length > 0 || p.GetMethod == null)
					continue;

				var shouldSerialize = type.GetMethod($"ShouldSerialize{p.Name}", BindingFlags.Public | BindingFlags.Instance);
				var present = shouldSerialize == null || (bool)shouldSerialize.Invoke(value, null)!;
				var current = p.GetValue(value);
				if (current == null)
					continue;

				if (current is IEnumerable list and not string)
				{
					var items = list.Cast<object>().ToList();
					if (items.Count == 0)
						continue;

					if (current is byte[] bytes)
					{
						if (!present || bytes.All(b => b == 0))
							continue;

						parts.Add($"{p.Name} = [{string.Join(", ", bytes.Select(b => $"0x{b:X2}"))}]");
						continue;
					}

					if (current is uint[] words)
					{
						if (words.All(w => w == 0))
							continue;

						parts.Add($"{p.Name} = [{string.Join(", ", words.Select(w => Scalar(w)))}]");
						continue;
					}

					var emitted = items.Select(i => Emit(i, depth + 2));
					var open = p.SetMethod != null ? $"{p.Name} = [" : $"{p.Name} = {{";
					var close = p.SetMethod != null ? "]" : "}";
					parts.Add($"{open}\n{inner}\t{string.Join($",\n{inner}\t", emitted)}\n{inner}{close}");
					continue;
				}

				if (!present)
					continue;

				if (IsScalar(current))
				{
					if (p.SetMethod == null)
						continue;

					var literal = Scalar(current);
					if (literal == null)
						continue;

					parts.Add($"{p.Name} = {literal}");
					continue;
				}

				if (p.SetMethod == null)
					continue;

				parts.Add($"{p.Name} = {Emit(current, depth + 1)}");
			}

			var name = TypeName(type);
			return parts.Count == 0
				? $"new {name}()"
				: $"new {name}\n{pad}{{\n{inner}{string.Join($",\n{inner}", parts)}\n{pad}}}";
		}

		private static bool IsScalar(object v)
		{
			return v is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or Vector3 or Quaternion or Enum;
		}

		private static string? Scalar(object v)
		{
			return v switch
			{
				string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
				bool b => b ? "true" : "false",
				float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
				double d => d.ToString("R", CultureInfo.InvariantCulture),
				Vector3 p => $"new Vector3({Scalar(p.X)}, {Scalar(p.Y)}, {Scalar(p.Z)})",
				Quaternion q => $"new Quaternion({Scalar(q.X)}, {Scalar(q.Y)}, {Scalar(q.Z)}, {Scalar(q.W)})",
				Enum e => $"{TypeName(e.GetType())}.{e}",
				byte b => $"0x{b:X2}",
				uint u => u > 9 ? $"0x{u:X}u" : $"{u}u",
				ulong u => u > 9 ? $"0x{u:X}ul" : $"{u}ul",
				ushort u => u > 9 ? $"0x{u:X}" : $"{u}",
				_ => Convert.ToString(v, CultureInfo.InvariantCulture)
			};
		}

		private static string TypeName(Type t)
		{
			return t.DeclaringType != null ? $"{TypeName(t.DeclaringType)}.{t.Name}" : t.Name;
		}

		// The FA015807 character records of the captured burst, in stream order.
		internal static List<(string Name, byte[] Record)> BurstCharRecords()
		{
			var burst = Golden.Bytes("InitialBurst");
			var result = new List<(string, byte[])>();
			for (var i = 0; i + 16 < burst.Length; i++)
			{
				if (burst[i] != 0xFA || burst[i + 1] != 0x01 || burst[i + 2] != 0x58 || burst[i + 3] != 0x07)
					continue;

				if (burst.ReadU32Be(i + 4) != 0x0000C350u)
					continue;

				var record = burst.AsSpan(i, (int)burst.ReadU32Be(i - 4)).ToArray();
				SpawnBodyReader.TryReadCharacterMessage(record.AsSpan(4).ToArray(), out var read);
				result.Add((read.Message?.Name ?? "?", record));
			}

			return result;
		}
	}
}
