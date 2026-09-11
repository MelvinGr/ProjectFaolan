using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Imports the account's Claims/Redeem list (CoreClaimIIR, sub 0x0001) into AccountClaim rows. See
	///     docs/subsystems/item-claims.md.
	/// </summary>
	/// <remarks>
	///     The item identities those claims reference (the per-item TEA key) are harvested separately, into Items, by
	///     the items module (ItemHashHarvester's claim-list source) - so this must run after it. Attaches to the import
	///     account (where imported captured characters live); a clean rebuild per account (overwrite).
	/// </remarks>
	public static class CaptureClaimImporter
	{
		/// <summary>
		///     Every claim row recovered from a capture's CoreClaimIIR list (deduped by template - one capture is a single
		///     account snapshot).
		/// </summary>
		public static IReadOnlyList<ClaimEntry> Extract(IEnumerable<byte[]> rawPackets)
		{
			var byTemplate = new Dictionary<uint, ClaimEntry>();
			foreach (var bytes in rawPackets)
			{
				byte[] payload;
				try
				{
					var packet = Rpc.Parse(bytes);
					if (packet.Method != (uint)GameAgentMethods.PassBlob)
						continue;

					payload = packet.BodyReader().ReadArrayUInt32Length();
				}
				catch
				{
					continue;
				}

				CoreClaimIIR parsed;
				try
				{
					parsed = CoreClaimCodec.Parse(payload);
				}
				catch (CodecParseException)
				{
					continue;
				}

				if (parsed is not CoreClaimListIIR list)
					continue;

				foreach (var claim in list.Claims)
					byTemplate[claim.ItemTemplateId] = claim;
			}

			return byTemplate.Values.OrderBy(c => c.Index).ToList();
		}

		/// <summary>
		///     Rebuilds <paramref name="accountId" />'s claims from the captured list (clears the account's existing rows
		///     first).
		/// </summary>
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
