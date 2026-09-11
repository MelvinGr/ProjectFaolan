using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     Item claims and the "Redeem" window (CoreClaimIIR 0x06EC1255).
	/// </summary>
	/// <remarks>
	///     The client requests its claims (sub 0x0007) and the server answers with the list (sub 0x0001,
	///     <c>CoreClaimCodec</c>); redeeming (sub 0x0003 plus a 1-based index) grants the item to the backpack and
	///     drops the claim. The "new claims available" glow (sub 0x0009, CoreClaimNewClaimsIIR) is pushed at world
	///     entry only when the account has unseen claims. Claims are account-scoped (<c>AccountClaim</c>), seeded by
	///     the <c>.claim</c> GM command. See docs/subsystems/item-claims.md. Item grants ride the
	///     <see cref="IEconomyOutbound" /> port and system messages the chat dispatcher.
	/// </remarks>
	public sealed class ClaimService
	{
		private readonly IChatServerDispatcher _chat;
		private readonly IAccountRepository _accounts;
		private readonly ICatalogRepository _catalog;
		private readonly ICharacterInventoryRepository _inventory;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;

		public ClaimService(IAccountRepository accounts, ICatalogRepository catalog, ICharacterInventoryRepository inventory, ILogger logger, IEconomyOutbound outbound, IChatServerDispatcher chat)
		{
			_accounts = accounts;
			_catalog = catalog;
			_inventory = inventory;
			_logger = logger;
			_outbound = outbound;
			_chat = chat;
		}

		/// <summary>
		///     <c>.claim add &lt;itemId&gt; [count]</c> seeds a redeemable item claim onto the calling account's Claims
		///     window - the test bootstrap until shop and reward grants populate it - and <c>.claim list</c> shows the
		///     pending claims. Redeeming is done in-game through the client's Claims window.
		/// </summary>
		public async Task ClaimCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var accountId = client.Session.Account.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

			if (parts.Length >= 1 && parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
			{
				var rows = await _accounts.GetAccountClaims(accountId);
				if (rows.Length == 0)
				{
					await _chat.SendSystemMessage(characterId, "No pending claims.");
					return;
				}

				// One read for every listed claim's item rather than a query per row.
				var items = (await _catalog.GetItems([.. rows.Select(r => r.ItemTemplateId).Distinct()])).ToDictionary(i => i.Id);
				for (var i = 0; i < rows.Length; i++)
				{
					var qty = rows[i].Quantity?.ToString() ?? "∞"; // null = infinite charges
					await _chat.SendSystemMessage(characterId, $"#{i + 1}: {qty}x '{items.GetValueOrDefault(rows[i].ItemTemplateId)?.Name ?? "?"}' (template {rows[i].ItemTemplateId})");
				}

				return;
			}

			if (parts.Length is < 2 or > 3 || !parts[0].Equals("add", StringComparison.OrdinalIgnoreCase) ||
			    !uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
			{
				await _chat.SendSystemMessage(characterId, "Usage: .claim add <itemId> [count]  |  .claim list");
				return;
			}

			uint count = 1;
			if (parts.Length == 3 && !uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
			{
				await _chat.SendSystemMessage(characterId, "Usage: .claim add <itemId> [count]");
				return;
			}

			var template = await _catalog.GetItem(itemId);
			// Level is cosmetic - the client shows the item's own level from its RDB - so it is seeded from the Items
			// row where there is one. A count of 1 or less is an ordinary infinite item claim (null charges, shown as
			// an infinity sign); a larger count is a finite multi-charge claim.
			await _accounts.CreateAccountClaim(new AccountClaim
			{
				AccountId = accountId,
				ItemTemplateId = itemId,
				Level = template?.Level ?? 0,
				Quantity = count <= 1
					? null
					: count,
				MaxCharges = count <= 1
					? null
					: count
			});
			await _chat.SendSystemMessage(characterId, $"Added claim: {(count == 0 ? 1 : count)}x '{template?.Name ?? "?"}' (template {itemId}). Open the Claims/Redeem window to collect it.");
		}

		/// <summary>
		///     Dispatches an inbound CoreClaimIIR. The body after the 0x2000 sub-header is
		///     [receiverClass][receiverInstance][u16 sub][args].
		/// </summary>
		/// <remarks>
		///     The shared reader arrives positioned past the [iirLength][iirType] sub-header, which the carrier handler
		///     consumed, so the first field read here is the receiver class (0xC350).
		/// </remarks>
		public async Task HandleCoreClaim(INetworkClient client, FaolanStream body)
		{
			_ = body.ReadUInt32(); // receiver class (0xC350 Character)
			_ = body.ReadUInt32(); // receiver instance (the player's handle)
			var sub = (CoreClaimSubOpcode)body.ReadUInt16();

			switch (sub)
			{
				case CoreClaimSubOpcode.Request:
				{
					await SendClaimList(client);
					break;
				}

				case CoreClaimSubOpcode.Redeem:
				{
					var index = body.ReadUInt32();
					await RedeemClaim(client, index);
					break;
				}

				default:
				{
					_logger.LogDebug("Unhandled CoreClaimIIR sub 0x{Sub:X4} ({RawLength} bytes).", (ushort)sub, body.Length);
					break;
				}
			}
		}

		/// <summary>
		///     Redeems the claim at the given 1-based index into the last-sent list: grants its item to the backpack,
		///     then drops the row.
		/// </summary>
		/// <remarks>
		///     The index is resolved against the session's <c>ClaimSlots</c> snapshot rather than a fresh query, so
		///     "Claim All"'s per-row redeems all hit the right claim even as earlier ones are deleted. A claim already
		///     redeemed this batch - an index resolving to a now-deleted id - is a silent no-op.
		/// </remarks>
		private async Task RedeemClaim(INetworkClient client, uint index)
		{
			var characterId = client.Session.Character?.Id ?? 0;
			var slots = client.Session.ClaimSlots;
			if (slots == null || index < 1 || index > slots.Count)
			{
				_logger.LogWarning("Redeem for out-of-range claim index {Index} (snapshot has {Count}).", index, slots?.Count ?? 0);
				return;
			}

			var claim = await _accounts.GetAccountClaim(slots[(int)(index - 1)]);
			if (claim == null || claim.AccountId != client.Session.Account.Id)
				return; // already redeemed, or not this account's

			// Retail refuses the redeem when the player already holds one of the item, which is why a capture retries
			// the same redeem four times and gets nothing each time. Enforced on a multi-charge claim only: an infinite
			// claim has nothing to protect, since redeeming it spends nothing, whereas a charge spent on a duplicate
			// the player cannot use is burnt for good.
			if (claim.Quantity is not null && characterId != 0)
			{
				var alreadyHeld = (await _inventory.GetCharacterItems(characterId)).Any(i => i.TemplateId == claim.ItemTemplateId);
				if (alreadyHeld)
				{
					_logger.LogInformation("Redeem of claim {ClaimId} (template {Template}) refused: {Character} already holds one.", claim.Id, claim.ItemTemplateId, characterId);
					await _chat.SendSystemMessage(characterId, "You already have one of those - use it before claiming another.");
					return;
				}
			}

			// Each redeem delivers exactly one of the item: the captured DispatchAddItem carries no stat-26 stack
			// count. The claim's Type is the binding (6 account, 4 character), not item-versus-currency.
			//
			// A claim row carries the item's key but no hash - none of the captured claim lists carry one - and that is
			// no obstacle, because the item's token is minted where no capture harvested one. The only reason left to
			// keep a claim on a redeem is a full bag.
			var slot = await _outbound.GrantItemToBackpack(client, claim.ItemTemplateId);
			if (slot == null)
			{
				// Bag full, or the item template is missing: keep the claim so the player can retry.
				if (characterId != 0)
					await _chat.SendSystemMessage(characterId, "Your bag is full - free a slot and redeem the claim again.");
				return;
			}

			// An infinite claim (Quantity null) is never consumed: deliver and leave the row. The client keeps the row
			// after the DispatchAddItem, so there is nothing else to send.
			if (claim.Quantity is not { } remaining)
			{
				_logger.LogInformation("Account {AccountId} redeemed infinite claim {ClaimId} (template {Template}).", client.Session.Account.Id, claim.Id, claim.ItemTemplateId);
				return;
			}

			// A finite multi-charge claim ("Double XP" x20) is decremented and, while any remain, refreshed in place on
			// the client with the sub-0x05 ack ([remaining][max]) rather than removed. It is dropped only when the last
			// charge is spent, at which point its DispatchAddItem tells the client to remove it.
			if (remaining > 1)
			{
				await _accounts.SetAccountClaimQuantity(claim.Id, remaining - 1);
				await SendRedeemAck(client, claim, index, remaining - 1);
				_logger.LogInformation("Account {AccountId} redeemed 1 charge of claim {ClaimId} (template {Template}); {Remaining} left.", client.Session.Account.Id, claim.Id, claim.ItemTemplateId,
					remaining - 1);
				return;
			}

			await _accounts.DeleteAccountClaim(claim.Id);
			_logger.LogInformation("Account {AccountId} redeemed claim {ClaimId} (template {Template}).", client.Session.Account.Id, claim.Id, claim.ItemTemplateId);
		}

		/// <summary>
		///     Builds and sends the account's claim list (sub 0x0001) and clears the "new" glow flag: opening the
		///     window counts as seeing the claims, so later logins do not re-glow, which is what retail does.
		/// </summary>
		private async Task SendClaimList(INetworkClient client)
		{
			var accountId = client.Session.Account.Id;
			var handle = client.Session.Character.Id;
			var rows = await _accounts.GetAccountClaims(accountId);
			var entries = BuildClaimEntries(accountId, rows);

			// Remember the exact order sent, so a subsequent redeem resolves its 1-based index against this list even
			// as "Claim All" deletes rows one by one, which would otherwise shift every later index.
			client.Session.ClaimSlots = rows.Select(r => r.Id).ToList();

			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CoreClaimCodec.Build(handle, entries))));

			// Opening the window counts as seeing the claims, so clear the account's glow flag in the database and on
			// the live session copy, or a re-entry this session re-glows.
			await _accounts.SetAccountClaimsAvailable(accountId, false);
			client.Session.Account.ClaimsAvailable = false;

			_logger.LogInformation("Sent {Count} claim(s) to account {AccountId}.", entries.Count, accountId);
		}

		/// <summary>
		///     Pushes the "new claims available" glow (sub 0x0009) at world entry, but only when the account has unseen
		///     claims (<c>Account.ClaimsAvailable</c>).
		/// </summary>
		/// <remarks>
		///     Retail sends this on a fresh login with new claims and omits it once they are viewed. The session
		///     account is loaded at login, so the flag is current here.
		/// </remarks>
		public static async Task SendNewClaimsGlowIfAny(INetworkClient client)
		{
			if (!client.Session.Account.ClaimsAvailable)
				return;

			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CoreClaimCodec.Build(new CoreClaimNewClaimsIIR(client.Session.Character.Id)))));
		}

		/// <summary>
		///     Maps database rows to wire entries: 1-based indices in Id order, each carrying the item's real TEA key
		///     (Item.Key1..4 as four big-endian u32s) as the 16-byte decryption token.
		/// </summary>
		/// <remarks>
		///     The client decrypts the item template from its own RDB with that token, so a wrong or synthetic key
		///     renders the row as "No name". A claim whose item has no stored key falls back to zeros: it still lists,
		///     but does not resolve client-side. The 8-byte batch guid is display-only, constant per account and
		///     derived deterministically.
		/// </remarks>
		private static List<ClaimEntry> BuildClaimEntries(uint accountId, IReadOnlyList<AccountClaim> rows)
		{
			var guid = DeriveBytes($"claim-batch:{accountId}", ClaimEntry.BatchGuidLength);
			var entries = new List<ClaimEntry>(rows.Count);
			for (var i = 0; i < rows.Count; i++)
			{
				var row = rows[i];
				var key = ItemKeyBytes(row.Item);
				var code = string.IsNullOrEmpty(row.Code)
					? DeriveCode(row.Id)
					: row.Code;
				// Infinite (null) charges go on the wire as 0.
				entries.Add(new ClaimEntry((uint)(i + 1), row.ItemTemplateId, row.Level, code, key, guid, row.ClaimType, row.Quantity ?? 0, row.MaxCharges ?? 0));
			}

			return entries;
		}

		private static byte[] DeriveBytes(string seed, int length)
		{
			var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
			var result = new byte[length];
			Array.Copy(hash, result, length);
			return result;
		}

		// A stable 4-character A-Z0-9 code from the claim id - cosmetic, for a row that carries none.
		private static string DeriveCode(uint claimId)
		{
			const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
			var hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"claim-code:{claimId}")), 0);
			var chars = new char[4];
			for (var i = 0; i < 4; i++)
			{
				chars[i] = alphabet[(int)(hash % (uint)alphabet.Length)];
				hash /= (uint)alphabet.Length;
			}

			return new string(chars);
		}

		// The item's ProtoDecryptionKey as the on-wire 16-byte token: Key1..4 big-endian. Zeros when the item or its
		// key is absent - the client cannot decrypt it then, so it lists as "No name", but the row is still valid.
		private static byte[] ItemKeyBytes(Item? item)
		{
			var key = new byte[ClaimEntry.KeyLength];
			if (item?.Key is not { } k)
				return key;

			key.WriteU32Be(0, k.Key1);
			key.WriteU32Be(4, k.Key2);
			key.WriteU32Be(8, k.Key3);
			key.WriteU32Be(12, k.Key4);
			return key;
		}

		// The sub-0x05 refresh for a multi-charge claim: echo the redeemed row, at its 1-based list index, with its new
		// remaining amount, so the client updates that row in place. Retail refreshes this way and sends no re-list.
		private static async Task SendRedeemAck(INetworkClient client, AccountClaim claim, uint index, uint remaining)
		{
			var guid = DeriveBytes($"claim-batch:{client.Session.Account.Id}", ClaimEntry.BatchGuidLength);
			var code = string.IsNullOrEmpty(claim.Code)
				? DeriveCode(claim.Id)
				: claim.Code;
			var entry = new ClaimEntry(index, claim.ItemTemplateId, claim.Level, code, ItemKeyBytes(claim.Item), guid, claim.ClaimType, remaining, claim.MaxCharges ?? 0);

			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CoreClaimCodec.Build(new CoreClaimRedeemAckIIR(client.Session.Character.Id, entry)))));
		}
	}
}
