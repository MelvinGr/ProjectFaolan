using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Conan;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Seeds captured characters' live STATE from their session traffic - the pieces that aren't part of the
	// character's identity but were observed on the wire:
	//  - wallet: the LAST 0x2005 money update ([u64 balance][u32 reason], 12-byte body; absolute balance).
	//  - hotbar: the FULL saved bar from DispatchShortcutBarReply (0x2076, s2c) - the authoritative bar retail
	//    sends at login. We take the LATEST one in the capture (a character's final saved bar) and use it wholesale;
	//    the incremental AddShortcut/RemoveShortcut (0x206E/0x2071) events are NOT read - they only carry the
	//    player's mid-session re-drags, not the actual saved bar, so an imported char must copy the 0x2076 snapshot.
	// Packets attribute to a character via the RpcHeader GameCharInterface endpoint instance (= the character id):
	// the DOWNLOAD's receiver. Only characters already in the DB are touched (CaptureCharacterImporter first).
	public static class CaptureCharacterStateImporter
	{
		private const uint GameCharInterfaceType = 0x8B87F791;
		private const uint ListFrame = 0x3F1; // (count + 1) * 0x3F1 list framing (matches ShortcutBarBuilder)
		private const int ShortcutEntrySize = 24; // [u32 slot][u32 type][u32 target][u32 0][u32 0][u32 0]

		public static CaptureState Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var money = new Dictionary<uint, ulong>();
			var shortcuts = new Dictionary<uint, Dictionary<uint, (uint Type, uint Target)>>();

			foreach (var p in packets.OrderBy(p => p.Ticks))
			{
				if (!p.IsDownload || p.Opcode is not (0x2005 or 0x2076))
					continue;

				ConanPacket packet;
				try
				{
					packet = new ConanPacket(p.Bytes);
				}
				catch
				{
					continue;
				}

				if (packet.Receiver.Type != GameCharInterfaceType)
					continue;

				var character = packet.Receiver.Instance;

				if (packet.Opcode == 0x2005 && packet.Body.Length == 12)
				{
					money[character] = packet.Body.ReadUInt64(); // last one wins (absolute balance)
					continue;
				}

				// DispatchShortcutBarReply (0x2076): the whole saved bar. Body = [u32 (count+1)*0x3F1][entry * count],
				// entry = [u32 slot][u32 type][u32 target][u32 0][u32 0][u32 0]. Latest in the capture wins.
				var body = packet.Body.ReadRemainingArray();
				if (body.Length < 4)
					continue;

				var framing = BinaryPrimitives.ReadUInt32BigEndian(body);
				if (framing < ListFrame || framing % ListFrame != 0)
					continue; // not a shortcut-bar reply (0x2076 collides across classes; the framing disambiguates)

				var count = (int)(framing / ListFrame) - 1;
				var bar = new Dictionary<uint, (uint, uint)>();
				for (var i = 0; i < count && 4 + (i + 1) * ShortcutEntrySize <= body.Length; i++)
				{
					var off = 4 + i * ShortcutEntrySize;
					var slot = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off, 4));
					var type = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off + 4, 4));
					var target = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(off + 8, 4));
					bar[slot] = (type, target);
				}

				shortcuts[character] = bar; // the full saved bar is authoritative (replaces any earlier one)
			}

			return new CaptureState(money, shortcuts);
		}

		public static async Task<(int MoneySeeded, int ShortcutsSeeded)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures)
		{
			// Replay ALL captures' state as one stream ordered by real capture time (CapturedPacket.Ticks = epoch
			// microseconds), so a character seen in several captures ends on its LATEST-in-time wallet + saved bar.
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
				// before the inserts so a re-run doesn't hit the UNIQUE (CharacterId, Slot) index (EF doesn't order
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
