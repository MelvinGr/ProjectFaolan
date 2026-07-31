using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// SimpleDynelVisualChangeIIR (0xE7F4F8F8) - flips a SimpleDynel's visual/collision STATE.
	//
	//   [subop][u32 typeTag][u32 handle][u8 0][u32 kind][ kind-specific payload ]
	//
	// Retail sends it to the Casilda chain decor the instant she's freed on the beach, switching it to kind 5 - which
	// drops the chain's COLLISION = the "invisible wall" (#267; the hangingwoman mesh doesn't render for us, so the
	// dynel reads as an invisible barrier). It is also what opens a loot/container WINDOW on an object (the bag stays
	// in the world; this just opens its UI).
	//
	// ⚠️ THE TAIL IS DISCRIMINATED BY `Kind`, and the record's own field split was off by one byte (#535). This used
	// to read `[u32 word0][u8 state]` and carry everything after as an opaque `Trailer`, on the reasoning that "the
	// two captured shapes DISAGREE and nothing in the corpus reconciles them" - one ending in four zero bytes, the
	// other in a length-prefixed name. They are the same grammar: `word0`'s three low bytes and the `state` byte are
	// ONE big-endian u32, and that u32 selects the payload. Reading a u32 as `[u32][u8]` is why the leading word
	// looked like a constant zero and the discriminator looked like a byte.
	//
	// The switch is the client's own (`SimpleDynelVisualChangeIIR_c::ReadSubClass` = FUN_007a6910, reached through
	// the vtable's ReadSubClass slot 8), so the payload sizes are stated rather than inferred from what happened to
	// be captured:
	//
	//   kind 0/1/6/7  [u16 len][ascii]   a state NAME - "Open", "object_open", "object_closed"
	//   kind 2        6 x u32            3 read inline, then 3 more through FUN_004e0300 (a shared 3-word reader;
	//                                    zero in every capture). FUN_007a6e10 beside it reads NOTHING - it appends
	//                                    to a 0x20-stride container.
	//   kind 3/5      1 x u32
	//   kind 4        2 x u32
	//   kind 8/9      1 x u32            carries a float on the wire (0x3E4CCCCD = 0.2 in every capture)
	//   kind 10       1 x u32
	//   kind 11       12 x u32           (FUN_00c9b900)
	//   kind 12       16 x u32
	//
	// All 2633 captured bodies parse and re-encode byte-exact, with nothing left over
	// (SimpleDynelVisualChangeCodecTests). The corpus exercises kinds 0, 2, 3, 4, 5, 8 and 9; the rest come from the
	// client's reader, which is why they are implemented rather than left to throw.
	//
	// This codec merges what used to be TWO emitters of this IIR (SimpleDynelMessageCodec.BuildVisualChange and
	// WorldLoot.BuildOpenWindowBody), which is exactly the duplicate the one-IIR-one-codec rule exists to surface.
	public static class SimpleDynelVisualChangeCodec
	{
		public const uint Opcode = (uint)IirTypes.SimpleDynelVisualChangeIIR;

		// The kind that carries a state NAME rather than words - what a window open sends.
		public const uint NamedKind = 0;

		// The byte between the handle and the kind. Consumed by the BASE record's read rather than by ReadSubClass,
		// and zero in all 2633 captured bodies - so it is expected, not carried.
		private const byte BaseTail = 0x00;

		// How many u32 each non-named kind's payload carries, straight off the client's switch.
		private static readonly Dictionary<uint, int> PayloadWords = new()
		{
			[2] = 6, [3] = 1, [4] = 2, [5] = 1, [8] = 1, [9] = 1, [10] = 1, [11] = 12, [12] = 16
		};

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SimpleDynelVisualChangeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The state flip - e.g. the Casilda chain decor going to kind 5. Its single payload word is 0 in every
		// captured flip of the kind we send.
		public static byte[] Build(uint handle, byte state)
		{
			return Build(new SimpleDynelVisualChangeIIR(EntityClass.SimpleDynel, handle, state, null, [0]));
		}

		// The window open, carrying the state NAME ("Open" / "object_open") - kind 0.
		public static byte[] Build(uint typeTag, uint handle, string state)
		{
			return Build(new SimpleDynelVisualChangeIIR(typeTag, handle, NamedKind, state, []));
		}

		public static SimpleDynelVisualChangeIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SimpleDynelVisualChangeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SimpleDynelVisualChangeIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SimpleDynelVisualChangeIIR subop");
			var typeTag = r.ReadU32Be();
			var handle = r.ReadU32Be();
			r.ExpectU8(BaseTail, "the base record's byte ahead of the kind");
			var kind = r.ReadU32Be();

			if (IsNamed(kind))
				return new SimpleDynelVisualChangeIIR(typeTag, handle, kind, Encoding.Latin1.GetString(r.ReadBytesU16Length()), []);

			if (!PayloadWords.TryGetValue(kind, out var words))
				throw CodecParseException.Unexpected(nameof(SimpleDynelVisualChangeCodec), r.Position - 4, "a kind the client's reader knows (0-12)", kind.ToString());

			var payload = new uint[words];
			for (var i = 0; i < words; i++)
				payload[i] = r.ReadU32Be();

			return new SimpleDynelVisualChangeIIR(typeTag, handle, kind, null, payload);
		}

		internal static void Write(FaolanStream s, SimpleDynelVisualChangeIIR v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(Opcode).WriteUInt32(v.TypeTag).WriteUInt32(v.Handle).WriteByte(BaseTail).WriteUInt32(v.Kind);

			if (IsNamed(v.Kind))
			{
				var text = Encoding.Latin1.GetBytes(v.Name ?? "");
				s.WriteUInt16(text.Length).WriteArray(text);

				return;
			}

			if (!PayloadWords.TryGetValue(v.Kind, out var words))
				throw new ArgumentOutOfRangeException(nameof(v), v.Kind, "not a kind the client's reader knows (0-12)");

			if (v.Words.Count != words)
				throw new ArgumentOutOfRangeException(nameof(v), v.Words.Count, $"kind {v.Kind} takes exactly {words} payload words");

			foreach (var word in v.Words)
				s.WriteUInt32(word);
		}

		// The four kinds whose payload is a length-prefixed name. They are one branch in the client's switch, split
		// across `case 0/6/7` and `case 1` only because the decompiler kept the duplicated tails apart.
		private static bool IsNamed(uint kind)
		{
			return kind is 0 or 1 or 6 or 7;
		}
	}

	// SimpleDynelVisualChangeIIR (0xE7F4F8F8). Exactly one of `Name` / `Words` carries the payload, selected by
	// `Kind` - see the codec for the client's switch.
	public sealed record SimpleDynelVisualChangeIIR(uint TypeTag, uint Handle, uint Kind, string? Name, IReadOnlyList<uint> Words);
}
