using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     One captured container entry -> one ItemInstance, sockets included.
	/// </summary>
	/// <remarks>
	///     Shared by the character importer (the login snapshot's four containers) and the bank importer (the BankIIR):
	///     both read the same ProtoInventoryGame shape and lift the same per-instance facts out of it - the stack count,
	///     the crafter, and the socketed gems.
	/// </remarks>
	internal static class CapturedItemInstances
	{
		/// <param name="knownTemplates">
		///     The Items catalog. An entry whose own template is absent returns null (the item is not importable).
		/// </param>
		/// <param name="unresolvedGems">
		///     Receives the template of any socket whose gem is absent from the catalog; such a socket is skipped rather
		///     than invented. A socket must name a real gem - that is the whole content of a socket on the wire - so a
		///     dangling one would be a false claim about what the character is wearing.
		/// </param>
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
				// It is what renders retail's "Created by <name>" tooltip line after a relog. ShouldSerializeCreatorName
				// means an absent field reads back as "", so normalize to null.
				CreatorName = string.IsNullOrEmpty(entry.Item!.CreatorName)
					? null
					: entry.Item!.CreatorName
			};

			// The gems: ProtoInventoryGameEntry.slot_map, the persistent form of a socket. This is the only place a
			// socketed gem appears - once slotted, a gem never shows up as a top-level inventory entry again.
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
