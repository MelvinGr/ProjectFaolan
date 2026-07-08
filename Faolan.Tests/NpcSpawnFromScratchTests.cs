using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// #244: NpcSpawnBuilder.BuildFromScratch assembles the whole FA015807 body with NO captured template (header +
	// transform-with-token + pre-IIR + CoreChar header + message + post-IIR). It must be byte-identical to the
	// freshSpawn TryBuild output (which regenerates the same scaffolds but off a template) for animals and
	// clean-appearance humanoids - proving Data is no longer needed for the frame.
	public class NpcSpawnFromScratchTests
	{
		private readonly ITestOutputHelper _out;
		public NpcSpawnFromScratchTests(ITestOutputHelper o) => _out = o;

		// Whole-DB coverage: how many shipped spawns assemble from scratch byte-exact vs. fall back to Data (the
		// humanoid runtime-post-IIR #243 tail). The from-scratch build must match the freshSpawn oracle for every
		// animal + clean-appearance humanoid; a mismatch is only allowed when the oracle's post-IIR isn't the clean
		// appearance shape (TryDecodeAppearance false but the spawn IS a humanoid carrying equipment).
		[Fact]
		public void EverySpawn_BuildsFromScratch_OrIsRuntimePostIir()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={db};Mode=ReadOnly").Options;
			using var ctx = new DatabaseContext(options);
			var rows = ctx.NpcSpawns
				.Include(s => s.Npc).ThenInclude(n => n.StatCore)
				.Include(s => s.Equipment).Include(s => s.Morphs)
				.Where(s => s.Data != null && s.Npc != null && s.Npc.StatCore != null && s.Life > 0)
				.AsNoTracking().ToList();

			int total = 0, fromScratch = 0, runtimeTail = 0, canonicalRuntime = 0, bad = 0;
			foreach (var row in rows)
			{
				var handle = row.CapturedHandle;
				if (!NpcSpawnBuilder.TryBuild(row, handle, out var oracle, freshSpawn: true) || !PlayerSpawnReader.TryReadCharacterMessage(oracle, out var read))
					continue;
				total++;
				var x = NpcStatCoreCodec.ReadIirHeaderWord(oracle);
				if (x == null)
				{
					bad++;
					continue;
				}

				var pos = new Vector3(ReadLE(oracle, SpawnPacketBuilder.PosXOffset), ReadLE(oracle, SpawnPacketBuilder.PosYOffset), ReadLE(oracle, SpawnPacketBuilder.PosZOffset));
				var rot = new Quaternion(ReadLE(oracle, SpawnPacketBuilder.RotXOffset), ReadLE(oracle, SpawnPacketBuilder.RotYOffset), ReadLE(oracle, SpawnPacketBuilder.RotZOffset), ReadLE(oracle, SpawnPacketBuilder.RotWOffset));
				var humanoid = NpcSpawnAppearanceCodec.TryDecodeAppearance(oracle, out var morphs);
				var message = NpcSpawnBuilder.BuildMessageFromColumns(row); // fully from DB columns/rows, no parsed Data
				var actual = NpcSpawnBuilder.BuildFromScratch(handle, x.Value, pos, rot, ReadPlayfieldToken(oracle), message, humanoid, morphs);

				var m = read.Message;
				var hasRuntimeMsg = m.DefensiveTarget != null || m.OffensiveTarget != null || m.LockedTo != null
					|| m.ShouldSerializePetOwnerInstance() || m.ShouldSerializePetType();
				if (oracle.AsSpan().SequenceEqual(actual))
					fromScratch++;
				else if (row.Equipment.Count > 0 && !humanoid) // humanoid with a non-appearance (runtime) post-IIR
					runtimeTail++;
				else if (hasRuntimeMsg) // the oracle kept a runtime target/pet field the canonical build drops
					canonicalRuntime++;
				else
					bad++;
			}

			_out.WriteLine($"spawns={total} fromScratch={fromScratch} runtimePostIirTail={runtimeTail} canonicalRuntime={canonicalRuntime} unexpected={bad}");
			Assert.Equal(0, bad);
		}

		// #243 tail: the no-morph HUMANOIDS ship the 41-byte minimal post-IIR but with recordCount=2 (vs the animal's
		// 1). BuildMinimalPostIir(2) must reproduce them byte-exact - the from-scratch build for an equipped NPC that
		// carries no appearance morphs.
		[Fact]
		public void NoMorphHumanoids_ReproduceWithMinimalRecordCount2()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={db};Mode=ReadOnly").Options;
			using var ctx = new DatabaseContext(options);
			var datas = ctx.NpcSpawns.Where(s => s.Data != null && s.HasEquipment).Select(s => s.Data).AsNoTracking().ToList();

			var expected = NpcSpawnScaffold.BuildMinimalPostIir(NpcSpawnScaffold.MinimalHumanoidRecordCount);
			var matched = 0;
			foreach (var data in datas)
			{
				var body = WorldObjectBuilder.ExtractBody(data);
				if (body == null || !PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;
				var post = body.AsSpan(read.Offset + read.Length);
				// the minimal-humanoid form: exactly the 41-byte body, header recordCount==2.
				if (post.Length != NpcSpawnScaffold.MinimalPostIirLength || BinaryPrimitives.ReadUInt32BigEndian(post.Slice(8, 4)) != NpcSpawnScaffold.MinimalHumanoidRecordCount)
					continue;
				Assert.Equal(expected, post.ToArray());
				matched++;
			}

			_out.WriteLine($"no-morph humanoids reproduced byte-exact with BuildMinimalPostIir(2): {matched}");
			Assert.True(matched > 100, $"expected ~148 minimal-humanoid spawns, got {matched}");
		}

		[Theory]
		[InlineData("SpawnNpcCrocodile")] // animal (minimal post-IIR)
		[InlineData("SpawnNpcCasilda")] // humanoid (appearance/morph post-IIR)
		public void BuildFromScratch_MatchesFreshSpawnBuild(string golden)
		{
			var captured = Golden.Bytes(golden);
			var handle = BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(SpawnPacketBuilder.HandleOffset, 4));

			// The freshSpawn TryBuild output is the oracle: same regenerated scaffolds, but assembled off the template.
			var row = RowFromCapturedBody(captured);
			if (NpcSpawnAppearanceCodec.TryDecodeAppearance(captured, out var rowMorphs))
				foreach (var m in rowMorphs)
					row.Morphs.Add(new NpcSpawnMorph { Ordinal = row.Morphs.Count, MorphId = m.MorphId, Value = m.Value, Text = m.Text });
			Assert.True(NpcSpawnBuilder.TryBuild(row, handle, out var expected, freshSpawn: true));

			// Decompose the oracle into the inputs BuildFromScratch takes, then reassemble from scratch.
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(expected, out var read));
			var x = NpcStatCoreCodec.ReadIirHeaderWord(expected)!.Value;
			var position = new Vector3(ReadLE(expected, SpawnPacketBuilder.PosXOffset), ReadLE(expected, SpawnPacketBuilder.PosYOffset), ReadLE(expected, SpawnPacketBuilder.PosZOffset));
			var rotation = new Quaternion(ReadLE(expected, SpawnPacketBuilder.RotXOffset), ReadLE(expected, SpawnPacketBuilder.RotYOffset), ReadLE(expected, SpawnPacketBuilder.RotZOffset), ReadLE(expected, SpawnPacketBuilder.RotWOffset));
			var token = ReadPlayfieldToken(expected);
			var humanoid = NpcSpawnAppearanceCodec.TryDecodeAppearance(expected, out var morphs);

			var actual = NpcSpawnBuilder.BuildFromScratch(handle, x, position, rotation, token, read.Message, humanoid, morphs);

			Assert.Equal(expected, actual);
		}

		// The playfield token: a 4-byte LEB128 varint at [0x18] right after the constructed transform (offset 60).
		private static uint ReadPlayfieldToken(byte[] body)
		{
			var o = SpawnPacketBuilder.RotWOffset + 4; // 60 - the 0x18 tag
			Assert.Equal(0x18, body[o]);
			return (uint)((body[o + 1] & 0x7F) | ((body[o + 2] & 0x7F) << 7) | ((body[o + 3] & 0x7F) << 14) | (body[o + 4] << 21));
		}

		private static float ReadLE(byte[] b, int offset) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));

		private static string FindRepoRoot()
		{
			var d = new DirectoryInfo(Directory.GetCurrentDirectory());
			while (d != null && !File.Exists(Path.Combine(d.FullName, "faolan.db")))
				d = d.Parent;
			return d?.FullName ?? throw new FileNotFoundException("faolan.db not found up-tree");
		}

		private static NpcSpawn RowFromCapturedBody(byte[] body)
		{
			var data = new byte[12 + body.Length]; // headerLen = 0
			body.CopyTo(data, 12);
			return new NpcSpawn
			{
				Data = data,
				PositionX = ReadLE(body, SpawnPacketBuilder.PosXOffset),
				PositionY = ReadLE(body, SpawnPacketBuilder.PosYOffset),
				PositionZ = ReadLE(body, SpawnPacketBuilder.PosZOffset),
				RotationX = ReadLE(body, SpawnPacketBuilder.RotXOffset),
				RotationY = ReadLE(body, SpawnPacketBuilder.RotYOffset),
				RotationZ = ReadLE(body, SpawnPacketBuilder.RotZOffset),
				RotationW = ReadLE(body, SpawnPacketBuilder.RotWOffset)
			};
		}
	}
}
