using System;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Pvp;
using Xunit;

namespace Faolan.Tests
{
	// 0x20A5 DispatchMassivePvpInfoRequestReturn - the Massive-PvP battlekeep board. The body below is
	// retail's, verbatim from Hakotep_hox_login_gameplay0.
	//
	// It is the shortest captured body whose last entry has a non-empty `otherName`, and that is the whole point of
	// picking it: the grammar this codec replaced ended an entry with "[u16 len][name][7 zero bytes]", which is the
	// same width as `[u16 0][u32 0][u8 0]` and so parses every board where `otherName` happens to be empty - 122 of
	// the 125 distinct retail bodies. Only a body from the other three can tell the two readings apart.
	public class MassivePvpInfoCodecTests
	{
		private const string CapturedBoard =
			"6a4a562c00002379000000010000000100002b2000001c200000038400000747000004eb6a4bdf100007b9ec0005552e532e42000000000000000000" +
			"00020000000200002b2000001c20000003840000025d0000043d6a4da1100007c6a6000e48616e64206f662053747967696100000000000000000000" +
			"030000000300002b2000001c2000000384000001ef000006d46a4b0c200007dfca000d466c65757273206465204c7973000000000000000000000400" +
			"00000400002f2600001c20000003840000015a0000048b6a4e82100007b726000a2d56656e64657474612d0000000000000000000005000000050000" +
			"2f2600001c200000038400000606000003fc6a4d309000083f7e00114b696c6c206f72206265204b696c6c6564000000000000000000000600000006" +
			"00002f2600001c20000003840000026f000001676a4d76e00008192a001b2d44656164204b6f77204b756c742d20547972616e6e7920507650000000" +
			"000000000000000700000007000032fa00001c20000003840000010d0000051e6a4ac5d00007a87d00122d42616e64206f662042726f74686572732d" +
			"000000000000000000000800000008000032fa00001c20000003840000046a000001746a4a474000083a190009496e63757273696f6e000b46757279" +
			"20284465616429000000090100000009";

		[Fact]
		public void CapturedBoard_RoundTripsByteExact()
		{
			var body = Convert.FromHexString(CapturedBoard);
			var parsed = MassivePvpInfoCodec.Parse(body);

			Assert.Equal(8, parsed.Entries.Count);
			Assert.Equal(MassivePvpInfoCodec.Build(parsed), body);
		}

		[Fact]
		public void CapturedBoard_DecodesTheColumnsTheClientNames()
		{
			var parsed = MassivePvpInfoCodec.Parse(Convert.FromHexString(CapturedBoard));

			// The list is a map: the key ahead of each element repeats the entry's own rank, which is why a captured
			// entry starts with its rank twice.
			for (var i = 0; i < parsed.Entries.Count; i++)
			{
				Assert.Equal((uint)(i + 1), parsed.Entries[i].Key);
				Assert.Equal(parsed.Entries[i].Key, parsed.Entries[i].Rank);
			}

			// Three battlekeep areas over the eight rows, in contiguous runs.
			Assert.Equal([11040u, 11040u, 11040u, 12070u, 12070u, 12070u, 13050u, 13050u], Array.ConvertAll([.. parsed.Entries], e => e.AreaId));

			// The two duration columns are constant in every entry of every captured board.
			Assert.All(parsed.Entries, e => Assert.Equal(7200u, e.BattleSeconds));
			Assert.All(parsed.Entries, e => Assert.Equal(900u, e.SignupSeconds));

			Assert.Equal("U.S.B", parsed.Entries[0].GuildName);

			// The row that only this body can prove: a non-empty trailing string, with the tail pair that moves with
			// it. Reading those 17 bytes as padding loses both.
			var last = parsed.Entries[^1];
			Assert.Equal("Incursion", last.GuildName);
			Assert.Equal("Fury (Dead)", last.OtherName);
			Assert.Equal(9u, last.Tail);
			Assert.Equal(1, last.TailFlag);

			// Every other row's trailing string is empty and its tail pair zero.
			var others = parsed.Entries.Take(parsed.Entries.Count - 1).ToArray();

			Assert.All(others, e => Assert.Equal("", e.OtherName));
			Assert.All(others, e => Assert.Equal(0u, e.Tail));
		}

		// The board the server actually sends: no Massive-PvP layer, so no guilds. This is the one form with no
		// capture oracle (every retail board carries 8), so it is pinned here explicitly - and the trailing word is
		// load-bearing: dropping it makes the client read past the end and drop the whole CSPlayerAgent route.
		[Fact]
		public void EmptyBoard_IsTheListFrameAndTheTrailingWord()
		{
			var body = MassivePvpInfoCodec.Build(MassivePvpInfo.Empty);

			Assert.Equal(12, body.Length);
			Assert.Equal(0u, body.ReadU32Be(0));
			Assert.Equal(ListFrame.ForCount(0), body.ReadU32Be(4));
			Assert.Equal(1u, body.ReadU32Be(8));
			Assert.Empty(MassivePvpInfoCodec.Parse(body).Entries);
		}

		[Fact]
		public void Parse_RejectsABodyThatEndsInsideAnEntry()
		{
			var truncated = Convert.FromHexString(CapturedBoard)[..64];

			Assert.ThrowsAny<Exception>(() => MassivePvpInfoCodec.Parse(truncated));
		}
	}
}
