using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Combat
{
	// RequestAggroStatus (GameCharAgentProject 0x2000) - the client asking what colour a target's overhead name is.
	// `RequestAggroStatus(const ID32& charId, const ID32& targetId)`, so two bare entity ids and nothing else.
	//
	// ⚠️ It GATES the loading screen: the in-world poll must be answered or the client never finishes entering the
	// world (GameServerListener.Inbound). The reply is `UpdateAggroStatusCodec`.
	public static class RequestAggroStatusCodec
	{
		public const uint Method = (uint)GameCharAgentProjectMethods.RequestAggroStatus;

		public static byte[] Build(RequestAggroStatus value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static RequestAggroStatus Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RequestAggroStatusCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static RequestAggroStatus Read(ref SpanReader r)
		{
			return new RequestAggroStatus(EntityIdCodec.Read(ref r), EntityIdCodec.Read(ref r));
		}

		internal static void Write(FaolanStream s, RequestAggroStatus v)
		{
			EntityIdCodec.Write(s, v.Character);
			EntityIdCodec.Write(s, v.Target);
		}
	}

	// RequestAggroStatus(charId, targetId) - the polling player and the entity whose name colour it wants.
	public sealed record RequestAggroStatus(EntityId Character, EntityId Target);
}
