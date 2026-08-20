using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// SetSprintInputState(bool sprintInput, bool clientCheck) - GameCharAgentProject 0x0C, client -> server. The
	// sprint key, and the third-busiest client message in the corpus: 9,329 frames over 90 captures (#867).
	//
	// ⚠️ **TWO bools, not one u16.** The body is two bytes and all four combinations occur
	// (`--report sprint`): `sprintInput=0 clientCheck=1` x3714, `0/0` x2954, `1/1` x1958, `1/0` x703. Reading it as a
	// single u16 and testing `== 1` - which is what this handler did until #867 - selects `00 01`, i.e. the frames
	// where the player is NOT sprinting, and misses all 2,661 frames where they are. The names are the client's own.
	//
	// `clientCheck` is the client's own local verdict on whether it believes it may sprint; the server is not obliged
	// to agree, which is why the answer (`DispatchSetIsSprinting`) is a separate server-owned flag rather than an ack.
	public static class SprintInputStateCodec
	{
		public const uint Method = (uint)GameCharAgentProjectMethods.SetSprintInputState;

		public static byte[] Build(SprintInputState value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static SprintInputState Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SprintInputStateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static SprintInputState Read(ref SpanReader r)
		{
			var sprintInput = r.ReadU8() != 0;

			return new SprintInputState(sprintInput, r.ReadU8() != 0);
		}

		internal static void Write(FaolanStream s, SprintInputState v)
		{
			s.WriteByte(v.SprintInput ? (byte)0x01 : (byte)0x00);
			s.WriteByte(v.ClientCheck ? (byte)0x01 : (byte)0x00);
		}
	}

	public sealed record SprintInputState(bool SprintInput, bool ClientCheck);
}
