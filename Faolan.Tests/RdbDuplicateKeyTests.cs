using System;
using System.IO;
using Faolan.Core.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// `RdbReader.TryGet` must resolve a duplicate `(type, id)` deliberately, not with `GroupBy(...).First()`: that
	// lets the first index entry win with nothing saying a choice was made, or in which order "first" was taken. It is
	// harmless on this client (its `le.idx` has zero collisions), but the failure it sets up is nasty: a patch/override layer
	// adds a second entry for a resource we read, every consumer silently gets the older copy, and the symptom reads
	// as wrong content rather than a wrong pick.
	//
	// Both halves of the fix are pinned here, and neither can be checked against the real client - a client with no
	// duplicates cannot demonstrate a duplicate rule. So the index is synthesised: `le.idx` is a plain flat format
	// (28-byte header, an 8-byte (type,id) per entry, then a fixed-stride detail record each), which makes a
	// two-entry index with a deliberate collision a few lines of code.
	public class RdbDuplicateKeyTests
	{
		private const int HeaderBytes = 28;
		private const int KeyBytes = 8;
		private const int DetailStrideV5 = 44;

		// A minimal v5 `le.idx` holding the given (type, id, size) entries in the order listed. Everything the reader
		// does not look at is left zero; `size` is the per-entry field the assertions use to tell two otherwise
		// identical entries apart.
		private static string WriteIndex(params (uint Type, uint Id, uint Size)[] entries)
		{
			var dir = Path.Combine(Path.GetTempPath(), "faolan-rdbdup-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);

			var b = new byte[HeaderBytes + entries.Length * (KeyBytes + DetailStrideV5)];
			"IBDR"u8.CopyTo(b);
			BitConverter.GetBytes(5u).CopyTo(b, 4); // version 5 (AoC)
			BitConverter.GetBytes((uint)entries.Length).CopyTo(b, 24);

			var detailOff = HeaderBytes + entries.Length * KeyBytes;
			for (var i = 0; i < entries.Length; i++)
			{
				BitConverter.GetBytes(entries[i].Type).CopyTo(b, HeaderBytes + i * KeyBytes);
				BitConverter.GetBytes(entries[i].Id).CopyTo(b, HeaderBytes + i * KeyBytes + 4);

				var d = detailOff + i * DetailStrideV5;
				b[d] = 0; // file 0 - a local rdbdata entry
				BitConverter.GetBytes(0u).CopyTo(b, d + 4); // offset
				BitConverter.GetBytes(entries[i].Size).CopyTo(b, d + 8);
			}

			File.WriteAllBytes(Path.Combine(dir, "le.idx"), b);
			return dir;
		}

		// The rule: the last entry for a key wins, because a later index entry is an override of the earlier one.
		[Fact]
		public void TryGet_OnADuplicateKey_ServesTheLastEntry()
		{
			var dir = WriteIndex((1000020, 7, 111), (1000020, 7, 222));
			try
			{
				using var rdb = new RdbReader(dir);

				Assert.True(rdb.TryGet(1000020, 7, out var e));
				Assert.Equal(222u, e.Size);
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		// And it does not stop being loud about it: a served override is still a fact the caller can print, which is
		// what `--only rdb-inventory` does with it.
		[Fact]
		public void DuplicateKeys_ReportsEveryCollidingKeyWithItsCount()
		{
			var dir = WriteIndex((1000020, 7, 111), (1000020, 7, 222), (1000020, 7, 333), (1010001, 9, 444));
			try
			{
				using var rdb = new RdbReader(dir);

				var dup = Assert.Single(rdb.DuplicateKeys);
				Assert.Equal((1000020u, 7u, 3), dup);
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		// The uncollided case has to be visibly empty rather than merely untested - "0 duplicates" is the answer an
		// audit needs, and without this it takes a hand-written index parser to get.
		[Fact]
		public void DuplicateKeys_IsEmpty_WhenEveryKeyIsDistinct()
		{
			var dir = WriteIndex((1000020, 7, 111), (1000020, 8, 222), (1010001, 7, 333));
			try
			{
				using var rdb = new RdbReader(dir);

				Assert.Empty(rdb.DuplicateKeys);
				Assert.True(rdb.TryGet(1000020, 8, out var e));
				Assert.Equal(222u, e.Size);
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}
	}
}
