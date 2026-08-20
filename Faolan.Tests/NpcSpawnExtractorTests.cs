using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
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
			var entries = NpcSpawnExtractor.Extract([Spawn(0x0004DE8A, 1f, 2f, 3f, "Orphan")]);

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

		// #309: an EPIC-mode window is not evidence for the normal stat columns. Epic re-tunes the SAME templates -
		// measured on chat-annotated-3's back-to-back Spider Caverns entries, Cavecrawler/Cavehunter carry ~8x the
		// health at the same name, template and level - so everything under an epic PlayfieldF is skipped until a
		// normal MapChange arrives.
		[Fact]
		public void Extract_SkipsSpawnsInsideAnEpicWindow()
		{
			var packets = new List<CapturedPacket>
			{
				EpicMapChange(1004),
				Spawn(0x0004DE8A, 1f, 2f, 3f, "Cavecrawler"),
				MapChange(1004),
				Spawn(0x0004DE8B, 4f, 5f, 6f, "Cavecrawler")
			};

			var entry = Assert.Single(NpcSpawnExtractor.Extract(packets));

			Assert.Equal(0x0004DE8Bu, entry.Handle); // only the NORMAL window's spawn survives
		}

		// A REAL PlayfieldFIIR, built by the codec: PlayfieldFCodec.Parse reads the record to its last byte (#530
		// rule 3), so a synthetic body with the map id at the right offset is not a MapChange any more.
		private static CapturedPacket MapChange(uint mapId)
		{
			return Packet(PlayfieldFCodec.Build(Vector3.Zero, mapId, 0, 1, 0x01EA7B9C, 15f, 13.86f, 0));
		}

		// The same record with the EPIC class word in its proxy - what retail sent on the 19:57:15 Spider Caverns
		// epic entry (0xCABC against the normal 0xC79C).
		private static CapturedPacket EpicMapChange(uint mapId)
		{
			return Packet(PlayfieldFCodec.Build(new PlayfieldFIIR(Vector3.Zero,
				new PlayfieldProxy(mapId, PlayfieldProxy.EpicPlayfieldClass, 0, 1, PlayfieldProxy.PinnedInstanceWord, 0x01EA7B9C),
				0u, 15f, 13.86f, 0u, 7u, 0u)));
		}

		// Frames content as [bodyLen4][content] then a full packet [len4][crc4][headerLen4=0][body].
		private static CapturedPacket Packet(byte[] content)
		{
			var body = new byte[4 + content.Length];
			body.WriteU32Be(0, (uint)content.Length);
			content.CopyTo(body, 4);
			return Wrap(body);
		}

		// Wraps a raw FA015807 body (golden = the post-header body) as a 0x2000 packet (12-byte zero header).
		private static CapturedPacket RawSpawn(byte[] body)
		{
			return Wrap(body);
		}

		// The sub-opcode is body[4:8], exactly as TsharkCaptureReader derives it from a real dump.
		private static CapturedPacket Wrap(byte[] body)
		{
			var full = new byte[12 + body.Length]; // headerLen (full[8..12]) stays 0
			body.CopyTo(full, 12);
			return new CapturedPacket(0, true, 0x2000, body.Length >= 8 ? body.ReadU32Be(4) : 0, full);
		}

		// ⚠️ The name rides a REAL length-prefixed CoreCharacter message, serialized by the generated type (#541).
		// This used to lay down a bare `0x0A <len> <name>` at a fixed offset, which is not a spawn - it only ever
		// satisfied the lenient byte scan the extractor has stopped using. `SpawnBodyReader.TryReadCharacterMessage`
		// verifies the u32 length ahead of field 1 and re-decodes the message, so the fixture has to carry one.
		private static CapturedPacket Spawn(uint handle, float x, float y, float z, string name)
		{
			var message = new ProtoCoreCharacterIIR
			{
				Name = name
			}.ToProtobuf();

			var content = new byte[Math.Max(56, 40 + message.Length)]; // >= the transform end (body offset 60)
			content.WriteU32Be(0, 0xFA015807);
			content.WriteU32Be(8, handle); // body@12
			content.WriteF32Le(20, x); // body@24
			content.WriteF32Le(25, y); // body@29
			content.WriteF32Le(30, z); // body@34
			content.WriteU32Be(36, (uint)message.Length); // body@40: the message's own length prefix
			message.CopyTo(content, 40); // body@44
			return Packet(content);
		}
	}
}
