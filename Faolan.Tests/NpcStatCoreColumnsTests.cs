using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using ProtoBuf;
using Xunit;

namespace Faolan.Tests
{
	// #244 gate: a captured stat-core sub-message (CharacterStats/NpcStats/CharacterBcc) must survive the
	// NpcStatCoreColumns decompose->compose round-trip and re-serialise byte-for-byte - the guarantee that lets NPC cores
	// be rebuilt from typed columns with zero wire drift. Driven from the golden captures (the DB no longer stores the
	// captured body - both NpcSpawn.Data and NpcTemplate.Data are retired), covering the animal + humanoid core shapes.
	public class NpcStatCoreColumnsTests
	{
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcMinimalPanther"), InlineData("SpawnNpcCasilda")]
		// animal
		// animal (minimal)
		// humanoid (full core + equipment)
		public void StatCore_RoundTripsByteExact(string golden)
		{
			var body = Golden.Bytes(golden);
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(body, out var read));

			// Lift(msg) -> NpcStatCore -> To*(core) must reproduce each stat-core sub-message byte-for-byte, so a spawn
			// built from typed columns matches the capture.
			var core = NpcStatCoreColumns.Lift(read.Message);
			if (read.Message.Stats != null)
				Assert.True(Same(read.Message.Stats, NpcStatCoreColumns.ToStats(core)), "CharacterStats");
			if (read.Message.npc_stats != null)
				Assert.True(Same(read.Message.npc_stats, NpcStatCoreColumns.ToNpcStats(core)), "NpcStats");
			if (read.Message.Bcc != null)
				Assert.True(Same(read.Message.Bcc, NpcStatCoreColumns.ToBcc(core)), "CharacterBcc");
		}

		// EVERY proto field of the three stat-core sub-messages must survive the round trip - including the ones no
		// capture in the corpus happens to set (#646).
		//
		// The golden round-trip above can only prove the fields those three captures carry, so a column for a field
		// retail never sends looks dead: `RunSpeed`, `MaxRunSpeed`, `Skulk`, `MaxSkulk`, `FactionId`, `LosHeight`,
		// `MountMesh`, `OocMovementMultiplier`, `TradeskillFlags` and `ClientScryScript` are all NULL on all 1,018
		// shipped `NpcStatCores` rows, and #646's sweep read that as "schema for a decode that never landed" and
		// proposed dropping them.
		//
		// It is the opposite. The column set is dictated by the client's SCHEMA, not by the corpus - the table replaced
		// the captured `Data` blob (#244), so a field with no column is a field silently dropped from the rebuilt
		// spawn the day a capture does carry it, and `NpcStatCoreColumns` preserves protobuf PRESENCE exactly (a field
		// is copied only when ShouldSerialize is true) precisely so that unset stays unset. This drives every field
		// SET, so each one has to survive on its own evidence rather than on a capture's.
		//
		// Fields with no column fail here by name, which is the answer to "a column nobody writes is a claim we cannot
		// keep": the claim is kept by this test, not by the corpus.
		[Fact]
		public void EveryStatCoreProtoField_SurvivesTheRoundTrip_EvenTheOnesNoCaptureSets()
		{
			var stats = new ProtoCoreCharacterIIR.CharacterStats();
			var npc = new ProtoCoreCharacterIIR.NpcStats();
			var bcc = new ProtoCoreCharacterIIR.CharacterBcc();

			// A distinct non-default value per field, so a mapping that crosses two fields over shows up as a mismatch
			// rather than cancelling out.
			var set = SetEveryField(stats, 1).Concat(SetEveryField(npc, 1000)).Concat(SetEveryField(bcc, 2000)).ToList();
			Assert.NotEmpty(set);

			var core = NpcStatCoreColumns.Lift(new ProtoCoreCharacterIIR
			{
				Stats = stats,
				npc_stats = npc,
				Bcc = bcc
			});

			var missing = new List<string>();
			Check(stats, NpcStatCoreColumns.ToStats(core), nameof(ProtoCoreCharacterIIR.CharacterStats), missing);
			Check(npc, NpcStatCoreColumns.ToNpcStats(core), nameof(ProtoCoreCharacterIIR.NpcStats), missing);
			Check(bcc, NpcStatCoreColumns.ToBcc(core), nameof(ProtoCoreCharacterIIR.CharacterBcc), missing);

			Assert.True(missing.Count == 0,
				$"{missing.Count} stat-core proto field(s) do not survive Lift -> NpcStatCore -> To*: {string.Join(", ", missing)}");
		}

		// The writable, protobuf-mapped scalar properties of a generated message - the ones a column has to exist for.
		private static IEnumerable<PropertyInfo> Fields(object message)
		{
			return message.GetType()
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<ProtoMemberAttribute>() != null)
				.Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(string));
		}

		// Gives every field a distinct non-default value, seeded per message so two messages never agree by accident.
		private static List<PropertyInfo> SetEveryField(object message, int seed)
		{
			var written = new List<PropertyInfo>();
			foreach (var p in Fields(message))
			{
				var n = seed + written.Count + 1;
				var value = p.PropertyType == typeof(string)
					? $"v{n}"
					: p.PropertyType == typeof(bool)
						? true
						: Convert.ChangeType(n, p.PropertyType, CultureInfo.InvariantCulture);
				p.SetValue(message, value);
				written.Add(p);
			}

			return written;
		}

		private static void Check(object expected, object actual, string message, ICollection<string> missing)
		{
			foreach (var p in Fields(expected))
			{
				if (!Equals(p.GetValue(expected), p.GetValue(actual)))
					missing.Add($"{message}.{p.Name}");
			}
		}

		private static bool Same<T>(T a, T b)
		{
			if (a == null && b == null)
				return true;

			return a != null && b != null && Ser(a).AsSpan().SequenceEqual(Ser(b));
		}

		private static byte[] Ser<T>(T msg)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, msg);
			return ms.ToArray();
		}
	}
}
