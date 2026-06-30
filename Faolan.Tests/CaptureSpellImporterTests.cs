using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// A real captured ApplySpell (0x2000 / 0x6B87E316) must yield the cast spell id (0x3dfa48 = 4061768) and
	// catalog it in Spells if absent.
	public class CaptureSpellImporterTests
	{
		// ApplySpell packet from NecroL80_mage-necromancer_login_kingconanscastle (spell id 0x3dfa48).
		private const string ApplySpellHex =
			"000000731bd646d2000000170a070d13ce71b11056120a0d47c1676c10d4cb8b402000000000506b87e3160000c3500802e5d400003dfa48000000c3500802e5d400000000000000000000000100010000000100000000003dfa480000005000000000000000000000000000000050000000001534b945";

		// A real-format DispatchLearnSpell grant (#73): GameCharAgent 0xa9ecb95d -> GameCharInterface 0x8b87f791
		// (receiver instance = DemoL80's character id 0x110099bd), body = [u32 spellId 0x4213cd]. Framing matches
		// TsharkCaptureReader.Rebuild; the CRC is a placeholder (ConanPacket does not enforce it, and the importer
		// filters on opcode/class-pair/body only).
		private const string LearnSpellHex =
			"0000002400000000000000180a070d5db9eca9105a120b0d91f7878b10bdb3828801200a004213cd";
		private const uint DemoL80Id = 0x110099bd;

		[Fact]
		public async Task ImportLearnedSpells_SeedsTargetedCharacterOnly()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-learn-{Guid.NewGuid():N}.db");
			try
			{
				var packet = Convert.FromHexString(LearnSpellHex);

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					db.Accounts.Add(new Account { Id = 1, UserName = "t", Password = "t", AuthChallenge = "", LastIpAddress = "" });
					db.Realms.Add(new Realm { Id = 1, Name = "Test", IpAddress = "127.0.0.1" });
					db.Maps.Add(new Map { Id = Map.TortageBeachId, Name = "Beach" });
					await db.SaveChangesAsync();
					// DemoL80 is the grant's target; the other character must NOT receive the spell.
					db.Characters.Add(new Character { Id = DemoL80Id, Name = "DemoL80", AccountId = 1, RealmId = 1 });
					db.Characters.Add(new Character { Id = 0xDEAD, Name = "Other", AccountId = 1, RealmId = 1 });
					await db.SaveChangesAsync();

					var (characters, inserted) = await CaptureSpellImporter.ImportLearnedSpellsAsync(db, [packet]);
					Assert.Equal(1, characters);
					Assert.Equal(1, inserted);
				}

				await using (var db = NewContext(dbPath))
				{
					Assert.Equal([0x4213cdu], await db.CharacterSpells.Where(s => s.CharacterId == DemoL80Id).Select(s => s.SpellId).ToListAsync());
					Assert.Empty(db.CharacterSpells.Where(s => s.CharacterId == 0xDEAD));
				}

				// Idempotent: a second import of the same grant adds nothing.
				await using (var db = NewContext(dbPath))
				{
					var (_, inserted) = await CaptureSpellImporter.ImportLearnedSpellsAsync(db, [packet]);
					Assert.Equal(0, inserted);
				}
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		[Fact]
		public async Task ImportLearnedSpells_SkipsCharactersNotInDb()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-learn-{Guid.NewGuid():N}.db");
			try
			{
				await using var db = NewContext(dbPath);
				await db.Database.EnsureCreatedAsync(); // no characters seeded
				var (characters, inserted) = await CaptureSpellImporter.ImportLearnedSpellsAsync(db, [Convert.FromHexString(LearnSpellHex)]);
				Assert.Equal(0, characters);
				Assert.Equal(0, inserted);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		[Fact]
		public async Task Import_CatalogsCastSpellId()
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-spell-{Guid.NewGuid():N}.db");
			try
			{
				var packet = Convert.FromHexString(ApplySpellHex);

				await using (var db = NewContext(dbPath))
				{
					await db.Database.EnsureCreatedAsync();
					var (seen, inserted) = await CaptureSpellImporter.ImportAsync(db, [packet]);
					Assert.Equal(1, seen);
					Assert.Equal(1, inserted);
				}

				await using (var db = NewContext(dbPath))
				{
					var spell = await db.Spells.FindAsync(4061768u); // 0x3dfa48
					Assert.NotNull(spell);
				}
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}

		private static DatabaseContext NewContext(string path)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}", x => x.MigrationsAssembly("Faolan"))
				.Options;
			return new DatabaseContext(options);
		}
	}
}
