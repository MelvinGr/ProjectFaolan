using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchInDungeonPlayfield(inDungeon) - GameCharInterfaceProject 0x07. Tells the client whether the playfield
	// it is standing in counts as a DUNGEON (#874).
	//
	//   [u8 inDungeon]
	//
	// One byte, and the corpus carries both values - 00 in a Tortage-city session and 01 in the Amphitheatre of
	// Karutonia instance - so this is a real per-playfield flag rather than a constant. It is what the dungeon and
	// instance-difficulty work (#309 / #511) has to set when it decides a playfield's kind.
	public static class DungeonPlayfieldCodec
	{
		public const uint Method = (uint)GameCharInterfaceProjectMethods.DispatchInDungeonPlayfield;

		public static byte[] Build(DungeonPlayfield value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(bool inDungeon)
		{
			return Build(new DungeonPlayfield(inDungeon));
		}

		public static DungeonPlayfield Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(DungeonPlayfieldCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static DungeonPlayfield Read(ref SpanReader r)
		{
			return new DungeonPlayfield(r.ReadU8() != 0);
		}

		internal static void Write(FaolanStream s, DungeonPlayfield v)
		{
			s.WriteByte(v.InDungeon ? (byte)0x01 : (byte)0x00);
		}
	}

	public sealed record DungeonPlayfield(bool InDungeon);
}
