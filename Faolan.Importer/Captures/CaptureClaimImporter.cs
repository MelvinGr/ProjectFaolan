using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Imports the account's Claims/Redeem list (CoreClaimIIR 0x06EC1255, sub 0x0001) into AccountClaim rows. The item
	// IDENTITIES those claims reference (the per-item TEA key) are harvested separately, into Items, by the items
	// module (ItemHashHarvester's claim-list source) - so this must run after it. Attaches to the shared "captured"
	// account (where imported captured characters live); a clean rebuild per account (overwrite). See
	// docs/subsystems/item-claims.md.
	public static class CaptureClaimImporter
	{
		// Every claim row recovered from a capture's CoreClaimIIR list (deduped by template - one capture is a single
		// account snapshot).
		public static IReadOnlyList<ClaimEntry> Extract(IEnumerable<byte[]> rawPackets)
		{
			var byTemplate = new Dictionary<uint, ClaimEntry>();
			foreach (var bytes in rawPackets)
			{
				byte[] payload;
				try
				{
					var packet = new ConanPacket(bytes);
					if (packet.Opcode != 0x2000)
						continue;

					payload = packet.Body.ReadArrayUInt32Length();
				}
				catch
				{
					continue;
				}

				if (!CoreClaimListCodec.TryParse(payload, out _, out var claims))
					continue;

				foreach (var claim in claims)
					byTemplate[claim.ItemTemplateId] = claim;
			}

			return byTemplate.Values.OrderBy(c => c.Index).ToList();
		}

		// Rebuilds `accountId`'s claims from the captured list (clears the account's existing rows first). Returns
		// (parsed, written).
		public static async Task<(int Parsed, int Written)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, uint accountId, ILogger? logger = null)
		{
			var claims = Extract(rawPackets);
			if (claims.Count == 0)
				return (0, 0);

			var stale = await db.AccountClaims.Where(c => c.AccountId == accountId).ToListAsync();
			db.AccountClaims.RemoveRange(stale);

			foreach (var claim in claims)
			{
				db.AccountClaims.Add(new AccountClaim
				{
					AccountId = accountId,
					ItemTemplateId = claim.ItemTemplateId,
					Level = claim.Level,
					// Wire 0 = infinite -> null charges.
					Quantity = claim.Quantity == 0
						? null
						: claim.Quantity,
					MaxCharges = claim.MaxQuantity == 0
						? null
						: claim.MaxQuantity,
					Code = claim.Code,
					ClaimType = claim.Type
				});
			}

			// Flag the account's glow (one per account, not per claim) so the client lights the button next login.
			await db.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClaimsAvailable, true));

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureClaimImporter: wrote {Count} claims to account {AccountId} (cleared {Stale}).", claims.Count, accountId, stale.Count);
			return (claims.Count, claims.Count);
		}
	}
}
