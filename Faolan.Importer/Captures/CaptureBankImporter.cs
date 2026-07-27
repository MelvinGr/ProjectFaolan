using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Faolan.Core.Network.Constants;

namespace Faolan.Importer.Captures
{
	// Seeds each captured character's personal BANK (CharacterInventory rows in container kind 105) from the on-demand
	// BankIIR packets - the bank is NOT in the login FullChar snapshot (unlike the bags, which CaptureCharacterImporter
	// already imports), it is streamed when the player opens the bank. Record framing (see BankService):
	//   0x2000 carrier -> [u32 subop=0x0BA1C1CB][u32 0xC350][u32 charInstance][u8 01][u32 BE blobLen][ProtoInventoryGame]
	// The blob is the standard container proto (kind 105 + InventoryListEntry items). The LAST BankIIR per character
	// wins (the bank can be re-sent after a move). Only DB characters are touched; the bank container is replaced.
	public static class CaptureBankImporter
	{
		private const int BlobOffset = 17; // subop(4) + 0xC350(4) + charInstance(4) + flag(1) + blobLen(4)
		private const uint BankIirSubOpcode = (uint)GameServer0X2000Opcodes.BankIIR;

		public static async Task<(int Characters, int Items)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var byCharacter = new Dictionary<uint, ProtoInventoryGame>();
			foreach (var packet in rawPackets)
				TryRead(packet, byCharacter);

			if (byCharacter.Count == 0)
				return (0, 0);

			var charactersSeeded = 0;
			var itemsStored = 0;
			foreach (var (characterId, container) in byCharacter)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				var kind = (ContainerKind)container.InventoryId.Type;

				// Replace the character's bank container with the captured contents (the BankIIR is the whole bank).
				var oldRows = await db.CharacterInventory.Where(r => r.CharacterId == characterId && r.Container == kind).ToListAsync();
				db.CharacterInventory.RemoveRange(oldRows);

				var stored = 0;
				foreach (var entry in container.Items)
				{
					var templateId = entry.Item?.ItemData?.Template1 ?? 0;
					if (templateId == 0 || !await db.Items.AnyAsync(i => i.Id == templateId))
						continue;

					db.CharacterInventory.Add(new CharacterInventory
					{
						CharacterId = characterId,
						Container = kind,
						Slot = (int)entry.Pos,
						ItemId = templateId,
						Count = entry.Item!.StatLists.FirstOrDefault(s => s.Stat == 26)?.Value ?? 1
					});
					stored++;
				}

				if (stored > 0 || oldRows.Count > 0)
					charactersSeeded++;
				itemsStored += stored;
			}

			if (itemsStored > 0 || charactersSeeded > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureBankImporter: bank contents for {Characters} characters, {Items} items.", charactersSeeded, itemsStored);
			return (charactersSeeded, itemsStored);
		}

		private static void TryRead(byte[] packetBytes, IDictionary<uint, ProtoInventoryGame> byCharacter)
		{
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Opcode != (ushort)GameAgentOpcodes.PassBlob)
					return;

				var inner = packet.BodyReader().ReadArrayUInt32Length();
				if (inner.Length < BlobOffset || inner.ReadU32Be(0) != BankIirSubOpcode)
					return;

				var charInstance = inner.ReadU32Be(8);
				var blobLen = (int)inner.ReadU32Be(13);
				if (charInstance == 0 || blobLen <= 0 || BlobOffset + blobLen > inner.Length)
					return;

				var container = inner.AsSpan(BlobOffset, blobLen).FromProtobuf<ProtoInventoryGame>();
				byCharacter[charInstance] = container; // last wins
			}
			catch
			{
				// not a parseable BankIIR
			}
		}
	}
}
