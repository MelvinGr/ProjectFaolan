using System.IO;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database;
using Faolan.Core.Network.Codecs.Spawn;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// #307: a summon-only creature (pet/mount) has no NpcSpawn placement - it builds wholly from its TYPE columns
	// (StatCore + Npc.Equipment + Npc.Morphs + recordCount) via NpcSpawnBuilder.TryBuildFromType. This is the gate
	// that every lifted type produces a parseable body carrying its equipment/appearance, with no Npc.Data blob.
	public class NpcSpawnFromTypeTests
	{
		private readonly ITestOutputHelper _out;
		public NpcSpawnFromTypeTests(ITestOutputHelper o) => _out = o;

		[Fact]
		public void EveryLiftedType_BuildsFromType_WithItsEquipmentAndAppearance()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={db};Mode=ReadOnly").Options;
			using var ctx = new DatabaseContext(options);
			// The summon-only types: those carrying their own equipment/morphs (a plain map-NPC type has none - its
			// placements do). Covers the pet family; mounts too when that opt-in module is imported.
			var types = ctx.Npcs
				.Include(n => n.StatCore).Include(n => n.Equipment).Include(n => n.Morphs)
				.Where(n => n.StatCore != null && n.StatCore.IirHeader != null && (n.Equipment.Any() || n.Morphs.Any()))
				.AsNoTracking().ToList();

			Assert.NotEmpty(types); // the necro pet family is imported by default in the shipped DB
			foreach (var npc in types)
			{
				Assert.True(NpcSpawnBuilder.TryBuildFromType(npc, handle: 0x00ABCDEF, new Vector3(1, 2, 3), Quaternion.Identity, out var body),
					$"{npc.Name} failed to build from type");
				Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read), $"{npc.Name} body did not parse");

				// The built body carries the type's worn equipment and (for a humanoid) its appearance table.
				Assert.Equal(npc.Equipment.Count, read.Message.InventoryEntries.Count);
				var hasAppearance = NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out var morphs);
				if (npc.Morphs.Count > 0)
				{
					Assert.True(hasAppearance, $"{npc.Name} lost its appearance table");
					Assert.Equal(npc.Morphs.Count, morphs.Count);
				}
			}

			_out.WriteLine($"summon-only types built from columns: {types.Count} ({string.Join(", ", types.Select(t => t.Name))})");
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
