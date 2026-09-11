using System.Collections.Generic;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     The create-editor avatar's own FullChar snapshot, assembled whole from typed data.
	/// </summary>
	/// <remarks>
	///     The regions it carries:
	///     <list type="bullet">
	///         <item>root: subscription flags, and four empty containers (equipment/backpack/vanity/quest).</item>
	///         <item>quests: all three regions empty - an unnamed avatar has no journal.</item>
	///         <item>access windows: the game-static <see cref="AccessWindowCatalog" />, the same for every character.</item>
	///         <item>stat block: <see cref="CreateEditorAvatarSheet" />, the only part still holding captured values.</item>
	///         <item>spellbook: absent, and both trailing tails empty.</item>
	///     </list>
	///     <para>
	///         The absent spellbook is a real distinction, not an empty list: a login snapshot always carries the list
	///         frame, while a create-editor snapshot ends at the second StatList with nothing after it. The two differ
	///         on the wire, so this cannot route through <c>SnapshotTails.FromScratch</c>, which always emits the list.
	///     </para>
	/// </remarks>
	public static class CreateEditorAvatar
	{
		// The editor avatar's four containers, with the sizes the captured record declares. All are empty: the
		// avatar's gear is streamed as its own spawn record, not through the snapshot's inventory.
		private static readonly (uint Kind, uint MaxSize, uint DisabledSlots)[] Containers =
		[
			(0x65, 1028, 0), // equipped
			(0x68, 24, 0), // backpack
			(0x70, 48, 12), // vanity
			(0x71, 48, 0) // quest
		];

		// The root's subscription/expansion word. Constant across the create-editor captures.
		private const uint SubscriptionFlags = 769;

		/// <summary>
		///     The avatar's snapshot as a value. It deliberately does not serialize itself: this is a value factory
		///     beside the codec, and <c>FullPlayerCharacterCodec.Build</c> is how it becomes bytes.
		/// </summary>
		public static FullPlayerCharacterIIR Snapshot(uint instance)
		{
			// `has_new_claim_items` and `has_spellbook` are set explicitly to false. Both are optional protobuf
			// scalars, so protobuf-net would omit a default-valued one and the root would come out four bytes short
			// of retail's. `has_spellbook = false` also tells the client the sub-class carries no spellbook list:
			// the flag and the absent list have to agree.
			var root = new ProtoFullCharacterIIR
			{
				SubscriptionFlags = SubscriptionFlags,
				HasNewClaimItems = false,
				HasSpellbook = false
			};
			foreach (var (kind, maxSize, disabled) in Containers)
			{
				root.Inventories.Add(new ProtoInventoryGame
				{
					InventoryId = new ProtoID32 { Type = kind, Instance = instance },
					MaxSize = maxSize,
					DisabledSlotsCount = disabled
				});
			}

			var block = new SubClassPostAccessBlock
			{
				Displayed = StatsOf(CreateEditorAvatarSheet.Displayed),
				Sheet = StatsOf(CreateEditorAvatarSheet.Sheet),
				KnownSpells = null // absent, not empty - see the type header
			};

			var subClass = new FullCharSnapshotSubClass
			{
				Quests = new QuestcRegion(),
				CompletedQuests = new CompletedQuestJournalRegion(),
				QuestIndex = new QuestIndexRegion(),
				AccessWindowEntries = [.. AccessWindowCatalog.Entries],
				PostAccessBlock = block
			};

			return new FullPlayerCharacterIIR(instance, root, subClass);
		}

		private static StatList StatsOf(IReadOnlyList<(uint Id, uint Value)> pairs)
		{
			var list = new StatList();
			foreach (var (id, value) in pairs)
				list.Pairs.Add((id, value));

			return list;
		}
	}
}
