using System.Linq;
using Faolan.Core;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.GameServer.CreateFlow;
using Program = Faolan.Program;
using Xunit;

namespace Faolan.Tests
{
	// The create-editor avatar's FullChar snapshot is a MODELLED record, not an opaque blob (#651). It used to be
	// emitted as the record's tail spliced onto a hand-written protobuf prefix at the send site; it is now built by
	// FullPlayerCharacterCodec from the typed model. These gates say the swap changed no bytes and that the record
	// is fully accounted for - which is what makes generating it from the character the next step rather than a leap.
	public class EditorAvatarSnapshotTests
	{
		// A real allocated character id: EntityHandles constrains every one of them to exactly four protobuf varint
		// bytes, which is what the retired hand-written prefix silently depended on.
		private const uint Id = 134407636;

		// The exact bytes the hand-spliced send site produced, rebuilt here. If the codec path ever diverges from
		// what retail was replayed as, this is what says so.
		private static byte[] HandSpliced(uint id)
		{
			using var s = new FaolanStream();
			s.WriteArray(0x73, 0x7A, 0x6D, 0xF9, 0x00, 0x00, 0xC3, 0x50);
			s.WriteUInt32(id);
			s.WriteArray(0x00, 0x00, 0x00, 0x00, 0x44, 0x28, 0x81, 0x06, 0x32, 0x0E, 0x0A, 0x07, 0x08, 0x65, 0x10);
			s.WriteVarint(id);
			s.WriteArray(0x10, 0x84, 0x08, 0x18, 0x00, 0x32, 0x0D, 0x0A, 0x07, 0x08, 0x68, 0x10);
			s.WriteVarint(id);
			s.WriteArray(0x10, 0x18, 0x18, 0x00, 0x32, 0x0D, 0x0A, 0x07, 0x08, 0x70, 0x10);
			s.WriteVarint(id);
			s.WriteArray(0x10, 0x30, 0x18, 0x0C, 0x32, 0x0D, 0x0A, 0x07, 0x08, 0x71, 0x10);
			s.WriteVarint(id);
			// The stored record's TAIL, read straight off the embedded resource rather than off the built output -
			// otherwise this would assert the codec against its own bytes. The 77-byte head above is the part that
			// used to be hand-written, and it is spelled out here independently of the codec that now emits it.
			s.WriteArray(Program.LoadEmbeddedResource("Faolan.CreateEditorAvatarSnapshot.bin")[^8001..]);
			return s.ToArray();
		}

		[Fact]
		public void TheCodecPath_ReproducesTheHandSplicedBytes()
		{
			Assert.Equal(HandSpliced(Id), CreateFlowService.EditorAvatarSnapshotFor(Id));
		}

		// Re-stamping is complete: the record carries the live character nowhere the placeholder survives.
		[Fact]
		public void EveryInstanceSlot_CarriesTheLiveCharacter()
		{
			var parsed = FullPlayerCharacterCodec.Parse(CreateFlowService.EditorAvatarSnapshotFor(Id));

			Assert.Equal(Id, parsed.Instance);
			Assert.Equal(4, parsed.Root.Inventories.Count);
			Assert.All(parsed.Root.Inventories, c => Assert.Equal(Id, c.InventoryId.Instance));
			Assert.Equal([101u, 104u, 112u, 113u], parsed.Root.Inventories.Select(SnapshotRoot.KindOf));
		}

		// Nothing in it is opaque any more: the whole record round-trips through the same codec the world-entry
		// snapshot is BUILT with, so what is left to source is the values, not the grammar.
		[Fact]
		public void TheWholeRecord_RoundTripsThroughTheSnapshotCodec()
		{
			var bytes = CreateFlowService.EditorAvatarSnapshotFor(Id);
			var parsed = FullPlayerCharacterCodec.Parse(bytes);

			Assert.Equal(bytes, FullPlayerCharacterCodec.Build(parsed));
			Assert.Equal(86, parsed.SubClass.AccessWindowEntries.Count);
			Assert.NotNull(parsed.SubClass.PostAccessBlock);
			Assert.Empty(parsed.SubClass.UnknownBeforeAccessWindowTable);
		}
	}
}
