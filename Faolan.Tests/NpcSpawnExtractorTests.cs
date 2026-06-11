using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Faolan.Importer;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the NPC spawn extraction: MapChange sets the current map; a following FA015807 with a small (NPC) handle
	// yields a spawn with name/position/map, while a spawn whose handle is in the instance range (the player's own)
	// is skipped.
	public class NpcSpawnExtractorTests
	{
		[Fact]
		public void Extract_IgnoresSpawnsBeforeAnyMapChange()
		{
			var entries = NpcSpawnExtractor.Extract(new List<CapturedPacket> { Spawn(0x0004DE8A, 1f, 2f, 3f, "Orphan") });

			Assert.Empty(entries); // no current map yet
		}

		// Against a REAL captured body, the extractor lifts the dissected identity from the CharacterIIR: a humanoid
		// (Casilda) with worn equipment and an animal (Crocodile, no equipment), each with its NpcStats.template_id.
		[Theory, InlineData("SpawnNpcCasilda", "Casilda", 9411u, 83u, 3u, true), InlineData("SpawnNpcCrocodile", "Crocodile", 14049u, 64u, 0u, false)]
		public void Extract_LiftsIdentityFromCapturedIir(string golden, string name, uint templateId, uint race, uint sex, bool hasEquipment)
		{
			var packets = new List<CapturedPacket>
			{
				MapChange(4010),
				RawSpawn(Golden.Bytes(golden))
			};

			var entry = Assert.Single(NpcSpawnExtractor.Extract(packets));

			Assert.Equal(name, entry.Name);
			Assert.Equal(templateId, entry.TemplateId);
			Assert.Equal(race, entry.Race);
			Assert.Equal(sex, entry.Sex);
			Assert.Equal(hasEquipment, entry.HasEquipment);
		}

		[Fact]
		public void Extract_TracksMap_SkipsPlayer_ReadsNpc()
		{
			var packets = new List<CapturedPacket>
			{
				MapChange(2040),
				Spawn(0x0004DE8A, 333.2f, 146.3f, 706.0f, "Kalanthes"),
				Spawn(0x0802E5D4, 10f, 20f, 30f, "NecroL80") // player (handle >= 0x02000000) -> skipped
			};

			var entries = NpcSpawnExtractor.Extract(packets);

			Assert.Single(entries);
			var npc = entries[0];
			Assert.Equal(2040u, npc.MapId);
			Assert.Equal("Kalanthes", npc.Name);
			Assert.Equal(0x0004DE8Au, npc.Handle);
			Assert.Equal(333.2f, npc.X, 0.1f);
		}

		private static CapturedPacket MapChange(uint mapId)
		{
			var content = new byte[34];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), 0x5D85BFC7);
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(30, 4), mapId);
			return Packet(content);
		}

		// Frames content as [bodyLen4][content] then a full packet [len4][crc4][headerLen4=0][body].
		private static CapturedPacket Packet(byte[] content)
		{
			var body = new byte[4 + content.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)content.Length);
			content.CopyTo(body, 4);

			var full = new byte[12 + body.Length]; // headerLen (full[8..12]) stays 0
			body.CopyTo(full, 12);

			return new CapturedPacket(0, true, 0x2000, 0, full);
		}

		// Wraps a raw FA015807 body (golden = the post-header body) as a 0x2000 packet (12-byte zero header).
		private static CapturedPacket RawSpawn(byte[] body)
		{
			var full = new byte[12 + body.Length];
			body.CopyTo(full, 12);
			return new CapturedPacket(0, true, 0x2000, 0, full);
		}

		private static CapturedPacket Spawn(uint handle, float x, float y, float z, string name)
		{
			var nameBytes = Encoding.ASCII.GetBytes(name);
			var content = new byte[Math.Max(56, 42 + nameBytes.Length)]; // >= the transform end (body offset 60)
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), 0xFA015807);
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8, 4), handle); // body@12
			BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(20, 4), x); // body@24
			BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(25, 4), y); // body@29
			BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(30, 4), z); // body@34
			content[40] = 0x0A; // body@44: name marker (>40)
			content[41] = (byte)nameBytes.Length;
			nameBytes.CopyTo(content.AsSpan(42));
			return Packet(content);
		}
	}
}
