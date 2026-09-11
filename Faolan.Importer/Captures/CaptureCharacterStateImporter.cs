using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Seeds captured characters' live state from their session traffic - the pieces that are not part of the
	///     character's identity but were observed on the wire.
	/// </summary>
	/// <remarks>
	///     Wallet: the last DispatchUpdateCash money update ([u64 balance][u32 reason], 12-byte body; absolute balance).
	///     Hotbar: the full saved bar from DispatchShortcutBarReply (s2c) - the authoritative bar retail sends at login.
	///     The latest one in the capture (a character's final saved bar) is used wholesale; the incremental
	///     AddShortcut/RemoveShortcut events are not read - they only carry the player's mid-session re-drags, not the
	///     actual saved bar.
	///     Packets attribute to a character via the RpcHeader GameCharInterface endpoint instance (the character id):
	///     the download's receiver. Only characters already in the DB are touched (CaptureCharacterImporter first).
	/// </remarks>
	public static class CaptureCharacterStateImporter
	{
		private const int ShortcutEntrySize = 24; // [u32 slot][u32 type][u32 target][u32 0][u32 0][u32 0]

		public static CaptureState Extract(IReadOnlyList<CapturedPacket> packets)
		{
			// The two s2c methods this reads, in the wire space CapturedPacket records: DispatchUpdateCash is the
			// wallet, DispatchShortcutBarReply the saved bar.
			var Wallet = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchUpdateCash);
			var ShortcutBar = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchShortcutBarReply);

			var money = new Dictionary<uint, ulong>();
			var shortcuts = new Dictionary<uint, Dictionary<uint, (uint Type, uint Target)>>();

			foreach (var p in packets.OrderBy(p => p.Ticks))
			{
				if (!p.IsDownload || (p.WireOpcode != Wallet && p.WireOpcode != ShortcutBar))
					continue;

				Rpc packet;
				try
				{
					packet = Rpc.Parse(p.Bytes);
				}
				catch
				{
					continue;
				}

				if (packet.Receiver.Type != RpcInterfaces.GameCharInterfaceType)
					continue;

				var character = packet.Receiver.Instance;

				// The wallet (DispatchUpdateCash) is a 12-byte body. Anything else under that method is neither a wallet
				// nor a shortcut bar and must never reach the bar parser below: a method is a per-interface index, so
				// index 0x05 on this conversation also carries auction/trader traffic, whose first word can happen to
				// read as a list count and make the strict bar codec throw.
				if (packet.Method == (uint)GameCharInterfaceMethods.DispatchUpdateCash)
				{
					if (packet.Body.Length == 12)
						money[character] = packet.BodyReader().ReadUInt64(); // last one wins (absolute balance)

					continue;
				}

				// DispatchShortcutBarReply: the whole saved bar - the same body ShortcutBarReplyCodec builds, so it is
				// parsed with that codec. Latest in the capture wins.
				var body = packet.BodyReader().ReadRemainingArray();
				if (body.Length < 4)
					continue;

				var framing = body.ReadU32Be(0);
				if (framing < ListFrame.Unit || framing % ListFrame.Unit != 0)
					continue; // not a shortcut-bar reply (the method index collides across classes; the framing disambiguates)

				// The codec reads all `count` records unconditionally, so only hand it a body that carries them (a
				// truncated reply is skipped rather than seeding a partial bar over a good one).
				var count = ListFrame.CountOf(framing);
				if (4 + count * ShortcutEntrySize > body.Length)
					continue;

				var bar = ShortcutBarReplyCodec.Parse(body.AsSpan(0, 4 + count * ShortcutEntrySize)).Entries.ToDictionary(e => e.Slot, e => (e.Type, e.Id));

				shortcuts[character] = bar; // the full saved bar is authoritative (replaces any earlier one)
			}

			return new CaptureState(money, shortcuts);
		}

		public static async Task<(int MoneySeeded, int ShortcutsSeeded)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures)
		{
			// Replay all captures' state as one stream ordered by real capture time (CapturedPacket.Ticks = epoch
			// microseconds), so a character seen in several captures ends on its latest-in-time wallet and saved bar.
			var state = Extract(captures.SelectMany(c => c).ToList());

			var moneySeeded = 0;
			var shortcutsSeeded = 0;

			foreach (var (characterId, balance) in state.MoneyByCharacter)
			{
				var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == characterId);
				if (character == null)
					continue;

				character.Money = balance;
				moneySeeded++;
			}

			foreach (var (characterId, slots) in state.ShortcutsByCharacter)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				// The captured bar is the whole bar: replace the character's shortcuts with it. Flush the deletes
				// before the inserts so a re-run does not hit the unique (CharacterId, Slot) index (EF does not order
				// delete-before-insert for a non-PK unique key).
				var old = await db.CharacterShortcuts.Where(s => s.CharacterId == characterId).ToListAsync();
				if (old.Count > 0)
				{
					db.CharacterShortcuts.RemoveRange(old);
					await db.SaveChangesAsync();
				}

				foreach (var (slot, (type, target)) in slots)
				{
					db.CharacterShortcuts.Add(new CharacterShortcut
					{
						CharacterId = characterId,
						Slot = slot,
						ShortcutType = type,
						TargetId = target
					});
					shortcutsSeeded++;
				}
			}

			await db.SaveChangesAsync();
			return (moneySeeded, shortcutsSeeded);
		}

		public sealed record CaptureState(Dictionary<uint, ulong> MoneyByCharacter, Dictionary<uint, Dictionary<uint, (uint Type, uint Target)>> ShortcutsByCharacter);
	}
}
