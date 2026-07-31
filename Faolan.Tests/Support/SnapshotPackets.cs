using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Protobuf;

namespace Faolan.Tests.Support
{
	// Locating the FullPlayerCharacterIIR RECORD inside a captured packet body, for the snapshot tests. Purely
	// positional - no wire model lives here; FullPlayerCharacterCodec still owns every byte (see
	// SnapshotEntryAccessors' header for the line this holds).
	public static class SnapshotPackets
	{
		// [u8 flag][u32 BE rootLength] - the client's blob-length frame ahead of the protobuf root.
		public const int PrefixLength = 5;

		// [u32 subOpcode][u32 typeTag][u32 instance] - the record head every caller below has already stepped over.
		private const int RecordHeadLength = 12;

		public static byte[] BuildBody(uint instance, ProtoFullCharacterIIR root, FullCharSnapshotSubClass subClass)
		{
			using var s = new FaolanStream();
			s.WriteArrayUInt32Length(FullPlayerCharacterCodec.Build(new FullPlayerCharacterIIR(instance, root, subClass)));
			return s.ToArray();
		}

		// The prefix the codec writes for a root of `rootLength` bytes.
		public static byte[] BuildPrefix(int rootLength)
		{
			var prefix = new byte[PrefixLength];
			prefix[0] = 0x00; // flag
			prefix.WriteU32Be(1, (uint)rootLength);
			return prefix;
		}

		// `stream` is positioned right after [subOpcode][typeTag][instance]; rewind to the record start, because the
		// whole record is the unit the codec parses.
		public static FullPlayerCharacterIIR Parse(FaolanStream stream)
		{
			stream.Position -= RecordHeadLength;
			return FullPlayerCharacterCodec.Parse(stream.ReadRemainingArray());
		}

		// The blob-length frame the codec writes for this snapshot's root.
		public static byte[] PrefixOf(FullPlayerCharacterIIR parts)
		{
			return BuildPrefix(parts.Root.ToProtobuf().Length);
		}

		// Where the sub-class's opaque middle starts: the byte length of the Quest_c journal region that opens it.
		// Was FullCharSnapshotSubClass.KnownHeaderLength, a parse artifact the value no longer carries.
		public static int JournalLength(FullCharSnapshotSubClass subClass)
		{
			return QuestcRegionCodec.Build(subClass.Quests).Length;
		}

		// The sub-class bytes AS THEY ARRIVED: step over the record head and the blob-framed root, sequentially.
		public static byte[] SubClassBytes(byte[] record)
		{
			var r = new SpanReader(record, nameof(SnapshotPackets));
			r.Skip(RecordHeadLength);
			r.Skip(1); // blob-frame flag
			r.Skip(checked((int)r.ReadU32Be())); // the protobuf root, delimited by the frame's own length
			return r.ReadRemaining().ToArray();
		}

		public static ProtoFullCharacterIIR Root(FaolanStream stream)
		{
			stream.Position -= RecordHeadLength;
			return SnapshotRoot.Of(stream.ReadRemainingArray());
		}
	}
}
