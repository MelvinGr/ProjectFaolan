using System.Collections.Generic;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Constants
{
	// The create-editor avatar's own FullChar (0x737A6DF9) snapshot, assembled WHOLE from typed data (#651). This
	// replaced an 8 KB captured record that was replayed with the character id spliced into it.
	//
	// It was never opaque - `FullPlayerCharacterCodec` parses the captured record and re-encodes it byte-exact - and
	// once every region is typed there is very little data left to carry:
	//
	//   root        subscription_flags = 769, and four EMPTY containers (equipment/backpack/vanity/quest)
	//   quests      all three regions empty: an unnamed avatar has no journal
	//   access win  the game-static AccessWindowCatalog (the same 86 entries every character gets)
	//   stat block  CreateEditorAvatarSheet - the only part that is still captured values
	//   spellbook   ABSENT, and both trailing tails empty
	//
	// ⚠️ **The absent spellbook is a real distinction, not an empty list.** A login snapshot always carries the
	// list (its 0x3F1 frame word), and a create-editor snapshot ends at StatList#2 with nothing after it - the two
	// differ on the wire. `SnapshotTails.FromScratch` always emits the list, which is right for world entry and
	// wrong here, and is why this does not simply call it.
	public static class CreateEditorAvatar
	{
		// The editor avatar's four containers, with the sizes the captured record declares. They are all EMPTY -
		// the avatar's gear is streamed as its own spawn record, not through the snapshot's inventory.
		private static readonly (uint Kind, uint MaxSize, uint DisabledSlots)[] Containers =
		[
			(0x65, 1028, 0), // equipped
			(0x68, 24, 0), // backpack
			(0x70, 48, 12), // vanity
			(0x71, 48, 0) // quest
		];

		// The root's subscription/expansion word. Constant across the create-editor captures.
		private const uint SubscriptionFlags = 769;

		// The avatar's snapshot as a VALUE. It deliberately does not serialize itself (#530 rule 1) - this is a
		// value factory beside the codec, like MoveTaskNodes.Path, and FullPlayerCharacterCodec.Build is how it
		// becomes bytes.
		public static FullPlayerCharacterIIR Snapshot(uint instance)
		{
			// ⚠️ `has_new_claim_items` and `has_spellbook` are set EXPLICITLY to false. Both are optional protobuf
			// scalars, so protobuf-net omits a default-valued one and the root comes out four bytes short of
			// retail's - the presence trap. Retail states them, and `has_spellbook = false` is not incidental: it is
			// what tells the client the sub-class carries NO spellbook list, which is exactly how a create-editor
			// snapshot differs from a login one. The flag and the absent list have to agree.
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
