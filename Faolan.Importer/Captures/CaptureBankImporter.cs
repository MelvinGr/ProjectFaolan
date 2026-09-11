using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Seeds each captured character's personal bank (container kind 105) from the on-demand BankIIR packets.
	/// </summary>
	/// <remarks>
	///     The bank is not in the login FullChar snapshot (unlike the bags, which <see cref="CaptureCharacterImporter" />
	///     imports); it is streamed when the player opens the bank. Record framing (see BankService):
	///     PassBlob carrier -> [u32 subop][u32 0xC350][u32 charInstance][u8 01][u32 be blobLen][ProtoInventoryGame].
	///     The blob is the standard container proto (kind 105 + InventoryListEntry items). The last BankIIR per character
	///     wins (the bank can be re-sent after a move). Only DB characters are touched; the bank container is replaced.
	/// </remarks>
	public static class CaptureBankImporter
	{
		private const int BlobOffset = 17; // subop(4) + 0xC350(4) + charInstance(4) + flag(1) + blobLen(4)
		private const uint BankIirSubOpcode = BankCodec.Opcode;

		public static async Task<(int Characters, int Items)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var byCharacter = new Dictionary<uint, ProtoInventoryGame>();
			foreach (var packet in rawPackets)
				TryRead(packet, byCharacter);

			if (byCharacter.Count == 0)
				return (0, 0);

			var charactersSeeded = 0;
			var itemsStored = 0;
			var knownTemplates = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();
			var unresolvedGems = new HashSet<uint>();
			foreach (var (characterId, container) in byCharacter)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				var kind = (ContainerKind)container.InventoryId.Type;

				// Replace the character's bank container with the captured contents (the BankIIR is the whole bank).
				var old = await db.ItemInstances.Where(i => i.OwnerCharacterId == characterId && i.Container == kind).ToListAsync();
				db.ItemInstances.RemoveRange(old);

				var stored = 0;
				foreach (var entry in container.Items)
				{
					// The banked item keeps its crafter and its socketed gems, same as a carried one - the BankIIR is the
					// same ProtoInventoryGame shape.
					if (CapturedItemInstances.From(entry, characterId, kind, knownTemplates, unresolvedGems) is not { } instance)
						continue;

					db.ItemInstances.Add(instance);
					stored++;
				}

				if (stored > 0 || old.Count > 0)
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
				if (packet.Method != (uint)GameAgentMethods.PassBlob)
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
