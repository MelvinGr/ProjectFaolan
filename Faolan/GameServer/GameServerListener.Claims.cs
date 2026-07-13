using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	// Item claims / "Redeem" window (CoreClaimIIR 0x06EC1255). The client requests its claims (sub 0x0007), the
	// server answers with the list (sub 0x0001, CoreClaimListCodec); redeeming (sub 0x0003 + 1-based index) grants
	// the item to the backpack and drops the claim. The "new claims available" glow (sub 0x0009, CoreClaimCodec) is
	// pushed at world entry only when the account has unseen claims. Claims are account-scoped (AccountClaim), seeded
	// by the .claim GM command for now. See docs/subsystems/item-claims.md.
	public partial class GameServerListener
	{
		// Dispatch an inbound CoreClaimIIR. Body (after the 0x2000 sub-header) = [receiverClass][receiverInstance][u16 sub][args].
		private async Task HandleCoreClaim(INetworkClient client, ConanPacket packet)
		{
			_ = packet.Body.ReadUInt32(); // receiver class (0xC350 Character)
			_ = packet.Body.ReadUInt32(); // receiver instance (the player's handle)
			var sub = packet.Body.ReadUInt16();

			switch (sub)
			{
				case CoreClaimListCodec.SubRequest: // 0x0007 - "send me my claims"
					await SendClaimList(client);
					break;

				case CoreClaimListCodec.SubRedeem: // 0x0003 - redeem the 1-based claim index
					var index = packet.Body.ReadUInt32();
					await RedeemClaim(client, index);
					break;

				default:
					Logger.LogDebug("Unhandled CoreClaimIIR sub 0x{Sub:X4} ({RawLength} bytes).", sub, packet.Body.Length);
					break;
			}
		}

		// Build + send the account's claim list (sub 0x0001) and clear the "new" glow flag - opening the window counts
		// as seeing the claims, so subsequent logins don't re-glow (matches retail).
		private async Task SendClaimList(INetworkClient client)
		{
			var accountId = client.Session.Account.Id;
			var handle = client.Session.Account.ClientInstance;
			var rows = await Database.GetAccountClaims(accountId);
			var entries = BuildClaimEntries(accountId, rows);

			// Remember the exact order we sent, so a subsequent redeem resolves its 1-based index against THIS list even
			// as "Claim All" deletes rows one by one (which would otherwise shift every later index).
			client.Session.ClaimSlots = rows.Select(r => r.Id).ToList();

			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(CoreClaimListCodec.Build(handle, entries))
				.SendAsync(client);

			// Opening the window counts as seeing the claims - clear the account's glow flag (DB + the live session copy
			// so a re-entry this session doesn't re-glow).
			await Database.SetAccountClaimsAvailable(accountId, false);
			client.Session.Account.ClaimsAvailable = false;

			Logger.LogInformation("Sent {Count} claim(s) to account {AccountId}.", entries.Count, accountId);
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
				Logger.LogWarning("Redeem for out-of-range claim index {Index} (snapshot has {Count}).", index, slots?.Count ?? 0);
				return;
			}

			var claim = await Database.GetAccountClaim(slots[(int)(index - 1)]);
			if (claim == null || claim.AccountId != client.Session.Account.Id)
				return; // already redeemed (or not this account's) - no-op

			// Each redeem delivers exactly ONE of the item (proven by the capture: the delivered DispatchAddItem carries
			// no stat-26 stack count). The Type is the BINDING (6 account / 4 character), not item-vs-currency. Claim
			// items carry a key but no hash, so allow the hashless live add (the DispatchAddItem is also the client's
			// "claim collected" signal that clears a single-use row).
			var slot = await GrantItemToBackpack(client, claim.ItemTemplateId, 1, allowHashless: true);
			if (slot == null)
			{
				// Bag full (or the item template is missing) - keep the claim so the player can retry.
				if (characterId != 0)
					await _agentServerDispatcher.SendSystemMessage(characterId, "Your bag is full - free a slot and redeem the claim again.");
				return;
			}

			// Multi-charge claim (MaxCharges > 0, e.g. "Double XP" x20): decrement the remaining charges and, while any
			// remain, refresh the client's row in place with the sub-0x05 ack ([remaining][max]) instead of removing it.
			// The row is dropped only when the last charge is spent. A single-use claim (MaxCharges 0) just drops now -
			// its DispatchAddItem already told the client to remove it.
			if (claim.MaxCharges > 0 && claim.Quantity > 1)
			{
				var remaining = claim.Quantity - 1;
				await Database.SetAccountClaimQuantity(claim.Id, remaining);
				await SendRedeemAck(client, claim, index, remaining);
				Logger.LogInformation("Account {AccountId} redeemed 1 charge of claim {ClaimId} (template {Template}); {Remaining} left.",
					client.Session.Account.Id, claim.Id, claim.ItemTemplateId, remaining);
				return;
			}

			await Database.DeleteAccountClaim(claim.Id);
			Logger.LogInformation("Account {AccountId} redeemed claim {ClaimId} (template {Template}).", client.Session.Account.Id, claim.Id, claim.ItemTemplateId);
		}

		// The sub-0x05 refresh for a multi-charge claim: echo the redeemed row (at its 1-based list index) with its new
		// remaining amount so the client updates that row in place (retail's window "refresh" - it sends no re-list).
		private async Task SendRedeemAck(INetworkClient client, AccountClaim claim, uint index, uint remaining)
		{
			var guid = DeriveBytes($"claim-batch:{client.Session.Account.Id}", ClaimEntry.BatchGuidLength);
			var code = string.IsNullOrEmpty(claim.Code) ? DeriveCode(claim.Id) : claim.Code;
			var entry = new ClaimEntry(index, claim.ItemTemplateId, claim.Level, code, ItemKeyBytes(claim.Item), guid, claim.ClaimType, remaining, claim.MaxCharges);

			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(CoreClaimListCodec.BuildRedeemAck(client.Session.Account.ClientInstance, entry))
				.SendAsync(client);
		}

		// Push the "new claims available" glow (sub 0x0009) at world entry, but only when the account has UNSEEN claims
		// (Account.ClaimsAvailable) - retail sends this on a fresh login with new claims and omits it once viewed. The
		// session account is loaded at login, so the flag is current here.
		private async Task SendNewClaimsGlowIfAny(INetworkClient client)
		{
			if (!client.Session.Account.ClaimsAvailable)
				return;

			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(CoreClaimCodec.Build(client.Session.Account.ClientInstance))
				.SendAsync(client);
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
				var code = string.IsNullOrEmpty(row.Code) ? DeriveCode(row.Id) : row.Code;
				entries.Add(new ClaimEntry((uint)(i + 1), row.ItemTemplateId, row.Level, code, key, guid, row.ClaimType, row.Quantity, row.MaxCharges));
			}

			return entries;
		}

		// The item's ProtoDecryptionKey as the on-wire 16-byte token: Key1..4 big-endian. Zeros when the item (or its
		// key) is absent - the client then can't decrypt it, so it lists as "No name" but the row is still valid.
		private static byte[] ItemKeyBytes(Item? item)
		{
			var key = new byte[ClaimEntry.KeyLength];
			if (item?.Key is not { } k)
				return key;

			System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(0), k.Key1);
			System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(4), k.Key2);
			System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(8), k.Key3);
			System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(12), k.Key4);
			return key;
		}

		private static byte[] DeriveBytes(string seed, int length)
		{
			var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
			var result = new byte[length];
			Array.Copy(hash, result, length);
			return result;
		}

		// A stable 4-char A-Z0-9 code from the claim id (cosmetic, when a row carries none).
		private static string DeriveCode(uint claimId)
		{
			const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
			var hash = BitConverter.ToUInt32(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"claim-code:{claimId}")), 0);
			var chars = new char[4];
			for (var i = 0; i < 4; i++)
			{
				chars[i] = alphabet[(int)(hash % (uint)alphabet.Length)];
				hash /= (uint)alphabet.Length;
			}

			return new string(chars);
		}

		// .claim add <itemId> [count]  - seed a redeemable item claim onto the calling account's Claims window (the
		// test bootstrap until shop/reward grants populate it). .claim list - show the pending claims. Redeem in-game
		// via the client's Claims/Redeem window.
		private async Task ClaimCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var accountId = client.Session.Account.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

			if (parts.Length >= 1 && parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
			{
				var rows = await Database.GetAccountClaims(accountId);
				if (rows.Count == 0)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "No pending claims.");
					return;
				}

				for (var i = 0; i < rows.Count; i++)
				{
					var item = await Database.GetItem(rows[i].ItemTemplateId);
					var qty = rows[i].Quantity == 0 ? 1 : rows[i].Quantity;
					await _agentServerDispatcher.SendSystemMessage(characterId, $"#{i + 1}: {qty}x '{item?.Name ?? "?"}' (template {rows[i].ItemTemplateId})");
				}

				return;
			}

			if (parts.Length is < 2 or > 3 || !parts[0].Equals("add", StringComparison.OrdinalIgnoreCase)
			    || !uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .claim add &lt;itemId&gt; [count]  |  .claim list");
				return;
			}

			uint count = 1;
			if (parts.Length == 3 && !uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .claim add &lt;itemId&gt; [count]");
				return;
			}

			var template = await Database.GetItem(itemId);
			// Level is cosmetic (the client shows the item's own level from its RDB); seed it from our Items row if we
			// have it. Quantity 0 = a single item (matches the wire for every item claim in the capture).
			await Database.CreateAccountClaim(new AccountClaim
			{
				AccountId = accountId,
				ItemTemplateId = itemId,
				Level = template?.Level ?? 0,
				// count <= 1 = a single-use item claim (0 charges, shown as ∞); count > 1 = a multi-charge claim.
				Quantity = count <= 1 ? 0u : count,
				MaxCharges = count <= 1 ? 0u : count
			});
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Added claim: {(count == 0 ? 1 : count)}x '{template?.Name ?? "?"}' (template {itemId}). Open the Claims/Redeem window to collect it.");
		}
	}
}
