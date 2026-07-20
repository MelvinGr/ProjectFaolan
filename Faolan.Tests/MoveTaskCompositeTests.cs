using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Round-trip gate for the full recursive MoveTaskIIR codec (MoveTaskComposite): Parse -> Encode reproduces EVERY
	// captured MoveTask record byte-exact, across all capture JSONs (#53 grammar, #190 construction). This is the
	// production-language proof that any MoveTask form - incl. the recursive ListMoveTask (mode 4) that dominates the
	// cutscene burst - can be constructed, not just replayed.
	public class MoveTaskCompositeTests
	{
		private static readonly byte[] Sub = Convert.FromHexString("96c46740");
		private static readonly string CapturesDir = TestPaths.CapturesDir;

		// A recursive ListMoveTask (mode 4) - the cutscene burst's dominant form - nests child factories; assert the
		// model exposes them and the re-encode is byte-exact.
		[Fact]
		public void Parse_DecodesNestedListMoveTask()
		{
			var listRec = AllMoveTaskRecords().First(r => r.Length > 27 && r[26] == 0x04);

			var c = MoveTaskComposite.Parse(listRec);

			Assert.Equal((byte)0x04, c.Task.Mode);
			Assert.NotEmpty(c.Task.Children);
			Assert.Equal(Convert.ToHexStringLower(listRec), Convert.ToHexStringLower(c.Encode()));
		}

		[Fact]
		public void Parse_ThenEncode_RoundTripsEveryCapturedRecord()
		{
			var records = AllMoveTaskRecords().ToList();
			Assert.True(records.Count > 5000, $"expected the full MoveTask corpus, got {records.Count}");

			var mismatches = 0;
			foreach (var rec in records)
			{
				var rebuilt = MoveTaskComposite.Parse(rec).Encode();
				if (!rec.AsSpan().SequenceEqual(rebuilt))
					mismatches++;
			}

			Assert.Equal(0, mismatches);
		}

		// Every length-prefixed [u32 len][0x96C46740...] record across all capture JSONs (the inner record the
		// MoveTaskCodec builders emit, minus the 4-byte length the burst framing adds).
		private static IEnumerable<byte[]> AllMoveTaskRecords()
		{
			// From the committed corpus extract, so this runs without the capture repo. Verified equivalent: all
			// 10241 MoveTask records in the corpus are TOP-LEVEL packets - none is nested inside another packet's
			// body - so filtering by IIR loses nothing versus scanning every body.
			foreach (var (_, raw) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.MoveTaskIIR))
			{
				var o = 0;
				while (true)
				{
					var j = IndexOf(raw, Sub, o);
					if (j < 0)
						break;

					o = j + 1;
					if (j < 4)
						continue;

					var len = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(j - 4, 4));
					if (len is >= 8 and <= 512 && j + len <= raw.Length)
						yield return raw[j..(j + len)];
				}
			}
		}

		private static int IndexOf(byte[] hay, byte[] needle, int start)
		{
			for (var i = start; i + needle.Length <= hay.Length; i++)
			{
				if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
					return i;
			}

			return -1;
		}
	}
}
