using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report inventory-token-check`: does every item a character is actually holding reach the wire as something
	// the client will render?
	//
	// The failure it exists for is an item whose hash was never harvested: the world-entry snapshot drops the row,
	// because it renders only entries with a non-zero hash, and a per-world-entry live re-add papers over the
	// invisible item by re-announcing "You received [X]" on every map change.
	//
	// It is a report and not a test because `ItemInstances` hangs off `Characters`, so `faolan.sql` excludes it as
	// per-developer play state: a gate over it would run on an empty table in a plain clone and could not fail. The
	// template half of the question is a test (every `Items` row mints a well-formed token, a strictly stronger
	// population); this is the half that needs the real database.
	public static partial class ReportCli
	{
		private static async Task<int> InventoryTokenCheck()
		{
			await using var db = OpenDb();

			var items = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id, i => new
			{
				i.Name,
				i.Hash
			});

			var held = await db.ItemInstances.AsNoTracking().Select(ii => new
			{
				ii.OwnerCharacterId,
				ii.Container,
				ii.Slot,
				ii.TemplateId
			}).ToListAsync();

			// An empty inventory population is a vacuous run, and a vacuous run is a failure rather than a pass: a
			// database whose characters have never been imported has nothing to say here and must not claim
			// otherwise.
			if (held.Count == 0)
			{
				Console.WriteLine("no item instances in this database - the check is vacuous, so this is a failure, not a pass."
				                  + " Import the characters (`--only characters`).");
				return 1;
			}

			// An item pointing at a template that is not in `Items` at all has no identity to mint from, so the
			// snapshot skips it and the item is invisible with nothing to explain it. The foreign key
			// (`ItemInstance.TemplateId` -> `Items`, Restrict) enforces this, so it can only fail on a database whose
			// constraints were bypassed - which `faolan.db` invites, being a file that a hand edit through
			// the sqlite3 CLI reaches with foreign keys off by default.
			var orphans = held.Where(h => !items.ContainsKey(h.TemplateId)).ToList();
			var malformed = held
				.Where(h => items.ContainsKey(h.TemplateId) && !AcgHash.IsWellFormed(AcgHash.ForWire(h.TemplateId, items[h.TemplateId].Hash)))
				.ToList();

			Console.WriteLine($"{held.Count} held item instances across {held.Select(h => h.OwnerCharacterId).Distinct().Count()} owners,"
			                  + $" over {items.Count} templates.");

			foreach (var o in orphans.Take(20))
				Console.WriteLine($"  ⚠️ character {o.OwnerCharacterId} {o.Container}[{o.Slot}] -> template {o.TemplateId}, which has no Items row");

			foreach (var m in malformed.Take(20))
				Console.WriteLine($"  ⚠️ character {m.OwnerCharacterId} {m.Container}[{m.Slot}] -> item {m.TemplateId} '{items[m.TemplateId].Name}'"
				                  + $" mints wire token 0x{AcgHash.ForWire(m.TemplateId, items[m.TemplateId].Hash):X8}, which the client would refuse");

			if (orphans.Count == 0 && malformed.Count == 0)
				Console.WriteLine("✅ every held item resolves to a template and to a wire token the client accepts.");

			return orphans.Count + malformed.Count == 0 ? 0 : 1;
		}
	}
}
