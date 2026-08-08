using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
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

		// `CharacterStats.flags` is a real BITFIELD, and bit 28 is the "this is an NPC" bit (#646).
		//
		// The sweep flagged it as a decode smell: `NpcStatCores.Flags` is 268435457 (0x10000001) on all 1,018 shipped
		// rows, and "a flags word with one value across the whole corpus is either not a flags word or is being read at
		// the wrong offset". Neither. It is protobuf field 16, addressed by field NUMBER, so there is no offset to get
		// wrong - and the corpus explains the constancy rather than undermining it:
		//
		//   NPC records    (17,400): 0x10000001, every one
		//   PLAYER records    (317): 0x00000001 x308, 0x00000021 x5, 0x00000009 x4 - bit 28 NEVER set
		//
		// So bit 0 is set on everything and bit 28 separates NPC from player. It is constant across NPCs BECAUSE they
		// are all NPCs, which is the opposite of the value being undecoded.
		//
		// ⚠️ Bits 3 (0x8) and 5 (0x20) are per-player STATE, not identity, and are still unknown: the same character
		// carries 0x1 at one moment and 0x9 or 0x21 at another (Viriden, Beriyah, Skylights, Dejachicka, Korlinius,
		// Kathokla, Shirux each appear with two values), and the two never co-occur. Tracked as Gitea #648. This gate
		// deliberately asserts only what is decoded, so a future capture that sets bit 28 on a player - or clears it on
		// an NPC - fails here rather than being absorbed.
		[Fact]
		public void CharacterStatsFlags_Bit28_IsTheNpcBit()
		{
			const uint npcBit = 1u << 28;
			var npcs = 0;
			var players = 0;
			var wrong = new List<string>();

			foreach (var iir in new[] { IirTypes.SimpleCharFullUpdateIIR, IirTypes.FullPlayerCharacterIIR })
			foreach (var (source, body) in CorpusExtract.OfType((uint)iir))
			{
				if (!SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					continue;

				var stats = read.Message.Stats;
				if (stats == null || !stats.ShouldSerializeFlags())
					continue;

				// `npc_stats` present IS the record being an NPC - the independent signal the bit is checked against.
				var isNpc = read.Message.npc_stats != null;
				if (isNpc)
					npcs++;
				else
					players++;

				if (((stats.Flags & npcBit) != 0) != isNpc)
					wrong.Add($"{read.Message.Name} 0x{stats.Flags:X8} (npc_stats {(isNpc ? "present" : "absent")}) in {source}");

				if ((stats.Flags & 1) == 0)
					wrong.Add($"{read.Message.Name} 0x{stats.Flags:X8} has bit 0 clear in {source}");
			}

			Assert.True(npcs > 1000, $"only {npcs} NPC stat cores in the corpus - the gate would be vacuous");
			Assert.True(players > 100, $"only {players} player stat cores in the corpus - the gate would be vacuous");
			Assert.True(wrong.Count == 0, $"{wrong.Count} record(s) disagree with the bit-28 = NPC reading: {string.Join("; ", wrong.Take(5))}");
		}

		// A TYPE row must not carry a pose the type is only RARELY caught in (#688, the sibling of #602/#605).
		//
		// `bcc_state` and the `anim_idle_id` that goes with it are per-spawn-EVENT: 102 of 1024 captured types appear
		// in more than one state, always lopsidedly. A Citizen (12460) is captured standing 286 times and sitting 5
		// times - and the representative that reached the lift was one of the 5, so every Citizen in Old Tarantia sat
		// down, walking around in a seated pose because a MoveTask moves an NPC whatever pose it holds.
		//
		// ⚠️ **This does NOT re-derive the importer's mode, and the first draft that did was wrong.** The importer
		// measures over ALL 120 captures; the committed extract is a sample of them, so on a near-tie the two
		// legitimately disagree (type 8775 is 32/26 in the extract, and the full corpus puts it the other way round).
		// A gate that demands equality there is asserting which sample it was fed, not what is true - the same trap as
		// any other derived gate. What IS stable across both is the SHAPE of the bug: a value that some other value
		// outnumbers many times over. So the assertion is that margin, and near-ties are deliberately left alone.
		//
		// It does not assert "everyone stands" either, which would trade one wrong pose for another: the Door Warden
		// (19619) is state 11 in all 15 of its sightings, the Beggar sits, the Drunken Pirate is drunk.
		[Fact]
		public void BccStateAndIdleAnimation_AreNotAPoseTheTypeIsRarelyCaughtIn()
		{
			// How far another value may outnumber the shipped one before it reads as "the representative was caught
			// mid-emote" rather than "the corpus is thin". The Citizen bug was 57x; the worst near-tie is 1.5x.
			const int outnumberedBy = 4;

			var states = new Dictionary<uint, Dictionary<uint, int>>();
			var idles = new Dictionary<uint, Dictionary<uint, int>>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryReadCharacterMessage(body, out var read) || read.Message.npc_stats is not { } npc || !npc.ShouldSerializeTemplateId())
					continue;

				if (read.Message.Stats?.ShouldSerializeBccState() == true)
					Tally(states, npc.TemplateId, read.Message.Stats.BccState);
				if (read.Message.Bcc?.ShouldSerializeAnimIdleId() == true)
					Tally(idles, npc.TemplateId, read.Message.Bcc.AnimIdleId);
			}

			// The shipped value is an outlier when the corpus has never seen it, or when another value outnumbers it
			// by the margin above. Everything closer than that is the sample talking, not the data.
			static string? Outlier(uint shipped, Dictionary<uint, int> tally, int margin)
			{
				var mine = tally.GetValueOrDefault(shipped);
				var best = tally.OrderByDescending(kv => kv.Value).First();
				if (mine == 0)
					return "never captured on this type";

				return best.Value >= mine * margin ? $"outnumbered {best.Value}:{mine} by {best.Key}" : null;
			}

			static void Tally(Dictionary<uint, Dictionary<uint, int>> into, uint template, uint value)
			{
				var tally = into.TryGetValue(template, out var t) ? t : into[template] = [];
				tally[value] = tally.GetValueOrDefault(value) + 1;
			}

			var mixed = states.Count(kv => kv.Value.Count > 1);
			Assert.True(mixed > 50, $"only {mixed} types are captured in more than one bcc_state - the gate would be vacuous");

			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			// EVERY core with one of the two fields, placed or not (#689). This used to exclude types with no
			// `NpcSpawns` row, on the reading that they were beyond the importer's reach - 42 rows, the Courtesan
			// among them. That reading was wrong twice over: those rows are overwhelmingly SUMMON targets (pets,
			// companions, minions) and encounter bosses, which are never placed BY DESIGN rather than by a dropped
			// placement; and they were only unreachable because the modal correction lived inline in the placed-NPC
			// path, so the pet/mount importers never ran it. They do now, so the exclusion would hide a live gate.
			//
			// A template the CORPUS has no sighting of is still skipped, but by the `TryGetValue` below rather than by
			// a proxy - that is the honest condition, and it is a much smaller set.
			cmd.CommandText =
				"""
				SELECT c.NpcTemplateId, c.BccState, c.AnimIdleId FROM NpcStatCores c
				WHERE c.BccState IS NOT NULL OR c.AnimIdleId IS NOT NULL
				""";
			using var reader = cmd.ExecuteReader();

			var wrong = new List<string>();
			var checkedRows = 0;
			while (reader.Read())
			{
				var template = (uint)reader.GetInt64(0);
				if (!reader.IsDBNull(1) && states.TryGetValue(template, out var seenStates))
				{
					checkedRows++;
					var shipped = (uint)reader.GetInt64(1);
					if (Outlier(shipped, seenStates, outnumberedBy) is { } why)
						wrong.Add($"template {template} ships bcc_state {shipped} ({why}); sightings {Format(seenStates)}");
				}

				if (!reader.IsDBNull(2) && idles.TryGetValue(template, out var seenIdles))
				{
					var shipped = (uint)reader.GetInt64(2);
					if (Outlier(shipped, seenIdles, outnumberedBy) is { } why)
						wrong.Add($"template {template} ships anim_idle_id {shipped} ({why}); sightings {Format(seenIdles)}");
				}
			}

			static string Format(Dictionary<uint, int> tally)
			{
				return string.Join(", ", tally.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}"));
			}

			Assert.True(checkedRows > 500, $"only {checkedRows} shipped cores could be checked against the corpus - the gate would be vacuous");
			Assert.True(wrong.Count == 0, $"{wrong.Count} shipped stat core(s) carry a pose the type is only rarely caught in:\n  " + string.Join("\n  ", wrong.Take(10)));
		}

		// The writable, protobuf-mapped scalar properties of a generated message - the ones a column has to exist for.
		private static IEnumerable<PropertyInfo> Fields(object message)
		{
			return message.GetType()
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Where(p => p is { CanRead: true, CanWrite: true } && p.GetCustomAttribute<ProtoMemberAttribute>() != null)
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
