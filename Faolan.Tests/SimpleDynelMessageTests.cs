using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Locks the typed SimpleDynel identity codec to the captured wire bytes (#187 / #57): the
	// SimpleDynelFullUpdateIIR (0x66AEDD50) message is lifted out of the opaque Data slice into a typed
	// ProtoSimpleDynelIIR and rebuilt BYTE-EXACT. Record captured from
	// Hoxxling_mage-heraldofxotli_login (the "Acheronian Ruins" world dynel). Reader decoded by
	// vtable-xref - docs/iir-subclass-readers.md; framing validated by Captures/scripts/decode_iir_subclass_readers.py.
	public class SimpleDynelMessageTests
	{
		// The full SimpleDynel IIR record (the bytes after the 0x2000 carrier's [u32 len][u32 iirType] prefix):
		// [u32 type=0000C878][u32 handle][u32 0][transform][u32 blobLen=0x68][ProtoSimpleDynelIIR][kd-tree tail].
		private const string RecordHex = "0000c87800001037000000002c0a0f0d3792614415d35d1f431d63a1334412140d00000000154033803e1d00000000255ad8773f18e0fcaa0f000000" +
		                                 "680800121041636865726f6e69616e205275696e731a4e3c6c6f63616c697a65642069643d2233363436363835222063617465676f72793d22353530" +
		                                 "30302220666c6167733d2222206b65793d2269524b344123712e4e584041424332324f52666b22203e2801600100" +
		                                 "02614f000f696e0000000000000000bf2d7c2d3e5014f9bd38811b3f26365d3f05e5193c7112fd3f4000000000000000";

		[Fact]
		public void BuildMessage_RoundTripsByteExact()
		{
			var record = Convert.FromHexString(RecordHex);
			Assert.True(SimpleDynelMessageCodec.TryParse(record, out var msg, out var start, out var consumed));

			var rebuilt = SimpleDynelMessageCodec.BuildMessage(msg);
			var captured = record.AsSpan(start, consumed).ToArray();

			Assert.Equal(Convert.ToHexString(captured), Convert.ToHexString(rebuilt));
		}

		[Fact]
		public void FromColumns_ReproducesRecordByteExact()
		{
			// The DB-source path: lift the identity into WorldObject columns (importer), then rebuild the message
			// from those columns (builder). Columns -> message -> splice must reproduce the captured record exactly.
			var record = Convert.FromHexString(RecordHex);
			Assert.True(SimpleDynelMessageCodec.TryParse(record, out var msg, out _, out _));

			var row = new WorldObject { SubOpcode = SimpleDynelMessageCodec.SubOpcode };
			SimpleDynelMessageCodec.ApplyColumns(row, msg); // importer side
			Assert.Equal("Acheronian Ruins", row.Name);
			Assert.Equal(1u, row.CarsGroup);
			Assert.True(row.IsClosed);
			Assert.Null(row.QuestItemEffect); // absent on this dynel
			Assert.Null(row.TemplateItemId);

			Assert.True(SimpleDynelMessageCodec.TryReplaceIdentityFromColumns(record, row, out var rebuilt)); // builder side
			Assert.Equal(Convert.ToHexString(record), Convert.ToHexString(rebuilt));
		}

		[Fact]
		public void Parse_ReadsTypedFields()
		{
			var record = Convert.FromHexString(RecordHex);

			Assert.True(SimpleDynelMessageCodec.TryParse(record, out var msg, out var start, out var consumed));
			Assert.Equal(57, start); // after [12-byte header][1-byte transform len 0x2C][44-byte transform]
			Assert.Equal(108, consumed); // 4-byte length prefix + 104-byte protobuf (0x68)

			Assert.Equal("Acheronian Ruins", msg.Name);
			Assert.Equal(0u, msg.Kdtree);
			Assert.Equal(1u, msg.CarsGroup);
			Assert.True(msg.IsClosed);
			Assert.StartsWith("<localized id=\"3646685\"", msg.DisplayName);
		}

		[Fact]
		public void SpliceRebuiltMessage_ReproducesWholeRecord()
		{
			// Replacing the opaque identity slice with the typed-message rebuild is drop-in safe: header + transform
			// (kept) + rebuilt message + kd-tree tail (kept) == the original record, byte for byte.
			var record = Convert.FromHexString(RecordHex);
			Assert.True(SimpleDynelMessageCodec.TryParse(record, out var msg, out var start, out var consumed));

			var rebuilt = SimpleDynelMessageCodec.BuildMessage(msg);
			var spliced = new byte[record.Length];
			record.AsSpan(0, start).CopyTo(spliced); // header + transform
			rebuilt.CopyTo(spliced.AsSpan(start)); // typed identity message
			record.AsSpan(start + consumed).CopyTo(spliced.AsSpan(start + rebuilt.Length)); // kd-tree tail

			Assert.Equal(Convert.ToHexString(record), Convert.ToHexString(spliced));
		}
	}
}
