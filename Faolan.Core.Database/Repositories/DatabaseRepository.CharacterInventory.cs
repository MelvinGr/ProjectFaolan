using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	// What the character carries: the item instances in its containers, the equipped set, sockets, the guild bank
	// and the purse. One of the five character slices; the row itself is ICharacterRepository.
	public interface ICharacterInventoryRepository
	{
		/// <summary>
		///     The character's worn set (container <see cref="ContainerKind.Equipped" />) as slot -> the item instance in
		///     it, templates loaded. Vanity slots (1000 + the base slot) are included: they are real equipped-container
		///     positions, not a display-only overlay, and a captured L80 snapshot carries pos 1002/1007/1011/1014/1018.
		/// </summary>
		Task<IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance>> GetEquippedItems(uint characterId);

		/// <summary>
		///     The character's item instances, template + sockets loaded. <paramref name="container" /> filters in SQL to
		///     one container (the bag, the bank); null (the default) returns every item the character holds, worn
		///     included, which is what the world-entry snapshot wants.
		/// </summary>
		Task<ItemInstance[]> GetCharacterItems(uint characterId, ContainerKind? container = null);

		/// <summary>
		///     The item in one cell, or null when it is empty. The equip / item-use / trade / tradepost paths all want a
		///     single (container, slot) and would otherwise read the whole inventory to pick it out in memory.
		/// </summary>
		Task<ItemInstance?> GetCharacterItemSlot(uint characterId, ContainerKind container, int slot);
		Task SaveCharacterWeaponSet(uint characterId, WeaponSet activeSet);

		/// <summary>
		///     Mints a new item in a cell, or clears the cell when <paramref name="templateId" /> is null. Whatever was
		///     there is deleted, sockets and all - so this is the right call for a grant, a purchase, a loot pickup or a
		///     split's new half, and the wrong one for a move: use <see cref="MoveCharacterItem" />, which preserves the
		///     instance. Returns the new instance (null when the cell was cleared).
		/// </summary>
		Task<ItemInstance?> SetCharacterItemSlot(uint characterId, ContainerKind container, int slot, uint? templateId, uint count = 1, string? creatorName = null);

		/// <summary>
		///     Moves the item at one cell to another, swapping with the destination's occupant when there is one. The
		///     instances are updated in place, never re-minted, so the crafter, the stack count and the socketed gems
		///     travel with the item by construction - which is the whole point of an item being a row rather than an
		///     address. Returns false when the source cell is empty.
		/// </summary>
		Task<bool> MoveCharacterItem(uint characterId, ContainerKind from, int fromSlot, ContainerKind to, int toSlot);

		/// <summary>
		///     Marks one item instance bound to whoever holds it - the per-instance half of binding, set when a
		///     bind-on-equip item is first worn. Returns false when the instance is gone or was already bound, so the
		///     caller only announces a change that happened.
		///     <para>
		///         The rule ("does this template bind on equip") is not decided here: that is <c>Item.BindsOnEquip</c>,
		///         and the caller applies it. This only persists the outcome.
		///     </para>
		/// </summary>
		Task<bool> BindCharacterItem(uint itemInstanceId);

		/// <summary>
		///     Puts a gem into one socket of an item and consumes the gem's own instance, in one transaction.
		///     Returns the new socket with its gem template loaded, or null when either instance is gone or that
		///     socket index is already taken.
		///     <para>
		///         The rules - is the item socketable, is there a free index, does the colour match - are not decided
		///         here; <c>TradeskillService.Socket</c> applies them. This is the write, and it is one transaction
		///         because the two halves must not come apart: a socket without the gem removed duplicates it, and a
		///         gem removed without the socket destroys it.
		///     </para>
		/// </summary>
		Task<ItemInstanceSocket?> SocketGem(uint itemInstanceId, uint socketIndex, uint gemInstanceId);

		/// <summary>The guild bank vault's contents, template + sockets loaded, ordered by cell.</summary>
		Task<IReadOnlyList<ItemInstance>> GetGuildBankItems(uint guildId);

		/// <summary>
		///     Moves one item between a member's own container and their guild's vault, in either direction - the wire
		///     has one symmetric message for both. Returns false when the source cell is empty, the destination is
		///     occupied, or neither end is the vault.
		///     <para>
		///         The instance is updated, never re-minted, so the crafter, the stack and the gems travel with it -
		///         and ownership moves with it: into the vault clears <c>OwnerCharacterId</c> and sets
		///         <c>OwnerGuildId</c>, out of it does the reverse. Exactly one is ever set.
		///     </para>
		/// </summary>
		Task<bool> MoveGuildBankItem(uint characterId, uint guildId, ContainerKind from, int fromSlot, ContainerKind to, int toSlot);

		/// <summary>Sets an existing stack's size. Returns false when the cell is empty.</summary>
		Task<bool> SetCharacterItemCount(uint characterId, ContainerKind container, int slot, uint count);

		/// <summary>
		///     Hands an item to another character: the same instance, re-owned into their cell, so its crafter, stack and
		///     socketed gems travel with it. Returns the instance, or null when it no longer exists or the target
		///     cell is occupied - in which case nothing has moved and the item is still where it was.
		///     <para>
		///         This is what a player trade is, and it must not be a delete-plus-mint. Re-minting deletes the
		///         giver's row (cascading its sockets) and hands the receiver a gemless copy with no crafter.
		///     </para>
		/// </summary>
		Task<ItemInstance?> TransferCharacterItem(uint instanceId, uint toCharacterId, ContainerKind container, int slot);
		Task SetCharacterMoney(uint characterId, ulong money);
	}

	public partial class DatabaseRepository
	{
		// Where a swap parks the displaced item for the duration of the swap. Negative because every real cell position
		// is a wire slot index and those are non-negative, so it cannot collide with one; the transaction is what
		// guarantees nothing is ever observed sitting here.
		private const int ParkedSlot = -1;

		public async Task<IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance>> GetEquippedItems(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var worn = await ctx.ItemInstances.AsNoTracking()
				.Include(i => i.Template)
				.Include(i => i.Sockets)
				.ThenInclude(x => x.Gem)
				.Where(i => i.OwnerCharacterId == characterId && i.Container == ContainerKind.Equipped)
				.ToArrayAsync();

			// A worn item's Slot is a CharacterEquipmentSlot. An undefined one is dropped rather than cast through: the
			// enum is the set of slots we can render, so an unknown slot on the wire would be a decode gap to report, not
			// something to guess at. (This is the same Enum.IsDefined gate the capture importer applies on the way in -
			// which is how VanityLegs (1016) was found, one item short at "20/21 worn".)
			return worn
				.Where(i => Enum.IsDefined((CharacterEquipmentSlot)i.Slot))
				.ToDictionary(i => (CharacterEquipmentSlot)i.Slot);
		}

		// `container` filters in SQL, like the quest reads' state: most callers want one container (the bag, the bank),
		// not everything the character holds. Null (the default) returns every item including the worn set, which is
		// what the snapshot builder wants - all four of its containers come from these rows.
		public async Task<ItemInstance[]> GetCharacterItems(uint characterId, ContainerKind? container = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ItemsOf(ctx)
				.Where(i => i.OwnerCharacterId == characterId && (container == null || i.Container == container))
				.OrderBy(i => i.Container)
				.ThenBy(i => i.Slot)
				.ToArrayAsync();
		}

		public async Task<ItemInstance?> GetCharacterItemSlot(uint characterId, ContainerKind container, int slot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ItemsOf(ctx).FirstOrDefaultAsync(i => i.OwnerCharacterId == characterId && i.Container == container && i.Slot == slot);
		}

		// Persist the character's drawn weapon set after a SwitchWeaponsButton toggle, so the active loadout (and the
		// login-time gear-stat aggregation that counts only its weapons) survives relog. Targeted update, no load.
		public async Task SaveCharacterWeaponSet(uint characterId, WeaponSet activeSet)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ActiveWeaponSet, activeSet));
		}

		// `creatorName` is the crafter of the item being minted. Defaulting it to null is what makes an ordinary grant
		// safe: an item arriving in a cell a crafted item used to occupy is not itself player-made, and carrying the
		// old value forward would glue the previous crafter to the new item.
		//
		// This deletes whatever occupied the cell, sockets included. That is right for a grant and wrong for a move -
		// a move must keep the instance, so it goes through MoveCharacterItem. Restating the id, the count and the
		// crafter into a new address instead silently drops anything the code does not think to restate, and a gem is
		// exactly that.
		public async Task<ItemInstance?> SetCharacterItemSlot(uint characterId, ContainerKind container, int slot, uint? templateId, uint count = 1, string? creatorName = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var existing = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.OwnerCharacterId == characterId && i.Container == container && i.Slot == slot);
			if (existing != null)
				ctx.ItemInstances.Remove(existing);

			if (templateId == null)
			{
				if (existing != null)
					await ctx.SaveChangesAsync();

				return null;
			}

			var minted = new ItemInstance
			{
				OwnerCharacterId = characterId,
				Container = container,
				Slot = slot,
				TemplateId = templateId.Value,
				Count = count,
				CreatorName = creatorName
			};
			ctx.ItemInstances.Add(minted);
			await ctx.SaveChangesAsync();

			// Load the Template the caller is going to need. Lazy loading is deliberately off, so a freshly minted
			// instance comes back with a null navigation while its sibling TransferCharacterItem Includes it - and
			// both feed the same live-add emitter, so the grant path would throw on exactly the code path the hand-over
			// path exercises safely. Explicit load, per the DB conventions.
			await ctx.Entry(minted).Reference(i => i.Template).LoadAsync();

			return minted;
		}

		public async Task<IReadOnlyList<ItemInstance>> GetGuildBankItems(uint guildId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			return await ctx.ItemInstances.AsNoTracking()
				.Include(i => i.Template)
				.Include(i => i.Sockets).ThenInclude(x => x.Gem)
				.Where(i => i.OwnerGuildId == guildId && i.Container == ContainerKind.GuildBank)
				.OrderBy(i => i.Slot)
				.ToListAsync();
		}

		public async Task<bool> MoveGuildBankItem(uint characterId, uint guildId, ContainerKind from, int fromSlot, ContainerKind to, int toSlot)
		{
			var intoVault = to == ContainerKind.GuildBank;
			if (intoVault == (from == ContainerKind.GuildBank))
				return false; // exactly one end is the vault; a bag-to-bag or vault-to-vault move is not this message

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var item = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.Container == from && i.Slot == fromSlot &&
				(from == ContainerKind.GuildBank ? i.OwnerGuildId == guildId : i.OwnerCharacterId == characterId));
			if (item == null)
				return false;

			// No swap here, unlike MoveCharacterItem. Both captured moves land in a free cell, so what retail does
			// with an occupied one is unobserved - and a wrong guess costs somebody else's deposit. Refuse instead.
			var occupied = await ctx.ItemInstances.AnyAsync(i => i.Container == to && i.Slot == toSlot &&
				(intoVault ? i.OwnerGuildId == guildId : i.OwnerCharacterId == characterId));
			if (occupied)
				return false;

			item.Container = to;
			item.Slot = toSlot;
			item.OwnerCharacterId = intoVault ? null : characterId;
			item.OwnerGuildId = intoVault ? guildId : null;
			await ctx.SaveChangesAsync();

			return true;
		}

		public async Task<ItemInstanceSocket?> SocketGem(uint itemInstanceId, uint socketIndex, uint gemInstanceId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var target = await ctx.ItemInstances.Include(i => i.Sockets).FirstOrDefaultAsync(i => i.Id == itemInstanceId);
			var gem = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.Id == gemInstanceId);
			if (target == null || gem == null || target.Id == gem.Id)
				return null;

			// The unique key is (ItemInstanceId, SocketIndex), so a taken index would throw at SaveChanges with
			// nothing naming it. Refuse it here instead - the caller turns that into silence, which is what retail
			// does for a refused tradeskill.
			if (target.Sockets.Any(x => x.SocketIndex == socketIndex))
				return null;

			var socket = new ItemInstanceSocket
			{
				ItemInstanceId = target.Id,
				SocketIndex = socketIndex,
				GemItemId = gem.TemplateId
			};
			ctx.ItemInstanceSockets.Add(socket);

			// The gem is consumed. A stack loses one; the last one takes the row with it.
			if (gem.Count > 1)
				gem.Count--;
			else
				ctx.ItemInstances.Remove(gem);

			await ctx.SaveChangesAsync();
			await ctx.Entry(socket).Reference(x => x.Gem).LoadAsync();

			return socket;
		}

		public async Task<bool> BindCharacterItem(uint itemInstanceId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var instance = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.Id == itemInstanceId);
			if (instance == null || instance.Bound)
				return false;

			instance.Bound = true;
			await ctx.SaveChangesAsync();

			return true;
		}

		// A move is a column update on the item's own row, which is the entire reason location lives on the instance.
		//
		// A swap cannot be one batch. Two rows trading (container, slot) is a cycle under the unique
		// (owner, container, slot) index - whichever update runs first collides with the row that has not moved yet -
		// and EF says so rather than picking an order ("a circular dependency was detected in the data to be saved").
		// SQLite would reject the intermediate state even if it did pick one: a unique index is enforced per statement,
		// there is nothing to defer to the end of the transaction.
		//
		// So the destination's occupant is parked at a slot no real cell uses, then the source moves in, then the parked
		// row takes the source's old cell - three statements, inside one transaction so a failure between them cannot
		// strand an item at the parking slot.
		public async Task<bool> MoveCharacterItem(uint characterId, ContainerKind from, int fromSlot, ContainerKind to, int toSlot)
		{
			if (from == to && fromSlot == toSlot)
				return true;

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var source = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.OwnerCharacterId == characterId && i.Container == from && i.Slot == fromSlot);
			if (source == null)
				return false;

			var destination = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.OwnerCharacterId == characterId && i.Container == to && i.Slot == toSlot);
			if (destination == null)
			{
				source.Container = to;
				source.Slot = toSlot;
				await ctx.SaveChangesAsync();
				return true;
			}

			await using var transaction = await ctx.Database.BeginTransactionAsync();

			destination.Slot = ParkedSlot;
			await ctx.SaveChangesAsync();

			source.Container = to;
			source.Slot = toSlot;
			await ctx.SaveChangesAsync();

			destination.Container = from;
			destination.Slot = fromSlot;
			await ctx.SaveChangesAsync();

			await transaction.CommitAsync();
			return true;
		}

		public async Task<ItemInstance?> TransferCharacterItem(uint instanceId, uint toCharacterId, ContainerKind container, int slot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var instance = await ctx.ItemInstances.Include(i => i.Template).Include(i => i.Sockets).ThenInclude(x => x.Gem).FirstOrDefaultAsync(i => i.Id == instanceId);
			if (instance == null)
				return null;

			// Refuse rather than overwrite. The caller found a free cell, so an occupied one means the receiver's
			// inventory changed underneath us - and the alternative to refusing is destroying whatever is in it. The
			// unique (owner, container, slot) index would catch it anyway, as an exception at SaveChanges; catching it
			// here means the caller gets "it did not move" and the giver keeps the item.
			if (await ctx.ItemInstances.AnyAsync(i => i.OwnerCharacterId == toCharacterId && i.Container == container && i.Slot == slot))
				return null;

			instance.OwnerCharacterId = toCharacterId;
			instance.Container = container;
			instance.Slot = slot;
			await ctx.SaveChangesAsync();
			return instance;
		}

		public async Task<bool> SetCharacterItemCount(uint characterId, ContainerKind container, int slot, uint count)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ItemInstances
				.Where(i => i.OwnerCharacterId == characterId && i.Container == container && i.Slot == slot)
				.ExecuteUpdateAsync(s => s.SetProperty(i => i.Count, count)) == 1;
		}

		public async Task SetCharacterMoney(uint characterId, ulong money)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Money, money));
		}

		// The one query shape every item read shares: the template (so a caller can name the item) plus the socketed
		// gems and their templates (so a restored socket can name its gem). Split-query because two collection Includes
		// over one root is the cartesian-explosion shape.
		private static IQueryable<ItemInstance> ItemsOf(DatabaseContext ctx)
		{
			return ctx.ItemInstances.AsNoTracking()
				.Include(i => i.Template)
				.Include(i => i.Sockets)
				.ThenInclude(x => x.Gem);
		}
	}
}
