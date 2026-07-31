using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The pre-IIR scaffold's SUBLIST is a list on both sides of the family (#466 / #475).
	//
	// It was held on NpcSpawnPreIir as the four raw bytes `00 00 03 F1` - "the count==0 framing word" - and the
	// codec REJECTED any spawn whose list was not empty, calling it "variable-width, out of scope". Worse, it found
	// the scaffold by subtracting the empty form's width from the CoreCharacter's offset, so a non-empty list did
	// not merely lose the list: the subtraction landed mid-field and the whole scaffold was undecodable.
	//
	// The player side has modelled this list as typed entries since #466 (the character's saved feat specs). Both
	// families run the same client reader, so it is now one shared type - and since #530 wave 5b one shared scaffold
	// (SpawnPreIir) read by one codec.
	public class SpawnPreIirSublistTests
	{
		[Fact]
		public void EveryCapturedScaffold_DecodesAndReEncodesByteExact()
		{
			var sublistSizes = new Dictionary<int, int>();
			var decoded = 0;
			var undecodable = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn))
				{
					undecodable++;
					continue;
				}

				var scaffold = spawn.PreIir;
				decoded++;
				sublistSizes[scaffold.Sublist.Count] = sublistSizes.GetValueOrDefault(scaffold.Sublist.Count) + 1;

				// The width is DERIVED from the scaffold's own grammar, and the re-encode has to be exactly that wide.
				// That the bytes themselves match the capture is SimpleCharFullUpdateCorpusTests' whole-record gate,
				// which reproduces every one of these spawns - the scaffold no longer has an offset of its own to be
				// right or wrong about, because the body is read sequentially.
				Assert.Equal(scaffold.ByteCount, Rebuild(scaffold).Length);
			}

			// Every captured scaffold, player and NPC alike - the codec is family-neutral because the client reader
			// is. (What the player model used to call "the frame" was this scaffold plus the embedded CoreCharacter's
			// 5-byte head, which is the message's, not the scaffold's.)
			Assert.Equal(0, undecodable);
			Assert.Equal(11632, decoded);

			// ⚠️ THE SIX. All are one NPC - Viriden - and they carry the same key-0/flag-0/unnamed/no-ids entry a
			// fresh player carries. Before this they were the only captured spawns the NPC codec could not read at
			// all. The 232 four-entry-or-fewer player frames are the feat specs (#474).
			Assert.Equal(11394, sublistSizes[0]);
			Assert.Equal(226 + 6, sublistSizes[1]);
			Assert.Equal(5, sublistSizes[2]);
			Assert.Equal(1, sublistSizes[4]);
		}

		// The scaffold composes into a spawn record rather than standing alone (#530 rule 5c).
		private static byte[] Rebuild(SpawnPreIir frame)
		{
			using var s = new FaolanStream();
			SpawnPreIirCodec.Write(s, frame);
			return s.ToArray();
		}

		// The from-scratch fresh scaffold emits an EMPTY sublist, which frames as ForCount(0) - the same four bytes
		// that used to be hardcoded. Pinned so the typed list cannot start emitting a different fresh form.
		[Fact]
		public void TheFreshScaffold_EmitsAnEmptySublist()
		{
			var fresh = SpawnPreIirs.Minimal();

			Assert.Empty(fresh.Sublist);
			Assert.Equal(SpawnPreIir.FixedLength, fresh.ByteCount);
			Assert.Equal(SpawnPreIir.FixedLength, Rebuild(fresh).Length);
			// And an entry costs exactly its own width - [u32 key][u8 flag][u16 len][count word] + name + ids - which
			// is what makes the fixed-width subtraction this codec used to do wrong rather than merely lossy.
			var withEntry = SpawnPreIirs.Minimal();
			withEntry.Sublist.Add(new SpawnPreFrameSublistEntry());
			Assert.Equal(SpawnPreIir.FixedLength + 11, Rebuild(withEntry).Length);
			Assert.Equal(Rebuild(withEntry).Length, withEntry.ByteCount);
		}
	}
}
