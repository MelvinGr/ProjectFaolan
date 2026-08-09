using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;

namespace Faolan.Importer.Captures
{
	// One captured container entry -> one ItemInstance, sockets included (#750). Shared by the character importer (the
	// login snapshot's four containers) and the bank importer (the BankIIR), because both read the same
	// ProtoInventoryGame shape and both have to lift the same per-instance facts out of it - the stack count, the
	// crafter, and the socketed gems. Two copies of that would drift, and the socket half is exactly the kind of thing
	// only one of them would have grown.
	internal static class CapturedItemInstances
	{
		// `knownTemplates` is the Items catalog: an entry whose own template is absent returns null (the item is not
		// importable), and a SOCKET whose gem template is absent is recorded in `unresolvedGems` and skipped rather
		// than invented. A socket must name a real gem - that is the whole content of a socket on the wire - so a
		// dangling one would be a false claim about what the character is wearing.
		public static ItemInstance? From(ProtoInventoryGame.InventoryListEntry entry, uint characterId, ContainerKind container, IReadOnlySet<uint> knownTemplates,
			ISet<uint> unresolvedGems)
		{
			var templateId = entry.Item?.ItemData?.Template1 ?? 0;
			if (templateId == 0 || !knownTemplates.Contains(templateId))
				return null;

			var instance = new ItemInstance
			{
				OwnerCharacterId = characterId,
				Container = container,
				Slot = (int)entry.Pos,
				TemplateId = templateId,
				Count = entry.Item!.StatLists.FirstOrDefault(s => s.Stat == StackCountStat)?.Value ?? 1,

				// Player-crafted stacks carry their crafter in creator_name (field 5); everything else omits the field.
				// Harvesting it is what makes the imported character's snapshot render retail's "Created by <name>"
				// tooltip line after a relog, exactly as the capture did (#727). ShouldSerializeCreatorName means an
				// ABSENT field reads back as "", so normalize to null.
				CreatorName = string.IsNullOrEmpty(entry.Item!.CreatorName)
					? null
					: entry.Item!.CreatorName
			};

			// The GEMS: ProtoInventoryGameEntry.slot_map, the persistent form of a socket. This is the ONLY place a
			// socketed gem appears - once slotted, a gem never shows up as a top-level inventory entry again - which is
			// why nothing imported them until now and why every gemmed item came back gemless.
			foreach (var (socket, gem) in SnapshotRoot.SocketsOf(entry))
			{
				if (!knownTemplates.Contains(gem.Template1))
				{
					unresolvedGems.Add(gem.Template1);
					continue;
				}

				instance.Sockets.Add(new ItemInstanceSocket
				{
					SocketIndex = socket,
					GemItemId = gem.Template1
				});
			}

			return instance;
		}

		// ProtoInventoryGameEntry.stat_list's StackSize (0x1A) - the entry's stack count.
		private const uint StackCountStat = 26;
	}
}
