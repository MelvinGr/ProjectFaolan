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
	//  - wallet: the LAST 0x2005 money update of the capture ([u64 balanceCopper][u32 reason], 12-byte body;
	//    the value is the absolute balance - see Send0x2005_MoneyUpdate / protocol notes in docs).
	//  - hotbar: AddShortcut (0x206E, [type][slot][target][0]) / RemoveShortcut (0x2071, [slot]) replayed in
	//    order; UpdateShortcut (0x206F) is skipped - its field semantics are undecoded.
	// Packets are attributed to a character via the RpcHeader GameCharInterface endpoint instance (= the
	// character id): the DOWNLOAD's receiver, the UPLOAD's sender/caller. Only characters already in the DB
	// are touched (CaptureCharacterImporter materializes captured characters on their original ids first).
	public static class CaptureCharacterStateImporter
	{
		private const uint GameCharInterfaceType = 0x8B87F791;

		public static CaptureState Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var money = new Dictionary<uint, ulong>();
			var shortcuts = new Dictionary<uint, Dictionary<uint, (uint, uint)>>();
			var removed = new Dictionary<uint, HashSet<uint>>();

			foreach (var p in packets.OrderBy(p => p.Ticks))
			{
				if (p.Opcode is not (0x2005 or 0x206E or 0x2071))
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

				if (p.IsDownload && packet.Opcode == 0x2005 && packet.Body.Length == 12 && packet.Receiver.Type == GameCharInterfaceType)
				{
					money[packet.Receiver.Instance] = packet.Body.ReadUInt64(); // last one wins (absolute balance)
					continue;
				}

				if (p.IsDownload || packet.Sender.Type != GameCharInterfaceType)
					continue;

				var character = packet.Sender.Instance;
				switch (packet.Opcode)
				{
					case 0x206E when packet.Body.Length == 16: // AddShortcut (the 16-byte body excludes the create-editor enter-world variant)
					{
						var type = packet.Body.ReadUInt32();
						var slot = packet.Body.ReadUInt32();
						var target = packet.Body.ReadUInt32();
						shortcuts.TryAdd(character, new Dictionary<uint, (uint, uint)>());
						shortcuts[character][slot] = (type, target);
						removed.GetValueOrDefault(character)?.Remove(slot);
						break;
					}

					case 0x2071 when packet.Body.Length == 4: // RemoveShortcut
					{
						var slot = packet.Body.ReadUInt32();
						shortcuts.GetValueOrDefault(character)?.Remove(slot);
						removed.TryAdd(character, new HashSet<uint>());
						removed[character].Add(slot);
						break;
					}
				}
			}

			return new CaptureState(money, shortcuts, removed);
		}

		public static async Task<(int MoneySeeded, int ShortcutsSeeded)> ImportAsync(IDatabaseContext db, IEnumerable<IReadOnlyList<CapturedPacket>> captures)
		{
			var moneySeeded = 0;
			var shortcutsSeeded = 0;
			foreach (var capture in captures)
			{
				var state = Extract(capture);

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

					var existing = await db.CharacterShortcuts.Where(s => s.CharacterId == characterId).ToDictionaryAsync(s => s.Slot);
					foreach (var (slot, (type, target)) in slots)
					{
						if (existing.TryGetValue(slot, out var row))
						{
							row.ShortcutType = type;
							row.TargetId = target;
						}
						else
						{
							db.CharacterShortcuts.Add(new CharacterShortcut
							{
								CharacterId = characterId,
								Slot = slot,
								ShortcutType = type,
								TargetId = target
							});
						}

						shortcutsSeeded++;
					}

					if (state.RemovedSlotsByCharacter.TryGetValue(characterId, out var removedSlots))
					{
						foreach (var slot in removedSlots)
						{
							if (existing.TryGetValue(slot, out var row))
								db.CharacterShortcuts.Remove(row);
						}
					}
				}
			}

			await db.SaveChangesAsync();
			return (moneySeeded, shortcutsSeeded);
		}

		public sealed record CaptureState(Dictionary<uint, ulong> MoneyByCharacter, Dictionary<uint, Dictionary<uint, (uint Type, uint Target)>> ShortcutsByCharacter, Dictionary<uint, HashSet<uint>> RemovedSlotsByCharacter);
	}
}
