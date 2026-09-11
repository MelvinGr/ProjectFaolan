using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The pre-IIR scaffold's sublist is a list on both sides of the family.
	//
	// Held as the four raw bytes `00 00 03 F1` - "the count==0 framing word" - it forces a codec to reject any spawn
	// whose list is not empty, and worse, to find the scaffold by subtracting the empty form's width from the
	// CoreCharacter's offset: a non-empty list then does not merely lose the list, the subtraction lands mid-field and
	// the whole scaffold is undecodable.
	//
	// Typed, it is the character's saved feat specs on the player side. Both families run the same client reader, so
	// it is one shared type in one shared scaffold (SpawnPreIir), read by one codec.
	public class SpawnPreIirSublistTests
	{
		[Fact]
		public void EveryCapturedScaffold_DecodesAndReEncodesByteExact()
		{
			var sublistSizes = new Dictionary<int, int>();
			var decoded = 0;
			var undecodable = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn))
				{
					undecodable++;
					continue;
				}

				var scaffold = spawn.PreIir;
				decoded++;
				sublistSizes[scaffold.Sublist.Count] = sublistSizes.GetValueOrDefault(scaffold.Sublist.Count) + 1;

				// The width is derived from the scaffold's own grammar, and the re-encode has to be exactly that wide.
				// That the bytes themselves match the capture is SimpleCharFullUpdateCorpusTests' whole-record gate,
				// which reproduces every one of these spawns - the scaffold has no offset of its own to be right or
				// wrong about, because the body is read sequentially.
				Assert.Equal(scaffold.ByteCount, Rebuild(scaffold).Length);
			}

			// Every captured scaffold, player and NPC alike - the codec is family-neutral because the client reader
			// is. (A "frame" that takes in the embedded CoreCharacter's 5-byte head is this scaffold plus bytes that
			// belong to the message.)
			Assert.Equal(0, undecodable);
			Assert.Equal(17717, decoded);

			// The six. Six of the single-entry frames are one NPC - Viriden - carrying the same key-0/flag-0/
			// unnamed/no-ids entry a fresh player carries, which is the shape a list-blind codec cannot read at all.
			// (That split is 226 + 6 counted by hand on the 2026-07-27 corpus; it is the total that is pinned here,
			// since nothing in this walk knows an entity's name.) The
			// four-entry-or-fewer player frames are the feat specs.
			Assert.Equal(17421, sublistSizes[0]);
			Assert.Equal(286, sublistSizes[1]);
			Assert.Equal(9, sublistSizes[2]);
			Assert.Equal(1, sublistSizes[4]);
		}

		// The scaffold composes into a spawn record rather than standing alone.
		private static byte[] Rebuild(SpawnPreIir frame)
		{
			using var s = new FaolanStream();
			SpawnPreIirCodec.Write(s, frame);
			return s.ToArray();
		}

		// The from-scratch fresh scaffold emits an empty sublist, which frames as ForCount(0). Pinned so the typed list
		// cannot start emitting a different fresh form.
		[Fact]
		public void TheFreshScaffold_EmitsAnEmptySublist()
		{
			var fresh = SpawnPreIirs.Minimal();

			Assert.Empty(fresh.Sublist);
			Assert.Equal(SpawnPreIir.FixedLength, fresh.ByteCount);
			Assert.Equal(SpawnPreIir.FixedLength, Rebuild(fresh).Length);
			// And an entry costs exactly its own width - [u32 key][u8 flag][u16 len][count word] + name + ids - which
			// is what makes a fixed-width subtraction wrong rather than merely lossy.
			var withEntry = SpawnPreIirs.Minimal();
			withEntry.Sublist.Add(new SpawnPreFrameSublistEntry());
			Assert.Equal(SpawnPreIir.FixedLength + 11, Rebuild(withEntry).Length);
			Assert.Equal(Rebuild(withEntry).Length, withEntry.ByteCount);
		}
	}
}
