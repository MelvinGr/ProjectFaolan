using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// SimpleDynelVisualChangeIIR (0xE7F4F8F8) - flips a SimpleDynel's visual/collision STATE.
	//
	//   [subop][u32 typeTag][u32 handle][u32 0][u8 state][ trailer ]
	//
	// Retail sends it to the Casilda chain decor the instant she's freed on the beach, switching it to state 5 - which
	// drops the chain's COLLISION = the "invisible wall" (#267; the hangingwoman mesh doesn't render for us, so the
	// dynel reads as an invisible barrier). It is also what opens a loot/container WINDOW on an object (the bag stays
	// in the world; this just opens its UI).
	//
	// ⚠️ The TRAILER is deliberately opaque, because the two captured shapes DISAGREE and nothing in the corpus
	// reconciles them:
	//   * the state flip (Hoxxling gameplay idx 2053, 21 B) ends in four zero bytes;
	//   * the window open (19 + len B) ends in a u16-length-prefixed ASCII state name ("Open", "object_open").
	// A single grammar would need the trailer to be one length-prefixed string, and no reading of the four zero bytes
	// as a u16 length plus something makes the two agree. Rather than invent one, the bytes after the state are
	// carried verbatim and the gap is tracked in Gitea #535. This merges what used to be TWO emitters of this IIR
	// (SimpleDynelMessageCodec.BuildVisualChange and WorldLoot.BuildOpenWindowBody), which is exactly the duplicate
	// the one-IIR-one-codec rule exists to surface.
	public static class SimpleDynelVisualChangeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.SimpleDynelVisualChangeIIR;

		// The state-flip trailer: four zero bytes in every captured state change.
		private static readonly byte[] StateFlipTrailer = [0x00, 0x00, 0x00, 0x00];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SimpleDynelVisualChangeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The state flip - e.g. the Casilda chain decor going to state 5.
		public static byte[] Build(uint handle, byte state)
		{
			return Build(new SimpleDynelVisualChangeIIR(EntityClass.SimpleDynel, handle, 0, state, StateFlipTrailer));
		}

		// The window open, carrying the state NAME ("Open" / "object_open") as a u16-length-prefixed Latin-1 string.
		public static byte[] Build(uint typeTag, uint handle, string state)
		{
			var text = Encoding.Latin1.GetBytes(state);
			byte[] trailer = [(byte)(text.Length >> 8), (byte)text.Length, .. text];
			return Build(new SimpleDynelVisualChangeIIR(typeTag, handle, 0, 0x00, trailer));
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
			var word0 = r.ReadU32Be();
			var state = r.ReadU8();

			// Opaque by decision, not by omission - see the class comment and Gitea #535.
			return new SimpleDynelVisualChangeIIR(typeTag, handle, word0, state, r.ReadRemaining().ToArray());
		}

		internal static void Write(FaolanStream s, SimpleDynelVisualChangeIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(v.TypeTag).WriteUInt32(v.Handle).WriteUInt32(v.Word0).WriteByte(v.State).WriteArray(v.Trailer);
		}
	}

	// SimpleDynelVisualChangeIIR (0xE7F4F8F8). `Trailer` is the still-undecoded region after the state byte (#535).
	public sealed record SimpleDynelVisualChangeIIR(uint TypeTag, uint Handle, uint Word0, byte State, byte[] Trailer);
}
