using System.Linq;
using System.Numerics;
using Faolan.Core.Database;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #803: `NpcSpawnBuilder.WithSpawnAnimation` marks ONE delivery as an entrance - the client's one-shot "play your
	// spawn-in clip now" trigger (`npc_stats.spawn_animation`, consumed and cleared by `SimpleChar_t` slot 36, #800).
	//
	// The sender rule it implements is measured rather than assumed: retail sets the flag when the entity is
	// (re)created (`--report spawn-animation`'s respawn test - 20 of 35 matched respawns flagged against 8.1 expected
	// from those same templates' other first-sightings, and 6/6 on the solitary animals where a positional match
	// cannot pick up a pack-mate). So `BroadcastNpcRespawn` is the one path that passes it.
	//
	// ⚠️ The stamp is a re-ENCODE, not a byte patch - the field sits inside the embedded protobuf and setting it moves
	// lengths at three nesting levels. That is only safe because Parse -> Build is byte-exact, which is exactly what
	// the second gate here asserts on the shipped bodies: everything except the one field survives the trip.
	public class SpawnAnimationStampTests
	{
		[Fact]
		public void WithSpawnAnimation_SetsTheFlag_AndChangesNothingElse()
		{
			var options = TestDb.ReadOnly(TestPaths.FaolanDb);
			using var ctx = new DatabaseContext(options);

			// Any lifted type is a body source; the flag lives on `npc_stats`, which every NPC record carries.
			var types = ctx
				.NpcTemplates.Include(n => n.StatCore)
				.Include(n => n.Equipment)
				.ThenInclude(e => e.Item)
				.Include(n => n.Morphs)
				.Where(n => n.StatCore != null && n.StatCore.IirHeader != null)
				.OrderBy(n => n.TemplateId)
				.Take(50)
				.AsNoTracking()
				.ToList();

			Assert.NotEmpty(types);

			foreach (var npc in types)
			{
				var body = NpcSpawnBuilder.BuildFromType(npc, 0x00ABCDEF, new Vector3(1, 2, 3), Quaternion.Identity, 0x00A0BEEF);
				var stamped = NpcSpawnBuilder.WithSpawnAnimation(body);

				// ⚠️ The type's OWN state first. Some shipped types already carry `NpcStatCore.SpawnAnimation` (the
				// imported mode-over-sightings value, #607), so "the flag is absent before the stamp" is false for
				// them - and restoring what was there is what makes the round-trip below a statement about the STAMP
				// rather than about the type it happened to run on.
				var before = SimpleCharFullUpdateCodec.Parse(body).Character?.npc_stats;
				Assert.NotNull(before);
				var had = before!.ShouldSerializeSpawnAnimation();
				var was = before.SpawnAnimation;

				var read = SimpleCharFullUpdateCodec.Parse(stamped);
				Assert.NotNull(read.Character?.npc_stats);
				Assert.True(read.Character!.npc_stats!.ShouldSerializeSpawnAnimation(), $"{npc.Name}: the flag was not stamped");

				// Retail never sends 0 - the field is UNSET or 1, a presence flag (#607).
				Assert.Equal(1u, read.Character.npc_stats.SpawnAnimation);

				// ...and nothing else moved: putting the field back as it was reproduces the original bytes exactly.
				// This is the part a byte patch could not offer, and it is what makes re-encoding at send time safe.
				if (had)
					read.Character.npc_stats.SpawnAnimation = was;
				else
					read.Character.npc_stats.ResetSpawnAnimation();

				Assert.Equal(body, SimpleCharFullUpdateCodec.Build(read));
			}
		}

		[Fact]
		public void WithSpawnAnimation_LeavesARecordWithNoNpcStatsAlone()
		{
			var options = TestDb.ReadOnly(TestPaths.FaolanDb);
			using var ctx = new DatabaseContext(options);

			var npc = ctx
				.NpcTemplates.Include(n => n.StatCore)
				.Include(n => n.Equipment)
				.ThenInclude(e => e.Item)
				.Include(n => n.Morphs)
				.AsNoTracking()
				.First(n => n.StatCore != null && n.StatCore.IirHeader != null);

			var body = NpcSpawnBuilder.BuildFromType(npc, 0x00ABCDEF, new Vector3(1, 2, 3), Quaternion.Identity, 0x00A0BEEF);
			var record = SimpleCharFullUpdateCodec.Parse(body);
			record.Character!.npc_stats = null;
			var playerShaped = SimpleCharFullUpdateCodec.Build(record);

			// Same reference back, not a re-encoded copy: a record with no `npc_stats` has no slot for the flag, and
			// the stamp says so by declining rather than by inventing the sub-message.
			Assert.Same(playerShaped, NpcSpawnBuilder.WithSpawnAnimation(playerShaped));
		}
	}
}
