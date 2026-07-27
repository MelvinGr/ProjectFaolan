using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The captured-character importer over the dikketest golden spawn: one run materializes the character
	// (identity, sex via the wire code MALE=2/FEMALE=3, looks in editor 2x units, map from MapChange
	// tracking, scaffold rows, equipment for DB items), a second run skips it (names are never duplicated
	// or overwritten).
	public class CaptureCharacterImporterTests
	{
		[Fact]
		public async Task Import_MaterializesTheGoldenCharacter_AndSkipsOnRerun()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-charimp-{Guid.NewGuid():N}.db");
			try
			{
				var options = TestDb.Options(dbPath);
				await using var db = new DatabaseContext(options);
				await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
				db.Accounts.Add(new Account
				{
					Id = 7,
					UserName = "t",
					Password = "t",
					AuthChallenge = "",
					LastIpAddress = ""
				});
				db.Maps.Add(new Map
				{
					Id = 4000,
					Name = "Slave Galley"
				});
				await db.SaveChangesAsync(TestContext.Current.CancellationToken);

				const uint dikketestHandle = 0x01316D2B; // the spawn handle = on-wire instance = the imported char id

				// One capture: a MapChange to 4000, the golden dikketest spawn (a real captured body), then a
				// personal FullChar snapshot for the same instance. The snapshot is the "this is the capturing
				// account's own played character" signal the importer's own-character prune requires (0x737A6DF9 is
				// only ever sent for the local player); without it the spawn is dropped as an other-player sighting.
				var capture = new[]
				{
					new CapturedPacket(0, true, 0x2000, 0x5D85BFC7, MapChangePacket(4000)), new CapturedPacket(1, true, 0x2000, 0xFA015807, WrapBody(Golden.Bytes("SpawnDikketestLogin"))),
					new CapturedPacket(2, true, 0x2000, (uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR, SnapshotPacket(dikketestHandle))
				};

				CaptureCharacterImporter.AccountId = 7;
				(int, int, int) result;
				try
				{
					result = await CaptureCharacterImporter.ImportAsync(db, [capture]);
				}
				finally
				{
					CaptureCharacterImporter.AccountId = 0;
				}

				Assert.Equal((1, 1, 0), result);

				var character = await db
					.Characters.Include(c => c.MaxStats)
					.Include(c => c.Body)
					.Include(c => c.Attributes)
					.Include(c => c.Skills)
					.SingleAsync(c => c.Name == "Dikketest", TestContext.Current.CancellationToken);
				Assert.Equal(0x01316D2Bu, character.Id); // the ORIGINAL retail id (the spawn handle)
				Assert.Equal(7u, character.AccountId);
				Assert.Equal(Sex.Female, character.Sex); // dikketest: editor ChangeSex 6 -> wire 3 = female
				Assert.Equal(Race.Aquilonian, character.Race);
				Assert.Equal(18u, (uint)character.Class); // the pre-frame island's low byte @135/136
				Assert.Equal(4000u, character.MapId);
				// RealmId is required; the importer find-or-creates the server's realm (none seeded here).
				Assert.Equal(await db.Realms.OrderBy(r => r.Id).Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken), character.RealmId);
				Assert.Equal(1142847u, character.Body!.HeadMesh); // the TRUE wire value (editor sent 2285694 = 2x)
				Assert.Equal(322.5f, character.PositionX); // the Tortage beach intro spawn (ProtoVec3 fields)
				Assert.Equal(145.3113250732f, character.PositionY, 0.001f);
				Assert.Equal(704.9000244141f, character.PositionZ, 0.001f);
				// The facing is decoded from the spawn's ProtoQuat (X/Y/Z/W); a captured quaternion is unit-length,
				// which confirms all four components (incl. the new RotationW) were read - not the W=1 fresh default.
				Assert.Equal(1f, character.Rotation.LengthSquared(), 0.001f);
				Assert.NotEqual(1f, character.RotationW); // a real captured W, not the identity fallback
				Assert.NotNull(character.MaxStats);
				Assert.Equal(14, character.Attributes.Count); // 6 weighted + 7 morphs + closer
				Assert.Equal(20, character.Skills.Count);

				// Re-run: nothing added, nothing changed.
				var rerun = await CaptureCharacterImporter.ImportAsync(db, [capture]);
				Assert.Equal((1, 0, 1), rerun);
				Assert.Equal(1, await db.Characters.CountAsync(c => c.Name == "Dikketest", TestContext.Current.CancellationToken));
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		private static byte[] MapChangePacket(uint mapId)
		{
			var body = new byte[40];
			body.WriteU32Be(0, (uint)body.Length - 4);
			body.WriteU32Be(4, 0x5D85BFC7);
			body.WriteU32Be(34, mapId);
			return WrapBody(body);
		}

		// A minimal personal FullChar snapshot (0x737A6DF9) addressed to `instance` - enough for the importer to key
		// it (subop at offset 4, instance at offset 12) and recognize the spawn as the local player. The inventory
		// body is empty/garbage, which ImportCarriedInventory parses to "no carried items" without throwing.
		private static byte[] SnapshotPacket(uint instance)
		{
			var body = new byte[40];
			body.WriteU32Be(0, (uint)body.Length - 4);
			body.WriteU32Be(4, (uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR);
			body.WriteU32Be(12, instance);
			return WrapBody(body);
		}

		// Wraps a raw packet body (incl. its leading u32 length) in the Conan framing the importer expects:
		// [len4][crc4][headerLen4=0][body].
		private static byte[] WrapBody(byte[] body)
		{
			var packet = new byte[12 + body.Length];
			packet.WriteU32Be(0, (uint)(packet.Length - 4));
			body.CopyTo(packet, 12);
			return packet;
		}
	}
}
