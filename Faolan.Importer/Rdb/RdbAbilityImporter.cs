using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Rdb
{
	// Names the Abilities catalog from the client RDB. Each ability's DEFINITION is an RDBH template in type 1000020
	// (the same TEA-encrypted catalog as item templates), and its per-template key is the ability's own Data1..4
	// quad - which CaptureAbilityImporter already harvested from the 0x2052 grant and stored on the row (identical to
	// how items store their ProtoDecryptionKey). So we decrypt each ability's 1000020 entry with its stored key and
	// pull the name via the shared RdbItemImporter.ExtractName (the tagged-string reader).
	//
	// NB the template carries the ability's effect (name/range/animation/effect-id), not its class/level REQUIREMENTS -
	// those live in the class-progression tables, not here (dumped by the ability-names report: fields 287=range,
	// 294=cast, 617=effect, 79=canonical/family id; nothing encodes required level or class).
	public static class RdbAbilityImporter
	{
		private const uint AbilityTemplateType = 1000020;

		public static async Task<(int Decrypted, int NamesSet)> ImportAsync(IDatabaseContext db, string rdbDir, string? cdnBase = null, ILogger? logger = null)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);
			var abilities = await db.Abilities.ToListAsync();

			var decrypted = 0;
			var namesSet = 0;
			foreach (var a in abilities)
			{
				if (!rdb.TryGet(AbilityTemplateType, a.Id, out var e) || e.Size == 0 || !rdb.CanRead(e))
					continue; // no template, empty sentinel, or CDN-only with no reachable mirror

				byte[] body;
				try
				{
					body = rdb.ReadDecrypted(e, [a.Key.Key1, a.Key.Key2, a.Key.Key3, a.Key.Key4]);
				}
				catch
				{
					continue; // wrong/absent key - leave the row unnamed
				}

				decrypted++;
				var name = RdbItemImporter.ExtractName(body);
				if (string.IsNullOrWhiteSpace(name) || a.Name == name)
					continue;

				a.Name = name;
				namesSet++;
			}

			if (namesSet > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("RdbAbilityImporter: {Decrypted} ability templates decrypted, {NamesSet} names set.", decrypted, namesSet);
			return (decrypted, namesSet);
		}
	}
}
