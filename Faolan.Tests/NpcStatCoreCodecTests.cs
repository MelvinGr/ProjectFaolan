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

			foreach (var table in new[] { "Npcs", "NpcSpawns" })
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
					mismatch += RoundTrip<ProtoCoreCharacterIIR.CharacterStats>(read.Message.Stats, ref checkedMsgs);
					mismatch += RoundTrip<ProtoCoreCharacterIIR.NpcStats>(read.Message.npc_stats, ref checkedMsgs);
					mismatch += RoundTrip<ProtoCoreCharacterIIR.CharacterBcc>(read.Message.Bcc, ref checkedMsgs);
				}
			}

			_out.WriteLine($"rows={rows} parsed={parsed} messagesChecked={checkedMsgs} mismatch={mismatch}");
			Assert.Equal(0, mismatch);
		}

		private static int RoundTrip<T>(T original, ref int checkedMsgs) where T : class, new()
		{
			if (original == null)
				return 0;
			checkedMsgs++;
			var rebuilt = NpcStatCoreCodec.Compose<T>(NpcStatCoreCodec.Decompose(original));
			return Ser(original).AsSpan().SequenceEqual(Ser(rebuilt)) ? 0 : 1;
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
