using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// `NpcStatCore.ProfessionType` is the client's own ROLE tag (`ProtoCoreCharacterIIR.NpcStats.profession_type`),
	// and #647 put the auction-house click on it. These pin the two claims that makes rest on: that the tag really is
	// a role, and that the auctioneer's value is exclusive to auctioneers.
	//
	// It matters because the alternative was a display NAME, and a name is not an identity here - "Trader" is a role
	// four templates play, and any NPC that happened to carry the string would have opened the auction house.
	public class NpcProfessionTests
	{
		// Every template tagged Auctioneer is a "Trader", and no template with any other profession is.
		//
		// Both halves are needed. Without the first, the tag could be some unrelated attribute that four Traders happen
		// to share; without the second, it could be a broad "vendor-ish" class that opens the auction house on
		// blacksmiths too. Together they say the tag IS the role.
		[Fact]
		public async Task Auctioneer_IsExactlyTheTradersAndNothingElse()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var byProfession = await db.NpcStatCores.AsNoTracking()
				.Join(db.NpcTemplates.AsNoTracking(), c => c.NpcTemplateId, t => t.TemplateId, (c, t) => new
				{
					c.ProfessionType,
					t.TemplateId,
					t.Name
				})
				.ToListAsync(ct);

			Assert.NotEmpty(byProfession);

			var auctioneers = byProfession.Where(x => x.ProfessionType == (uint)NpcProfession.Auctioneer).ToList();
			Assert.NotEmpty(auctioneers);
			Assert.All(auctioneers, a => Assert.Equal(AuctioneerName, a.Name));

			// The exclusivity half: nothing else in the world carries the tag.
			var strays = byProfession
				.Where(x => x.Name == AuctioneerName && x.ProfessionType != (uint)NpcProfession.Auctioneer)
				.Select(x => $"{x.Name} ({x.TemplateId}) profession {x.ProfessionType?.ToString() ?? "null"}")
				.ToList();

			Assert.True(strays.Count == 0, $"{strays.Count} \"{AuctioneerName}\" template(s) are not tagged Auctioneer: {string.Join(", ", strays)}");
		}

		// Every NAMED member of the partial catalog must still match templates, and each name must still describe them.
		//
		// The enum is deliberately partial and open - eight values occur and five are identified - so the risk is not
		// an unknown value appearing (that is expected) but a named one quietly going empty or picking up an unrelated
		// population after a re-harvest. The expected substring is the evidence the member was named from.
		[Theory]
		[InlineData(NpcProfession.Auctioneer, "Trader")]
		[InlineData(NpcProfession.Merchant, "")] // 112 templates, named by their own ProfessionName rather than a shared display name
		[InlineData(NpcProfession.Traveller, "")] // wagoneers + smugglers; the display names differ
		[InlineData(NpcProfession.BlackMarketeer, "Black Marketeer")]
		[InlineData(NpcProfession.Guard, "")] // Guard / Guardsman / Sentinel / City Guard
		[InlineData(NpcProfession.WayfarerOfAsura, "")] // Blaine, Tanor, Malia, Nidd, Finian - named from LDB[253], not from them
		public async Task EveryNamedProfession_StillMatchesTemplates(NpcProfession profession, string expectedName)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var names = await db.NpcStatCores.AsNoTracking()
				.Where(c => c.ProfessionType == (uint)profession)
				.Join(db.NpcTemplates.AsNoTracking(), c => c.NpcTemplateId, t => t.TemplateId, (c, t) => t.Name)
				.ToListAsync(ct);

			Assert.True(names.Count > 0, $"{profession} matches no template - the member was named from templates that are gone.");
			if (expectedName.Length > 0)
				Assert.All(names, n => Assert.Equal(expectedName, n));
		}

		// EVERY template of a NAMED profession must carry a `ProfessionName`. This is the precondition that makes
		// naming a member from the client's string honest, and it is not a formality - it caught a real over-claim
		// while #649 was being written.
		//
		// Type 4's one labelled template (Fenor) says LDB[253] "Redeemer", which is a tempting name for the type; the
		// type has NINE templates and the other eight carry nothing. Naming it from 1 of 9 rows would have described
		// Fenor and called it the role. So type 4 stays unnamed and this gate keeps the rule: a member may only be
		// named once every template of its type is labelled.
		//
		// ⚠️ It asserts the labels EXIST, not that there is exactly one id. `Guard` has two - 246974048 and the small
		// id 28 - and both resolve to the same LDB[253] string "Guard", so the client duplicates that entry. Distinct
		// ids are therefore not evidence of distinct professions, and the strings themselves cannot be checked here
		// (the suite runs without the RDB, by design). `--report professions` is where the strings are read.
		[Theory]
		[InlineData(NpcProfession.Auctioneer)]
		[InlineData(NpcProfession.WayfarerOfAsura)]
		[InlineData(NpcProfession.Guard)]
		public async Task EveryTemplateOfANamedProfession_CarriesAProfessionName(NpcProfession profession)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var cores = await db.NpcStatCores.AsNoTracking()
				.Where(c => c.ProfessionType == (uint)profession)
				.Select(c => new
				{
					c.NpcTemplateId,
					c.ProfessionName
				})
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(cores);
			var unlabelled = cores.Where(c => c.ProfessionName == null).Select(c => c.NpcTemplateId).ToList();
			Assert.True(unlabelled.Count == 0,
				$"{profession} is named from the client's profession string, but {unlabelled.Count} of its {cores.Count} template(s) carry none: "
				+ $"{string.Join(", ", unlabelled.Take(8))}");
		}

		// `ProfessionName` is the ELF hash of the profession's NAME string - the encoding, pinned by two exact hits.
		//
		// This is what identified the sibling column and is what let #649 resolve it: the hash is an LDB ID, and every
		// one of the 88 distinct values lands in LDB category 253 (the profession / shop-kind table). Two of them fall
		// out of a plain wordlist and both land exactly, which is not a coincidence at 28 bits.
		//
		// ⚠️ The column is NOT uniformly hashed - eight ids are small and sequential (1, 2, 22, 28, 44, 46, 53, 56) and
		// resolve in the same category. The hash is how MOST ids are formed, not a rule for reading the column.
		[Theory]
		[InlineData("Merchant", 210363236u)]
		[InlineData("Trainer", 193463442u)]
		public async Task ProfessionName_IsTheElfHashOfTheProfessionName(string name, uint hash)
		{
			Assert.Equal(hash, ElfHash(name));

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var carriers = await db.NpcStatCores.AsNoTracking().CountAsync(c => c.ProfessionName == hash, TestContext.Current.CancellationToken);
			Assert.True(carriers > 0, $"no template carries ProfessionName {hash} (\"{name}\") any more");
		}

		private const string AuctioneerName = "Trader";

		// The classic ELF/PJW hash: 4-bit shift, and the top nibble folded back in and cleared, so the result is 28
		// bits wide.
		private static uint ElfHash(string s)
		{
			var h = 0u;
			foreach (var b in Encoding.Latin1.GetBytes(s))
			{
				h = (h << 4) + b;
				var high = h & 0xF0000000;
				if (high != 0)
					h ^= high >> 24;

				h &= ~high;
			}

			return h;
		}
	}
}
