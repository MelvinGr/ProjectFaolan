using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.GroupFinder
{
	// s2c DispatchGroupFinderReceivedNeededRole (0x20CE): the answer to c2s RequestNeededRole(uint32 activityId) -
	// the activity echoed back plus the role word the finder still needs for it. Captured as [00000026][00000001]
	// (activity 38, roles 0x1) both times it fires in chat-annotated-4; the role word's bit meaning is the client's
	// role bitmask (the player answers SelectGroupFinderRoles with 0x4 for the third role button).
	public static class GroupFinderNeededRoleCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGroupFinderReceivedNeededRole;

		public static byte[] Build(GroupFinderNeededRole value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(value.ActivityId).WriteUInt32(value.NeededRoles);

			return s.ToArray();
		}

		public static byte[] Build(uint activityId, uint neededRoles)
		{
			return Build(new GroupFinderNeededRole(activityId, neededRoles));
		}

		public static GroupFinderNeededRole Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GroupFinderNeededRoleCodec));
			var activityId = r.ReadU32Be();
			var neededRoles = r.ReadU32Be();
			r.EnsureAtEnd();

			return new GroupFinderNeededRole(activityId, neededRoles);
		}
	}

	public sealed record GroupFinderNeededRole(uint ActivityId, uint NeededRoles);
}
