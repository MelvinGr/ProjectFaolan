using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The mount harvest (#27): a player-cast SummonMount renders only if the mount creature ("Fading Black Horse")
	// is in the DB to spawn. CaptureMountImporter must lift it out of the real mount captures' FA015807 spawns and
	// seed it under the never-loaded sentinel Mounts map, resolvable by name (the MountCatalog target).
	public class CaptureMountImporterTests
	{
		private static readonly string CapturesDir = TestPaths.CapturesDir;

		[Fact]
		public async Task Import_HarvestsTheFadingBlackHorse_UnderTheMountMap()
		{
			var capture = Path.Combine(CapturesDir, "AssassinL23_rogue-assassin_login_randomActions-resurect.tshark.json");
			if (!File.Exists(capture))
				return; // capture not present in this checkout - nothing to assert

			var packets = TsharkCaptureReader.Read(capture);

			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-mount-{Guid.NewGuid():N}.db");
			try
			{
				await using var db = NewContext(dbPath);
				await db.Database.EnsureCreatedAsync();

				var (seen, seeded) = await CaptureMountImporter.ImportAsync(db, [packets]);

				Assert.True(seen >= 1, "expected at least one mount creature harvested from the capture");
				Assert.True(seeded >= 1);

				// Mounts are now TYPES (#307), not sentinel-map NpcSpawn placements: the creature builds wholly from
				// columns via TryBuildFromType, so its type carries a lifted stat core (no Npc.Data blob).
				var mountTypes = await db.Npcs.Include(t => t.StatCore).ToListAsync();
				Assert.Contains(mountTypes, t => string.Equals(t.Name, "Fading Black Horse", StringComparison.OrdinalIgnoreCase) && t.StatCore?.IirHeader != null);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = TestDb.Options(path);
			return new DatabaseContext(options);
		}
	}
}
