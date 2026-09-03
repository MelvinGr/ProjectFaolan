using System;
using System.Linq;
using System.Security.Cryptography;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The create-editor avatar's FullChar snapshot is BUILT, not carried (#651). It used to be an 8,078-byte captured
	// record replayed with the character id spliced into it; CreateEditorAvatarSnapshot now assembles the whole thing
	// from typed data, and these are the gates that say the construction reproduces retail exactly.
	//
	// The record's SHA-256 is pinned rather than its bytes: the blob is gone from the repo (#555), so the golden has
	// to be one-way. It was byte-compared against the captured record before deletion, for the first, last and a
	// mid-range character id.
	public class EditorAvatarSnapshotTests
	{
		// A real allocated character id. EntityHandles pins every one of them to 0x02000000..0x0FFFFFFF, which is
		// exactly the range whose protobuf varint is four bytes.
		private const uint Id = 134407636;

		// The captured record, byte-for-byte, for the character id below. Verified against
		// CreateEditorAvatarSnapshot.bin (since deleted) at 0 differing bytes for 0x02000000, this id, and
		// 0x0FFFFFFF - i.e. across the whole allocatable range, so the varint width cannot shift the layout.
		private const string CapturedSha256 = "260f96de9fe974710f1264af56a4b1c4c1795e9117e655b88f75791006c60172";

		// THE gate: the constructed record is the retail one, byte for byte. The digest is of the deleted
		// CreateEditorAvatarSnapshot.bin - retail's own bytes, addressed to the placeholder id the resource was
		// stored with - so this compares construction against a capture, not against itself.
		[Fact]
		public void TheConstructedRecord_IsTheCapturedOne()
		{
			var built = FullPlayerCharacterCodec.Build(CreateEditorAvatar.Snapshot(EntityHandles.CharacterInstanceBase));

			Assert.Equal(8078, built.Length);
			Assert.Equal(CapturedSha256, Convert.ToHexString(SHA256.HashData(built)).ToLowerInvariant());
		}

		[Fact]
		public void TheRecord_RoundTripsThroughTheSnapshotCodec()
		{
			var bytes = FullPlayerCharacterCodec.Build(CreateEditorAvatar.Snapshot(Id));
			var parsed = FullPlayerCharacterCodec.Parse(bytes);

			Assert.Equal(bytes, FullPlayerCharacterCodec.Build(parsed));
			Assert.Equal(Id, parsed.Instance);
		}

		// The root: four EMPTY containers owned by the live character, and the two presence flags retail states
		// explicitly. `has_spellbook = false` is load-bearing - it is what tells the client the sub-class carries no
		// spellbook list, and a protobuf-net default would have omitted it (leaving the root four bytes short).
		[Fact]
		public void TheRoot_CarriesFourEmptyContainersAndBothPresenceFlags()
		{
			var root = CreateEditorAvatar.Snapshot(Id).Root;

			Assert.Equal([101u, 104u, 112u, 113u], root.Inventories.Select(SnapshotRoot.KindOf));
			Assert.All(root.Inventories, c => Assert.Equal(Id, c.InventoryId.Instance));
			Assert.All(root.Inventories, c => Assert.Empty(SnapshotRoot.EntriesOf(c)));
			Assert.True(root.ShouldSerializeHasSpellbook());
			Assert.True(root.ShouldSerializeHasNewClaimItems());
			Assert.False(root.HasSpellbook);
		}

		// The sub-class: empty journal, the game-static access-window catalog, the editor's own stat sheet, and NO
		// spellbook list - which is what distinguishes a create-editor snapshot from a login one on the wire.
		[Fact]
		public void TheSubClass_IsEmptyJournalPlusTheCatalogPlusTheSheet()
		{
			var sub = CreateEditorAvatar.Snapshot(Id).SubClass;

			Assert.Empty(sub.Quests.Records);
			Assert.NotNull(sub.CompletedQuests);
			Assert.Empty(sub.CompletedQuests.Areas);
			Assert.Empty(sub.QuestIndex!.QuestIds);
			Assert.Empty(sub.UnknownAfterAccessWindowTable);
			Assert.Equal(AccessWindowCatalog.Entries.Count, sub.AccessWindowEntries.Count);

			var block = sub.PostAccessBlock!;
			Assert.Null(block.KnownSpells); // ABSENT, not empty - it agrees with root.HasSpellbook
			Assert.Empty(block.WideTail);
			Assert.Empty(block.PairTail);
			Assert.Equal(17, block.Displayed.Pairs.Count);
			Assert.Equal(148, block.Sheet.Pairs.Count);
			Assert.True(block.HasRepeatedStatTable);
		}

		// StatList#2 is the same 74 pairs TWICE - both copies are on the wire and a sheet is only consistent when
		// they agree, so the table stores 148 rather than 74 plus a doubling rule.
		[Fact]
		public void TheSheet_IsSeventyFourPairsWrittenTwice()
		{
			var sheet = CreateEditorAvatarSheet.Sheet;

			Assert.Equal(148, sheet.Count);
			Assert.Equal(sheet.Take(74), sheet.Skip(74));
		}

		// Every stat the editor sheet carries is a stat we have a NAME for - the sheet is self-labelling, so a
		// generated table of raw ids is still readable rather than 165 magic numbers.
		[Fact]
		public void EveryStatId_IsADefinedCharacterStat()
		{
			foreach (var (id, _) in CreateEditorAvatarSheet.Displayed.Concat(CreateEditorAvatarSheet.Sheet))
				Assert.True(Enum.IsDefined(typeof(CharacterStatId), id), $"stat 0x{id:X} is not a defined CharacterStatId");
		}

		// The playfield token is the named editor constant, not a literal lifted out of a capture.
		[Fact]
		public void ThePlayfieldToken_IsTheEditorConstant()
		{
			var displayed = CreateEditorAvatarSheet.Displayed.Single(p => p.Id == (uint)CharacterStatId.PlayfieldToken);
			Assert.Equal(CreateEditorCharBuilder.EditorPlayfieldInstance, displayed.Value);
		}
	}
}
