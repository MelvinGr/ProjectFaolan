using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
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
		[Fact]
		public async Task Import_HarvestsTheFadingBlackHorse_UnderTheMountMap()
		{
			// A committed slice of one capture: the Fading Black Horse plus a stride sample of the other creatures
			// spawned around it, so the importer still has to SELECT the wanted mount by name out of a crowd of
			// unwanted ones rather than being handed it alone. The leading MapChange is load-bearing - NpcSpawnExtractor
			// tracks the current map and drops every spawn it sees before one.
			var packets = CapturedFixture.Packets("mount-spawns.bin").Select(p => new CapturedPacket(0, true, (uint)GameInterfaceOpcodes.DispatchPassBlob, p.Iir, p.Full)).ToList();

			Assert.Equal((uint)IirTypes.PlayfieldFIIR, packets[0].SubOpcode);
			Assert.All(packets.Skip(1), p => Assert.Equal((uint)IirTypes.SimpleCharFullUpdateIIR, p.SubOpcode));

			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-mount-{Guid.NewGuid():N}.db");
			try
			{
				await using var db = NewContext(dbPath);
				await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

				var (seen, seeded) = await CaptureMountImporter.ImportAsync(db, [packets]);

				Assert.True(seen >= 1, "expected at least one mount creature harvested from the capture");
				Assert.True(seeded >= 1);

				// Mounts are now TYPES (#307), not sentinel-map NpcSpawn placements: the creature builds wholly from
				// columns via TryBuildFromType, so its type carries a lifted stat core (no NpcTemplate.Data blob).
				var mountTypes = await db.NpcTemplates.Include(t => t.StatCore).ToListAsync(TestContext.Current.CancellationToken);
				Assert.Contains(mountTypes, t => string.Equals(t.Name, "Fading Black Horse", StringComparison.OrdinalIgnoreCase) && t.StatCore?.IirHeader != null);
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = TestDb.Options(path);
			return new DatabaseContext(options);
		}
	}
}
