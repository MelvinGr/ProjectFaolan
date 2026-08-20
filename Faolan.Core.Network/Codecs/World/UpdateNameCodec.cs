using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchUpdateName(nCharID, cName) - GameCharInterface 0x01. Renames a LIVE entity on the client (#874).
	//
	//   [u32 nCharID][u16 len][ascii cName]
	//
	// ⚠️ `nCharID` is a BARE u32 handle, not an ID32 - there is no `0xC350` class word in front of it. The single
	// captured frame is 16 bytes: handle `0x013184AE`, length 10, "Strickenne", and 4 + 2 + 10 = 16 accounts for
	// every byte.
	//
	// Distinct from `SetNameIIR`, which carries the same idea as an IIR record with an entity class word; this is the
	// RPC form, and the nameplate work (#363) is where a live rename would come from.
	public static class UpdateNameCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchUpdateName;

		public static byte[] Build(UpdateName value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId, string name)
		{
			return Build(new UpdateName(characterId, name));
		}

		public static UpdateName Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateNameCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static UpdateName Read(ref SpanReader r)
		{
			var characterId = r.ReadU32Be();

			return new UpdateName(characterId, r.ReadStringU16Length());
		}

		internal static void Write(FaolanStream s, UpdateName v)
		{
			s.WriteUInt32(v.CharacterId).WriteString(v.Name);
		}
	}

	public sealed record UpdateName(uint CharacterId, string Name);
}
