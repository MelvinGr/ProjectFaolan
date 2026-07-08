using System;
using System.IO;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Protobuf;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// #244 gate: every captured stat-core sub-message (CharacterStats/NpcStats/CharacterBcc) in the shipped DB must
	// survive the NpcStatCoreCodec decompose->compose round-trip and re-serialise byte-for-byte - the guarantee that
	// lets NPC cores be rebuilt from typed columns with zero wire drift. If a re-import ever lands a core the codec
	// can't losslessly round-trip (a new field shape), this catches it before the spawn body diverges.
	public class NpcStatCoreCodecTests
	{
		private readonly ITestOutputHelper _out;
		public NpcStatCoreCodecTests(ITestOutputHelper o) => _out = o;

		[Fact]
		public void EveryStatCore_RoundTripsByteExact()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
			con.Open();

			int rows = 0, parsed = 0, checkedMsgs = 0, mismatch = 0;

			// NpcSpawn.Data was retired (#243/#244 - spawns build from typed columns); the type-level Npc.Data captured
			// bodies remain (pets #307) and still exercise the stat-core codec round-trip.
			foreach (var table in new[] { "Npcs" })
			{
				using var cmd = con.CreateCommand();
				cmd.CommandText = $"SELECT Data FROM {table} WHERE Data IS NOT NULL";
				using var r = cmd.ExecuteReader();
				while (r.Read())
				{
					rows++;
					var body = WorldObjectBuilder.ExtractBody((byte[])r[0]);
					if (body == null || !PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
						continue;
					parsed++;

					// The importer->builder path: Lift(msg) -> NpcStatCore -> To*(core) must reproduce each stat-core
					// sub-message byte-for-byte, so a spawn built from typed columns matches the capture.
					// Only NPC cores carry all three sub-messages; the builder composes those. Non-NPC/edge rows with a
					// null sub-message are skipped (the builder never composes one).
					var core = NpcStatCoreCodec.Lift(read.Message);
					if (read.Message.Stats != null) { checkedMsgs++; mismatch += Same(read.Message.Stats, NpcStatCoreCodec.ToStats(core)); }
					if (read.Message.npc_stats != null) { checkedMsgs++; mismatch += Same(read.Message.npc_stats, NpcStatCoreCodec.ToNpcStats(core)); }
					if (read.Message.Bcc != null) { checkedMsgs++; mismatch += Same(read.Message.Bcc, NpcStatCoreCodec.ToBcc(core)); }
				}
			}

			_out.WriteLine($"rows={rows} parsed={parsed} messagesChecked={checkedMsgs} mismatch={mismatch}");
			Assert.Equal(0, mismatch);
		}

		private static int Same<T>(T a, T b)
		{
			if (a == null && b == null)
				return 0;
			return a != null && b != null && Ser(a).AsSpan().SequenceEqual(Ser(b)) ? 0 : 1;
		}

		private static byte[] Ser<T>(T msg)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, msg);
			return ms.ToArray();
		}

		private static string FindRepoRoot()
		{
			var d = new DirectoryInfo(Directory.GetCurrentDirectory());
			while (d != null && !File.Exists(Path.Combine(d.FullName, "faolan.db")))
				d = d.Parent;
			return d?.FullName ?? throw new FileNotFoundException("faolan.db not found up-tree");
		}
	}
}
