using System.Linq;
using System.Numerics;
using Faolan.Core.Database;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #307: a summon-only creature (pet/mount) has no NpcSpawn placement - it builds wholly from its TYPE columns
	// (StatCore + NpcTemplate.Equipment + NpcTemplate.Morphs + recordCount) via NpcSpawnBuilder.BuildFromType. This is the gate
	// that every lifted type produces a parseable body carrying its equipment/appearance, with no NpcTemplate.Data blob.
	public class NpcSpawnFromTypeTests
	{
		private readonly ITestOutputHelper _out;

		public NpcSpawnFromTypeTests(ITestOutputHelper o)
		{
			_out = o;
		}

		[Fact]
		public void EveryLiftedType_BuildsFromType_WithItsEquipmentAndAppearance()
		{
			var db = TestPaths.FaolanDb;
			var options = TestDb.ReadOnly(db);
			using var ctx = new DatabaseContext(options);
			// The summon-only types: those carrying their own equipment/morphs (a plain map-NPC type has none - its
			// placements do). Covers the pet family; mounts too when that opt-in module is imported.
			var types = ctx
				.NpcTemplates.Include(n => n.StatCore)
				.Include(n => n.Equipment)
				.ThenInclude(e => e.Item) // the worn item's identity (quad + hash) lives in Items via the FK
				.Include(n => n.Morphs)
				.Where(n => n.StatCore != null && n.StatCore.IirHeader != null && (n.Equipment.Any() || n.Morphs.Any()))
				.AsNoTracking()
				.ToList();

			Assert.NotEmpty(types); // the necro pet family is imported by default in the shipped DB
			foreach (var npc in types)
			{
				// A live zone token (4-byte varint, in [2^21, 2^28) like every real one). The version-offset check below
				// is what catches the #-crash regression: BuildFromType used to bake token 0 (a 1-byte varint), leaving the
				// transform 3 bytes shorter than its hardcoded 0x2C length prefix - so the client read the version byte 3
				// bytes late as 0x00 and asserted. TryReadCharacterMessage (a name-signature SCAN) never noticed.
				var body = NpcSpawnBuilder.BuildFromType(npc, 0x00ABCDEF, new Vector3(1, 2, 3), Quaternion.Identity, 0x00A0BEEF);
				AssertVersionByteFollowsTransformPrefix(body, npc.Name);
				Assert.True(SpawnBodyReader.TryReadCharacterMessage(body, out var read), $"{npc.Name} body did not parse");

				// The built record carries the type's worn equipment and (for a humanoid) its appearance table.
				Assert.Equal(npc.Equipment.Count, read.Message.InventoryEntries.Count);
				var morphs = SimpleCharFullUpdateCodec.Parse(body).Post.Slot.Appearance;
				if (npc.Morphs.Count > 0)
					Assert.Equal(npc.Morphs.Count, morphs.Count);
			}

			_out.WriteLine($"summon-only types built from columns: {types.Count} ({string.Join(", ", types.Select(t => t.Name))})");
		}

		// Reproduces the client's read: after [u32 subOp][u32 ack][u32 handle][u32 fieldA] (16 bytes - the carrier's
		// u32 length is the sender's, #530 rule 2) comes the
		// transform-length prefix (a varint), then the client consumes EXACTLY that many transform bytes and reads the
		// SimpleCharFullUpdateIIR version byte, which must be 0x05. A length prefix that disagrees with the real transform
		// length (the token-0 bug) lands this read on the wrong byte - the exact "serverversion: 0" crash.
		private static void AssertVersionByteFollowsTransformPrefix(byte[] body, string? name)
		{
			var i = 16;
			var prefix = 0;
			var shift = 0;
			while (true)
			{
				var x = body[i++];
				prefix |= (x & 0x7F) << shift;
				if ((x & 0x80) == 0)
					break;

				shift += 7;
			}

			Assert.Equal(0x05, body[i + prefix]); // the version byte the client reads at transform-end
		}
	}
}
