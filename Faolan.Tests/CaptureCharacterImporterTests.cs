using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;
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
					new CapturedPacket(2, true, 0x2000, (uint)IirTypes.FullPlayerCharacterIIR, SnapshotPacket(dikketestHandle))
				};

				CaptureCharacterImporter.AccountId = 7;
				(int, int, int, int) result;
				try
				{
					result = await CaptureCharacterImporter.ImportAsync(db, [capture]);
				}
				finally
				{
					CaptureCharacterImporter.AccountId = 0;
				}

				Assert.Equal((1, 1, 0, 0), result);

				var character = await db
					.Characters.Include(c => c.Body)
					.Include(c => c.Skills)
					.Include(c => c.BodyResources)
					.SingleAsync(c => c.Name == "Dikketest", TestContext.Current.CancellationToken);
				Assert.Equal(0x01316D2Bu, character.Id); // the ORIGINAL retail id (the spawn handle)
				Assert.Equal(7u, character.AccountId);
				Assert.Equal(Sex.Female, character.Sex); // dikketest: editor ChangeSex 6 -> wire 3 = female
				// Race 1. This assertion used to read Aquilonian and was written from the enum, which had 1 and 2 the
				// wrong way round (#469). It is now the PROOF that they were: Dikketest's captured head mesh 1142847
				// occurs in exactly one appearance list - the female heads under CHARACTERCREATOR's <cimmerian>
				// element - and her captured race byte is 1. That ties wire race 1 to Cimmerian without going through
				// the enum at all.
				Assert.Equal(Race.Cimmerian, character.Race);
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

				// The capture's own vitals are deliberately NOT carried: vitals are derived from (Class, Level) so an
				// imported character is no different from a created one (#578).
				Assert.Equal(CharacterMaxStats.For(character.Class, character.Level, VitalAttributes.LevelOneBase).Health, character.MaxVitals(VitalAttributes.LevelOneBase).Health);

				// The spawn's 7 morph entries must reach CharacterBody, which IS the appearance now: it is what
				// Faolan.Web renders from and what the spawn's entry table is regenerated from. They used to stay at
				// zero because the entry table was stored alongside them, so an imported character looked right in game
				// (the spawn replayed those rows) and unmorphed everywhere else - and the first appearance edit rebuilt
				// the table from those zeros and lost the captured face.
				// BuildEntries skips a zero slider, so 7 entries means all 7 body sliders are non-zero.
				float[] bodyMorphs = [character.Body!.Arm, character.Body.Chest, character.Body.Bosom, character.Body.Stomach, character.Body.Arse, character.Body.Thigh, character.Body.Leg];
				Assert.All(bodyMorphs, m => Assert.NotEqual(0f, m));
				Assert.Equal(7, CharacterMorphs.BuildEntries(character.Body).Count);

				// The weighted entries are recovered as resource refs (the inverse of BuildWeightedEntries, which maps
				// a ref to its MorphTarget's Id = the entry tag). This test seeds no MorphTargets, so NOTHING should
				// resolve - that is the property worth pinning here: recovery is driven entirely by the catalog and
				// never invents a ref. The real inversion is exercised against the shipped catalog by
				// PlayerSpawnScaffoldRowsTests, where every captured tag resolves.
				Assert.Empty(character.BodyResources);

				// His 20 captured ability/feat ids plus the archetype ability (300, Rogue) that opens every player's
				// skill list - a real element of the list under the client's carve (#466), so the extract keeps it.
				Assert.Equal(21, character.Skills.Count);
				Assert.Contains(character.Skills, sk => sk.SkillId == 300);

				// Re-run: nothing added, nothing changed.
				var rerun = await CaptureCharacterImporter.ImportAsync(db, [capture]);
				Assert.Equal((1, 0, 1, 0), rerun);
				Assert.Equal(1, await db.Characters.CountAsync(c => c.Name == "Dikketest", TestContext.Current.CancellationToken));

				// #753: the row carries the marker that makes it refreshable, and a REFRESH run rebuilds it - one
				// character still, but deleted and re-imported rather than skipped.
				Assert.True(character.ImportedFromCapture);
				var refreshed = await CaptureCharacterImporter.ImportAsync(db, [capture], refresh: true);
				Assert.Equal((1, 1, 0, 1), refreshed);
				Assert.Equal(1, await db.Characters.CountAsync(c => c.Name == "Dikketest", TestContext.Current.CancellationToken));

				// ...and a character created IN-GAME is exempt by construction: no marker, so even a refresh keeps
				// it. This is the `Adada` case - a blanket re-import deleted a level-4 local character, and the level
				// threshold that was supposed to be the guard is what deleted it.
				db.Characters.Add(new Character
				{
					AccountId = 7,
					RealmId = character.RealmId,
					Name = "Dikketest",
					Level = 4,
					Class = ConanClass.Barbarian,
					Race = Race.Cimmerian,
					Sex = Sex.Male,
					MapId = 4000
				});
				db.Characters.Remove(await db.Characters.SingleAsync(c => c.ImportedFromCapture, TestContext.Current.CancellationToken));
				await db.SaveChangesAsync(TestContext.Current.CancellationToken);

				var kept = await CaptureCharacterImporter.ImportAsync(db, [capture], refresh: true);
				Assert.Equal((1, 0, 1, 0), kept);
				var local = await db.Characters.SingleAsync(c => c.Name == "Dikketest", TestContext.Current.CancellationToken);
				Assert.False(local.ImportedFromCapture);
				Assert.Equal(4, local.Level); // untouched - not rebuilt from the capture
			}
			finally
			{
				TestDb.Delete(dbPath);
			}
		}

		// A REAL PlayfieldFIIR, built by the codec - PlayfieldFCodec.Parse reads the record whole (#530 rule 3), so a
		// 40-byte stub with the map id at an offset is no longer a MapChange.
		private static byte[] MapChangePacket(uint mapId)
		{
			var record = PlayfieldFCodec.Build(Vector3.Zero, mapId, 0, 1, 0x01EA7B9C, 15f, 13.86f, 0);
			byte[] body = [0, 0, 0, 0, .. record];
			body.WriteU32Be(0, (uint)record.Length);
			return WrapBody(body);
		}

		// A minimal personal FullChar snapshot (0x737A6DF9) addressed to `instance` - enough for the importer to key
		// it (subop at offset 4, instance at offset 12) and recognize the spawn as the local player. The inventory
		// body is empty/garbage, which ImportCarriedInventory parses to "no carried items" without throwing.
		private static byte[] SnapshotPacket(uint instance)
		{
			var body = new byte[40];
			body.WriteU32Be(0, (uint)body.Length - 4);
			body.WriteU32Be(4, (uint)IirTypes.FullPlayerCharacterIIR);
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
