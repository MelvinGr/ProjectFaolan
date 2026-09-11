using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     RequestAggroStatus - the client asking what colour a target's overhead name is. The signature is
	///     <c>RequestAggroStatus(const ID32&amp; charId, const ID32&amp; targetId)</c>, so two bare entity ids and nothing
	///     else.
	/// </summary>
	/// <remarks>
	///     The in-world poll gates the loading screen: it must be answered or the client never finishes entering the world
	///     (see GameServerListener.Inbound). The reply is <see cref="UpdateAggroStatusCodec" />.
	/// </remarks>
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

	/// <summary>
	///     The RequestAggroStatus body: the polling player and the entity whose name colour it wants.
	/// </summary>
	public sealed record RequestAggroStatus(EntityId Character, EntityId Target);
}
