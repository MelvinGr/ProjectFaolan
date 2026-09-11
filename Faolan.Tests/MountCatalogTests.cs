using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// The summonable-mount protocol and the shipped mount catalog. The catalog is the `Mounts` table, lifted whole
	// from the client's own data (RDB spell field 186), so the identity half of this gate reads the committed
	// faolan.db.
	public class MountCatalogTests
	{
		[Fact]
		public void FadingBlackHorse_IsInTheShippedCatalog_WithItsCapturedCreatureId()
		{
			var mounts = ShippedMounts();

			// The two mount ids the NecroL80 mount/dismount captures show the client sending as the SummonMount
			// action's `from` word. 0x3E34F1's creature id is the one retail pushed in the frame-520 StatUpd
			// (stat 0x0BA = 0x00313C9B) - the capture and the client's own RDB catalog agreeing on the same number
			// from two directions is what makes the import a mirror of retail rather than a guess.
			Assert.Equal(("Fading Black Horse", 0x00313C9Bu), mounts[0x3E34F1u]);
			Assert.Equal(("Swift Fading Black Horse", 0x00313C9Fu), mounts[0x3E34F2u]);
		}

		[Fact]
		public void TheCatalog_IsTheWholeClientList_KeyedByTheSummonSpell()
		{
			var mounts = ShippedMounts();

			// 155 records of RDB type 1040005 carry field 186. All of them are here: a mount id is its summon
			// spell's record id, so nothing is dropped for want of a key.
			Assert.Equal(155, mounts.Count);

			// The creature id is not unique and must never be used as the key - two "Killer Rhino" summons name one
			// creature, and three "Ridden Death" summons name three different ones.
			Assert.True(mounts.Count > new HashSet<uint>(mounts.Values.Select(m => m.CreatureId)).Count);
		}

		[Fact]
		public void Quality_IsTheOnlyVariantDiscriminator_AndIsARarityLadder()
		{
			// No speed field distinguishes "Swift <horse>" from "<horse>": field 499 is the only field that differs,
			// and it is the same 0-indexed ItemQuality ladder an item template
			// carries. A Swift horse is Enchanted; its plain and Azure-barded twins carry no tier at all.
			var quality = ShippedQuality();
			Assert.Null(quality[0x3E34F1u]); // Fading Black Horse
			Assert.Equal(ItemQuality.Enchanted, quality[0x3E34F2u]); // Swift Fading Black Horse
			Assert.Null(quality[4076787u]); // Azure-barded Fading Black Horse

			// The ladder is sparse in exactly the way rarity is and a speed would not be: nothing is Superior, and
			// the top of it is the Legendary end of the mount store (Blood of Zath, Abomination of Selkhet).
			Assert.DoesNotContain(ItemQuality.Superior, quality.Values);
			Assert.Equal(ItemQuality.Legendary, quality[5069286u]); // Blood of Zath
		}

		[Fact]
		public void RideBuffPayload_ReproducesTheCapturedFrame520Blob()
		{
			// The verbatim 54-byte ride-buff payload from the mount capture (frame 520): duration@8 = 8_640_000 cs
			// (24 h), spell base 0x31EB15 @22, casting level 80, and the @46 u64 expiry stamp 0x1560F9BB. Now
			// constructed via MagicBuffDatas.Apply instead of a verbatim blob.
			//
			// That stamp is session state, not a catalog constant: 0x1560F9BB ms = 4.15 days of that retail server's
			// uptime, which is when the 24 h buff would have ended. It is passed in here because this test
			// reproduces one captured send; the live sender takes it from ServerUptimeClock.
			byte[] captured =
			[
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x83, 0xD6, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x31, 0xEB, 0x15, 0x00, 0x00, 0x00, 0x50,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00, 0x00, 0x15, 0x60, 0xF9, 0xBB
			];

			Assert.Equal(captured, MagicBuffDataCodec.Build(MountCatalog.RideBuffPayload(80, 0x1560F9BB))); // the mount capture is a level-80 character
		}

		// (mount id -> name + creature id) straight out of the committed faolan.db.
		private static Dictionary<uint, (string Name, uint CreatureId)> ShippedMounts()
		{
			var rows = new Dictionary<uint, (string, uint)>();
			foreach (var (id, name, creature, _) in ReadCatalog())
				rows[id] = (name, creature);
			return rows;
		}

		private static Dictionary<uint, ItemQuality?> ShippedQuality()
		{
			var rows = new Dictionary<uint, ItemQuality?>();
			foreach (var (id, _, _, quality) in ReadCatalog())
				rows[id] = quality;
			return rows;
		}

		private static List<(uint Id, string Name, uint CreatureId, ItemQuality? Quality)> ReadCatalog()
		{
			using var con = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = "SELECT m.Id, s.Name, m.CreatureId, m.Quality FROM Mounts m JOIN Spells s ON s.Id = m.Id";
			using var r = cmd.ExecuteReader();
			var rows = new List<(uint, string, uint, ItemQuality?)>();
			while (r.Read())
				rows.Add(((uint)r.GetInt64(0), r.GetString(1), (uint)r.GetInt64(2), r.IsDBNull(3)
					? null
					: (ItemQuality)r.GetInt64(3)));

			return rows;
		}
	}
}
