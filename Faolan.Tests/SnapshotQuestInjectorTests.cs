using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// Byte-surgery tests for SnapshotQuestInjector against the REAL beach tails template (the snapshot region the
	// in-client render uses). Verifies the index is found + extended and the block is spliced in - the parts that
	// don't need a live client. The in-client gate (whether the resulting tails parse + render) is separate.
	public class SnapshotQuestInjectorTests
	{
		private static readonly byte[] Template = LoadTemplate();

		private static byte[] LoadTemplate()
		{
			for (var dir = AppContext.BaseDirectory; dir != null; dir = Directory.GetParent(dir)?.FullName)
			{
				var p = Path.Combine(dir, "Faolan", "GameServer", "Packets", "FullCharSnapshot", "BeachTailsTemplate.bin");
				if (File.Exists(p))
					return File.ReadAllBytes(p);
			}

			throw new FileNotFoundException("BeachTailsTemplate.bin not found above " + AppContext.BaseDirectory);
		}

		// Parse the index [0x3F1][marker][id x N][N][0xF1] -> (ids, count, indexOffset).
		private static (uint[] Ids, uint Count, int Off) ParseIndex(byte[] tails)
		{
			for (var i = 0; i + 12 <= tails.Length; i++)
			{
				if (U32(tails, i) == 0x000003F1 && U32(tails, i + 8) >= 0x1000)
				{
					var o = i + 8;
					var ids = new System.Collections.Generic.List<uint>();
					while (U32(tails, o) >= 0x1000) { ids.Add(U32(tails, o)); o += 4; }
					return (ids.ToArray(), U32(tails, o), i);
				}
			}

			throw new InvalidOperationException("index tag not found");
		}

		private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));

		[Fact]
		public void Template_HasExactlyTheBeachQuest()
		{
			var (ids, count, _) = ParseIndex(Template);
			Assert.Equal(new uint[] { 10238 }, ids);
			Assert.Equal(1u, count);
		}

		[Fact]
		public void Inject_AddsBlockAndExtendsIndex()
		{
			var block = Enumerable.Range(0, 879).Select(i => (byte)(i & 0xFF)).ToArray(); // a stand-in 13479 detail block
			var outp = SnapshotQuestInjector.Inject(Template, new (uint, byte[])[] { (13479u, block) });

			var (ids, count, off) = ParseIndex(outp);
			Assert.Equal(new uint[] { 10238, 13479 }, ids); // beach quest first, new quest appended
			Assert.Equal(2u, count);                        // count bumped
			Assert.Equal(Template.Length + block.Length + 4, outp.Length); // block + the extra id u32

			// The block bytes sit immediately before the index tag.
			Assert.Equal(block, outp[(off - block.Length)..off]);

			// Everything from the index sentinel onward is preserved verbatim.
			var (_, _, tOff) = ParseIndex(Template);
			var tSentinel = tOff + 8 + 4 * 1 + 4; // tag+marker + 1 id + count
			var oSentinel = off + 8 + 4 * 2 + 4;  // tag+marker + 2 ids + count
			Assert.Equal(Template[tSentinel..], outp[oSentinel..]);
		}

		[Fact]
		public void Inject_IsNoOp_ForEmptyOrAlreadyPresent()
		{
			Assert.Same(Template, SnapshotQuestInjector.Inject(Template, []));
			// 10238 is already listed -> nothing added, returns the input unchanged.
			Assert.Same(Template, SnapshotQuestInjector.Inject(Template, new (uint, byte[])[] { (10238u, new byte[10]) }));
		}
	}
}
