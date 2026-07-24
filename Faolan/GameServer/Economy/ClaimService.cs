using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	// Item claims / "Redeem" window (CoreClaimIIR 0x06EC1255). The client requests its claims (sub 0x0007), the
	// server answers with the list (sub 0x0001, CoreClaimListCodec); redeeming (sub 0x0003 + 1-based index) grants
	// the item to the backpack and drops the claim. The "new claims available" glow (sub 0x0009, CoreClaimListCodec.BuildNewClaimsGlow) is
	// pushed at world entry only when the account has unseen claims. Claims are account-scoped (AccountClaim), seeded
	// by the .claim GM command for now. See docs/subsystems/item-claims.md. Extracted from GameServerListener as a
	// composed collaborator (#200): item-grant rides the IEconomyOutbound port, system messages the chat dispatcher.
	public sealed class ClaimService
	{
		private readonly IChatServerDispatcher _chat;
		private readonly IDatabaseRepository _database;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;

		public ClaimService(IDatabaseRepository database, ILogger logger, IEconomyOutbound outbound, IChatServerDispatcher chat)
		{
			_database = database;
			_logger = logger;
			_outbound = outbound;
			_chat = chat;
		}

		// .claim add <itemId> [count]  - seed a redeemable item claim onto the calling account's Claims window (the
		// test bootstrap until shop/reward grants populate it). .claim list - show the pending claims. Redeem in-game
		// via the client's Claims/Redeem window.
		public async Task ClaimCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var accountId = client.Session.Account.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

			if (parts.Length >= 1 && parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
			{
				var rows = await _database.GetAccountClaims(accountId);
				if (rows.Length == 0)
				{
					await _chat.SendSystemMessage(characterId, "No pending claims.");
					return;
				}

				for (var i = 0; i < rows.Length; i++)
				{
					var item = await _database.GetItem(rows[i].ItemTemplateId);
					var qty = rows[i].Quantity?.ToString() ?? "∞"; // null = infinite charges
					await _chat.SendSystemMessage(characterId, $"#{i + 1}: {qty}x '{item?.Name ?? "?"}' (template {rows[i].ItemTemplateId})");
				}

				return;
			}

			if (parts.Length is < 2 or > 3 || !parts[0].Equals("add", StringComparison.OrdinalIgnoreCase) ||
			    !uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
			{
				await _chat.SendSystemMessage(characterId, "Usage: .claim add &lt;itemId&gt; [count]  |  .claim list");
				return;
			}

			uint count = 1;
			if (parts.Length == 3 && !uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
			{
				await _chat.SendSystemMessage(characterId, "Usage: .claim add &lt;itemId&gt; [count]");
				return;
			}

			var template = await _database.GetItem(itemId);
			// Level is cosmetic (the client shows the item's own level from its RDB); seed it from our Items row if we
			// have it. count <= 1 = an ordinary INFINITE item claim (null charges, shown as ∞); count > 1 = a finite
			// multi-charge claim.
			await _database.CreateAccountClaim(new AccountClaim
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

		// Dispatch an inbound CoreClaimIIR. Body (after the 0x2000 sub-header) = [receiverClass][receiverInstance][u16 sub][args].
		// The shared reader arrives positioned PAST the [pbLength][pbOpcode] sub-header (Handle0X2000 consumed it), so the
		// first field here is the receiver class (0xC350), matching the docs layout - no re-read from position 0.
		public async Task HandleCoreClaim(INetworkClient client, FaolanStream body)
		{
			_ = body.ReadUInt32(); // receiver class (0xC350 Character)
			_ = body.ReadUInt32(); // receiver instance (the player's handle)
			var sub = body.ReadUInt16();

			switch (sub)
			{
				case CoreClaimListCodec.SubRequest: // 0x0007 - "send me my claims"
					await SendClaimList(client);
					break;

				case CoreClaimListCodec.SubRedeem: // 0x0003 - redeem the 1-based claim index
					var index = body.ReadUInt32();
					await RedeemClaim(client, index);
					break;

				default:
					_logger.LogDebug("Unhandled CoreClaimIIR sub 0x{Sub:X4} ({RawLength} bytes).", sub, body.Length);
					break;
			}
		}

		// Redeem the claim at the given 1-based index into the LAST-SENT list: grant its item to the backpack, then drop
		// the row. The index is resolved against the session's ClaimSlots snapshot (not a fresh query) so "Claim All"'s
		// per-row redeems all hit the right claim even as earlier ones are deleted. A claim already redeemed this batch
		// (index resolves to a now-deleted id) is a silent no-op.
		private async Task RedeemClaim(INetworkClient client, uint index)
		{
			var characterId = client.Session.Character?.Id ?? 0;
			var slots = client.Session.ClaimSlots;
			if (slots == null || index < 1 || index > slots.Count)
			{
				_logger.LogWarning("Redeem for out-of-range claim index {Index} (snapshot has {Count}).", index, slots?.Count ?? 0);
				return;
			}

			var claim = await _database.GetAccountClaim(slots[(int)(index - 1)]);
			if (claim == null || claim.AccountId != client.Session.Account.Id)
				return; // already redeemed (or not this account's) - no-op

			// Retail refuses the redeem when the player ALREADY HOLDS one of the item (#360) - it is why the capture
			// retried the same redeem four times and got nothing each time. Enforced on a MULTI-CHARGE claim only: an
			// infinite claim has nothing to protect (redeeming it never spends anything), whereas letting a charge be
			// spent on a duplicate the player cannot use burns it for good.
			if (claim.Quantity is not null && characterId != 0)
			{
				var alreadyHeld = (await _database.GetCharacterInventory(characterId)).Any(i => i.Item?.Id == claim.ItemTemplateId);
				if (alreadyHeld)
				{
					_logger.LogInformation("Redeem of claim {ClaimId} (template {Template}) refused: {Character} already holds one.", claim.Id, claim.ItemTemplateId, characterId);
					await _chat.SendSystemMessage(characterId, "You already have one of those - use it before claiming another.");
					return;
				}
			}

			// Each redeem delivers exactly ONE of the item (proven by the capture: the delivered DispatchAddItem carries
			// no stat-26 stack count). The Type is the BINDING (6 account / 4 character), not item-vs-currency. Claim
			// items carry a key but no hash, so allow the hashless live add.
			var slot = await _outbound.GrantItemToBackpack(client, claim.ItemTemplateId, allowHashless: true);
			if (slot == null)
			{
				// Bag full (or the item template is missing) - keep the claim so the player can retry.
				if (characterId != 0)
					await _chat.SendSystemMessage(characterId, "Your bag is full - free a slot and redeem the claim again.");
				return;
			}

			// Infinite claim (Quantity == null, the ∞ Amount): never consumed - deliver and leave the row. The client
			// keeps an ∞ row after the DispatchAddItem, so nothing else to send.
			if (claim.Quantity is not { } remaining)
			{
				_logger.LogInformation("Account {AccountId} redeemed infinite claim {ClaimId} (template {Template}).", client.Session.Account.Id, claim.Id, claim.ItemTemplateId);
				return;
			}

			// Finite multi-charge claim ("Double XP" x20): decrement and, while any remain, refresh the client's row in
			// place with the sub-0x05 ack ([remaining][max]) instead of removing it. Dropped only when the last charge
			// is spent (its DispatchAddItem then tells the client to remove it).
			if (remaining > 1)
			{
				await _database.SetAccountClaimQuantity(claim.Id, remaining - 1);
				await SendRedeemAck(client, claim, index, remaining - 1);
				_logger.LogInformation("Account {AccountId} redeemed 1 charge of claim {ClaimId} (template {Template}); {Remaining} left.", client.Session.Account.Id, claim.Id, claim.ItemTemplateId,
					remaining - 1);
				return;
			}

			await _database.DeleteAccountClaim(claim.Id);
			_logger.LogInformation("Account {AccountId} redeemed claim {ClaimId} (template {Template}).", client.Session.Account.Id, claim.Id, claim.ItemTemplateId);
		}

		// Build + send the account's claim list (sub 0x0001) and clear the "new" glow flag - opening the window counts
		// as seeing the claims, so subsequent logins don't re-glow (matches retail).
		private async Task SendClaimList(INetworkClient client)
		{
			var accountId = client.Session.Account.Id;
			var handle = client.Session.Character.Id;
			var rows = await _database.GetAccountClaims(accountId);
			var entries = BuildClaimEntries(accountId, rows);

			// Remember the exact order we sent, so a subsequent redeem resolves its 1-based index against THIS list even
			// as "Claim All" deletes rows one by one (which would otherwise shift every later index).
			client.Session.ClaimSlots = rows.Select(r => r.Id).ToList();

			await client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CoreClaimListCodec.Build(handle, entries))));

			// Opening the window counts as seeing the claims - clear the account's glow flag (DB + the live session copy
			// so a re-entry this session doesn't re-glow).
			await _database.SetAccountClaimsAvailable(accountId, false);
			client.Session.Account.ClaimsAvailable = false;

			_logger.LogInformation("Sent {Count} claim(s) to account {AccountId}.", entries.Count, accountId);
		}

		// Push the "new claims available" glow (sub 0x0009) at world entry, but only when the account has UNSEEN claims
		// (Account.ClaimsAvailable) - retail sends this on a fresh login with new claims and omits it once viewed. The
		// session account is loaded at login, so the flag is current here.
		public static async Task SendNewClaimsGlowIfAny(INetworkClient client)
		{
			if (!client.Session.Account.ClaimsAvailable)
				return;

			await client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CoreClaimListCodec.BuildNewClaimsGlow(client.Session.Character.Id))));
		}

		// Map DB rows to wire entries: assign 1-based indices in Id order and, per row, emit the item's real TEA key
		// (Item.Key1..4 as 4×u32 big-endian) as the 16-byte decryption token - the client decrypts the item template
		// from its own RDB with it, so a wrong/synthetic key renders the row as "No name". A claim whose item has no
		// stored key falls back to zeros (it still lists, just won't resolve client-side). The batch guid (8B) is
		// display-only and constant per account, derived deterministically.
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
				// Infinite (null) charges go on the wire as 0 (the ∞ Amount).
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

		// A stable 4-char A-Z0-9 code from the claim id (cosmetic, when a row carries none).
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

		// The item's ProtoDecryptionKey as the on-wire 16-byte token: Key1..4 big-endian. Zeros when the item (or its
		// key) is absent - the client then can't decrypt it, so it lists as "No name" but the row is still valid.
		private static byte[] ItemKeyBytes(Item? item)
		{
			var key = new byte[ClaimEntry.KeyLength];
			if (item?.Key is not { } k)
				return key;

			BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(0), k.Key1);
			BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(4), k.Key2);
			BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(8), k.Key3);
			BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(12), k.Key4);
			return key;
		}

		// The sub-0x05 refresh for a multi-charge claim: echo the redeemed row (at its 1-based list index) with its new
		// remaining amount so the client updates that row in place (retail's window "refresh" - it sends no re-list).
		private static async Task SendRedeemAck(INetworkClient client, AccountClaim claim, uint index, uint remaining)
		{
			var guid = DeriveBytes($"claim-batch:{client.Session.Account.Id}", ClaimEntry.BatchGuidLength);
			var code = string.IsNullOrEmpty(claim.Code)
				? DeriveCode(claim.Id)
				: claim.Code;
			var entry = new ClaimEntry(index, claim.ItemTemplateId, claim.Level, code, ItemKeyBytes(claim.Item), guid, claim.ClaimType, remaining, claim.MaxCharges ?? 0);

			await client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(CoreClaimListCodec.BuildRedeemAck(client.Session.Character.Id, entry))));
		}
	}
}
