using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report codec-sweep`: run EVERY codec against EVERY captured record of the type it declares, over all the
	// dumps - Parse, then re-Build, then compare byte-for-byte. Two failure kinds, and they are different findings:
	//
	//   REFUSED    Parse threw. The codec models a shape retail does not send.
	//   MISMATCH   Parse succeeded but Build did not reproduce the input. The codec read the body without
	//              accounting for all of it, so a field is being silently dropped or defaulted.
	//
	// This generalises the `--report timeline` footer that found #809's four (it counts refusals in ONE capture, and
	// one such line is invisible in ten thousand). It is deliberately a REPORT, not a test: `CodecSurfaceConventionTests
	// .BuildParseRoundTripsOverTheCorpus` does the same thing but against `fixtures.bin`'s frozen 9-type slice at 50
	// bodies per type, and a gate measured against the real dumps would fail the moment a capture is added (CLAUDE.md,
	// "a gate comparing SHIPPED DATA to the corpus belongs in a --report"). The test keeps the floor; this says where
	// the corpus actually stands.
	//
	// ⚠️ It sweeps IIR CARRIERS only. An RPC-message codec is identified by its `Method`, and the dump records a
	// LOSSY rendering of that field on the hash-registered classes (7 of 32 bits, #615), so dispatching on it would
	// hand bodies to the wrong codec and report the mismatch as a decode bug. Those need the `Method` to be recovered
	// first, which is #615's own remaining work.
	//
	// `--report-arg <IirTypeName>` restricts the sweep to one type and dumps the offending bodies in full.
	public static partial class ReportCli
	{
		private static readonly ConcurrentDictionary<MethodInfo, Func<byte[], object?>> SweepParsers = new();

		private static void CodecSweep(IReadOnlyList<NamedCapture> captures, string? only)
		{
			var codecs = CodecModulesWithBodies();
			var wanted = string.IsNullOrWhiteSpace(only) ? null : only.Trim();

			// opcode -> the codec that declares it. One IIR, one codec (#530 rule 6), so a collision is itself a
			// finding rather than something to pick a winner from.
			var byOpcode = new Dictionary<uint, (Type Codec, MethodInfo Parse, MethodInfo? Build, bool OpcodeIsInTheFrame)>();
			var collisions = new List<string>();
			foreach (var (type, parse, build) in codecs)
				foreach (var op in DeclaredIirTypes(type))
				{
					if (byOpcode.TryGetValue((uint)op, out var existing))
					{
						collisions.Add($"{op}: {existing.Codec.Name} and {type.Name}");
						continue;
					}

					byOpcode[(uint)op] = (type, parse, build, OpcodeIsInTheFrame(type));
				}

			var stats = new SortedDictionary<string, Tally>(StringComparer.Ordinal);
			var samples = new List<string>();
			var unclaimed = new SortedDictionary<uint, int>();

			foreach (var capture in captures)
				foreach (var packet in capture.Packets)
				{
					// ⚠️ `IsIirCarrier` is `WireOpcode == 0x2000`, and 0x2000 COLLIDES across interfaces (a method
					// index of 0 on any of them renders the same way) - so it also catches `RequestAggroStatus` and
					// friends, whose bodies are typed RPC arguments and open with an ID32 rather than an IIR type
					// word. Requiring the first word to be a named IirTypes is what separates the two; without it the
					// "no codec declares this" list fills up with entity HANDLES.
					if (!packet.IsIirCarrier || !Enum.IsDefined(typeof(IirTypes), packet.SubOpcode))
						continue;

					var record = packet.Record;
					if (record.Length < 4)
						continue;

					if (!byOpcode.TryGetValue(packet.SubOpcode, out var codec))
					{
						unclaimed[packet.SubOpcode] = unclaimed.GetValueOrDefault(packet.SubOpcode) + 1;
						continue;
					}

					var name = ((IirTypes)packet.SubOpcode).ToString();
					if (wanted != null && !name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
						continue;

					var tally = stats.TryGetValue(name, out var t) ? t : stats[name] = new Tally(codec.Codec.Name, codec.Build == null);
					tally.Seen++;

					// ⚠️ A c2s codec that declares `OpcodeIsInTheFrame` starts its body at the ENTITY CLASS: its caller
					// is the listener's routing switch, which has already consumed the type word. The record still
					// carries that word on the wire - handing it over would read four bytes out of step and return
					// plausible values rather than failing (#531), which is exactly what the flag exists to prevent.
					var bytes = (codec.OpcodeIsInTheFrame ? record[4..] : record).ToArray();
					object? value;
					try
					{
						value = SweepParserFor(codec.Parse)(bytes);
					}
					catch (Exception e) when (e is CodecParseException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
					{
						tally.Refused++;
						Record(tally.RefusedReasons, e.Message);
						if (wanted != null || tally.Refused <= 2)
							samples.Add($"    REFUSED  {name} {capture.Name} f{packet.Frame}: {e.Message}\n             {Convert.ToHexStringLower(bytes)}");

						continue;
					}

					// A PARSE-ONLY codec (a c2s message we never emit) has no Build, and that is a declaration rather
					// than a gap - there is nothing to round-trip, but whether it ACCEPTS retail traffic is still the
					// question this report exists to answer.
					if (codec.Build == null)
					{
						tally.Exact++;
						continue;
					}

					byte[] rebuilt;
					try
					{
						rebuilt = (byte[])codec.Build.Invoke(null, [value])!;
					}
					catch (TargetInvocationException e)
					{
						tally.BuildThrew++;
						Record(tally.RefusedReasons, e.InnerException?.Message ?? e.Message);
						if (wanted != null || tally.BuildThrew <= 2)
							samples.Add($"    BUILD THREW  {name} {capture.Name} f{packet.Frame}: {e.InnerException?.Message}\n             {Convert.ToHexStringLower(bytes)}");

						continue;
					}

					if (rebuilt.AsSpan().SequenceEqual(bytes))
					{
						tally.Exact++;
						continue;
					}

					tally.Mismatched++;
					if (wanted != null || tally.Mismatched <= 2)
						samples.Add($"    MISMATCH {name} {capture.Name} f{packet.Frame}: {bytes.Length} -> {rebuilt.Length} bytes, first differing byte {FirstDiff(bytes, rebuilt)}\n"
						            + $"             in  {Convert.ToHexStringLower(bytes)}\n             out {Convert.ToHexStringLower(rebuilt)}");
				}

			Report(captures, codecs.Count, byOpcode.Count, stats, samples, unclaimed, collisions, wanted);
		}

		private static void Report(IReadOnlyList<NamedCapture> captures, int codecCount, int opcodeCount,
			SortedDictionary<string, Tally> stats, List<string> samples, SortedDictionary<uint, int> unclaimed,
			List<string> collisions, string? wanted)
		{
			Console.WriteLine($"Codec sweep over {captures.Count} capture(s): {codecCount} codec(s) with a standalone body declaring {opcodeCount} IIR type(s).");
			Console.WriteLine();
			Console.WriteLine($"  {"IIR type",-38} {"codec",-46} {"seen",8} {"parsed",8} {"refused",8} {"mismatch",9}");
			Console.WriteLine($"  {new string('-', 38)} {new string('-', 46)} {new string('-', 8)} {new string('-', 8)} {new string('-', 8)} {new string('-', 9)}");

			foreach (var (name, t) in stats.OrderByDescending(kv => kv.Value.Refused + kv.Value.Mismatched + kv.Value.BuildThrew).ThenBy(kv => kv.Key, StringComparer.Ordinal))
				Console.WriteLine($"  {name,-38} {t.Codec,-46} {t.Seen,8} {t.Exact,8} {t.Refused + t.BuildThrew,8} {t.Mismatched,9}");

			var seen = stats.Values.Sum(t => (long)t.Seen);
			var exact = stats.Values.Sum(t => (long)t.Exact);
			var refused = stats.Values.Sum(t => (long)(t.Refused + t.BuildThrew));
			var mismatched = stats.Values.Sum(t => (long)t.Mismatched);
			Console.WriteLine($"  {"TOTAL",-38} {"",-46} {seen,8} {exact,8} {refused,8} {mismatched,9}");
			Console.WriteLine();
			Console.WriteLine(seen == 0
				? "⚠️ nothing swept - the filter matched no captured record."
				: $"{100.0 * exact / seen:F3}% of captured records Parse (and, where the codec has a Build, re-Build byte-exact).");

			if (collisions.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("⚠️ TWO CODECS DECLARE ONE IIR TYPE - one IIR, one codec (#530 rule 6), so these are the same codec:");
				foreach (var c in collisions)
					Console.WriteLine("    " + c);
			}

			foreach (var (name, t) in stats.Where(kv => kv.Value.Refused + kv.Value.BuildThrew > 0))
			{
				Console.WriteLine();
				Console.WriteLine($"  {name} refused {t.Refused + t.BuildThrew} of {t.Seen}:");
				foreach (var (reason, n) in t.RefusedReasons.OrderByDescending(kv => kv.Value).Take(5))
					Console.WriteLine($"    x{n,-6} {reason}");
			}

			if (samples.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"  Sample bodies ({samples.Count}{(wanted == null ? ", up to 2 per type - pass --report-arg <IirTypeName> for all of one type" : "")}):");
				foreach (var s in samples.Take(wanted == null ? 60 : 400))
					Console.WriteLine(s);
			}

			if (wanted != null || unclaimed.Count == 0)
				return;

			// Not a defect: an IIR type no codec declares is coverage the sweep cannot speak to, and saying so is what
			// keeps the percentage above from reading as "everything on the wire".
			Console.WriteLine();
			Console.WriteLine($"  {unclaimed.Count} IIR type(s) no codec declares, so not swept ({unclaimed.Values.Sum()} records):");
			foreach (var (op, n) in unclaimed.OrderByDescending(kv => kv.Value).Take(30))
				Console.WriteLine($"    x{n,-7} {(IirTypes)op}");
		}

		private static string FirstDiff(byte[] a, byte[] b)
		{
			for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
				if (a[i] != b[i])
					return $"@{i} (0x{a[i]:X2} -> 0x{b[i]:X2})";

			return $"@{Math.Min(a.Length, b.Length)} (length only)";
		}

		private static void Record(Dictionary<string, int> into, string reason)
		{
			into[reason] = into.GetValueOrDefault(reason) + 1;
		}

		// The codec modules that own a standalone body: a static class with a `Parse(in ReadOnlySpan<byte>)` and the
		// canonical `Build(T)` that takes exactly what Parse returns. Sub-record codecs declare no opcode and so drop
		// out below; text codecs (rule 1a) have a `string` Parse and never match here.
		private static List<(Type Type, MethodInfo Parse, MethodInfo? Build)> CodecModulesWithBodies()
		{
			var found = new List<(Type, MethodInfo, MethodInfo?)>();
			foreach (var t in typeof(TeleportCodec).Assembly.GetTypes().Where(t => t is { IsAbstract: true, IsSealed: true, IsNested: false }))
			{
				var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
				var parse = methods.SingleOrDefault(m => m.Name == "Parse" && m.GetParameters() is [{ } p] && p.ParameterType == typeof(ReadOnlySpan<byte>).MakeByRefType());
				if (parse == null)
					continue;

				found.Add((t, parse, methods.FirstOrDefault(m => m.Name == "Build" && m.GetParameters() is [{ } p] && p.ParameterType == parse.ReturnType)));
			}

			return found;
		}

		// A codec's declared IIR identity - `Opcode` (one type) or `HandledTypes` (a family). The same two fields
		// docs/protocol/gen/coverage.md reads, which is what makes `grep IirTypes.<Type>` find every emitter.
		private static IEnumerable<IirTypes> DeclaredIirTypes(Type t)
		{
			foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
			{
				if (f.IsLiteral && f.FieldType == typeof(uint) && f.Name == "Opcode")
					yield return (IirTypes)(uint)f.GetRawConstantValue()!;
				else if (f.Name == "HandledTypes" && f.FieldType == typeof(IirTypes[]))
					foreach (var v in (IirTypes[])f.GetValue(null)!)
						yield return v;
			}
		}

		// `Parse` takes an `in ReadOnlySpan<byte>`, which `MethodInfo.Invoke` cannot pass (a byref to a ref struct is
		// not boxable), so the call is emitted. Same trick as CodecSurfaceConventionTests - and it has the side
		// benefit that the codec's own exception arrives UNWRAPPED, without a TargetInvocationException around it.
		private static Func<byte[], object?> SweepParserFor(MethodInfo parse)
		{
			return SweepParsers.GetOrAdd(parse, static m =>
			{
				var dm = new DynamicMethod($"sweep_{m.DeclaringType!.Name}", typeof(object), [typeof(byte[])], typeof(ReportCli).Module, true);
				var il = dm.GetILGenerator();
				il.DeclareLocal(typeof(ReadOnlySpan<byte>));
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Call, typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", [typeof(byte[])])!);
				il.Emit(OpCodes.Stloc_0);
				il.Emit(OpCodes.Ldloca_S, (byte)0);
				il.Emit(OpCodes.Call, m);
				if (m.ReturnType.IsValueType)
					il.Emit(OpCodes.Box, m.ReturnType);

				il.Emit(OpCodes.Ret);

				return dm.CreateDelegate<Func<byte[], object?>>();
			});
		}

		// Whether the codec says its body starts AFTER the type word (a c2s record whose router already read it).
		private static bool OpcodeIsInTheFrame(Type t)
		{
			return t.GetField("OpcodeIsInTheFrame", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly) is { IsLiteral: true } f && (bool)f.GetRawConstantValue()!;
		}

		private sealed class Tally(string codec, bool parseOnly)
		{
			public string Codec { get; } = codec + (parseOnly ? " (parse-only)" : "");

			public int Seen;
			public int Exact;
			public int Refused;
			public int BuildThrew;
			public int Mismatched;
			public Dictionary<string, int> RefusedReasons { get; } = new(StringComparer.Ordinal);
		}
	}
}
