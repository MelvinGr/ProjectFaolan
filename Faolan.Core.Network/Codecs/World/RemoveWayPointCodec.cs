using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchRemoveWayPoint(i_cID, i_nPlayfieldID) - GameCharInterface 0x7B. Takes the pin down (#868).
	//
	//   [ID32 i_cID][u32 i_nPlayfieldID]
	//
	// Twelve bytes - the first twelve of the ADD, which is what confirms both readings at once: the corpus's single
	// remove frame is `0000C87B 00000058 00000444`, and an add frame in the same capture opens with exactly those
	// twelve bytes. Same marker, planted then taken down.
	//
	// A separate codec from `WayPointCodec` rather than a `HandledMethods` sibling, because the bodies genuinely
	// differ: `HandledMethods` is for methods that share ONE layout, not for a prefix of one.
	public static class RemoveWayPointCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchRemoveWayPoint;

		public static byte[] Build(RemoveWayPoint value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(EntityId id, uint playfieldId)
		{
			return Build(new RemoveWayPoint(id, playfieldId));
		}

		public static RemoveWayPoint Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RemoveWayPointCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static RemoveWayPoint Read(ref SpanReader r)
		{
			var id = EntityIdCodec.Read(ref r);

			return new RemoveWayPoint(id, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, RemoveWayPoint v)
		{
			EntityIdCodec.Write(s, v.Id);
			s.WriteUInt32(v.PlayfieldId);
		}
	}

	public sealed record RemoveWayPoint(EntityId Id, uint PlayfieldId);
}
