using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     DispatchUpdateAggroStatus - the reply to a RequestAggroStatus poll, and the sole driver of an NPC's overhead
	///     name colour. <c>Category</c> is the client's <c>cCategory</c>: 0 friendly (green), 1 hostile (red), 2 neutral
	///     (yellow), proven by live debugging, where DispatchUpdateAggroStatus into FUN_0115eb10 emits the disposition
	///     signal that writes color_friendly_npc / color_hostile_npc / color_neutral_npc.
	/// </summary>
	/// <remarks>
	///     It is neither an aggro toggle nor the spawn's community: the client ignores the community for the nameplate.
	///     <para>
	///         The method index belongs to GameCharInterfaceProject, not to the PassBlob IIR carrier that shares the number
	///         on GameInterface - the opcode space is per interface, so the send must be spelled with this interface's enum.
	///     </para>
	/// </remarks>
	public static class UpdateAggroStatusCodec
	{
		public const uint Method = (uint)GameCharInterfaceProjectMethods.DispatchUpdateAggroStatus;

		public static byte[] Build(UpdateAggroStatus value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static UpdateAggroStatus Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateAggroStatusCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static UpdateAggroStatus Read(ref SpanReader r)
		{
			return new UpdateAggroStatus(EntityIdCodec.Read(ref r), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, UpdateAggroStatus v)
		{
			EntityIdCodec.Write(s, v.Target);
			s.WriteUInt32(v.Category);
		}
	}

	/// <summary>The DispatchUpdateAggroStatus body: 0 friendly, 1 hostile, 2 neutral.</summary>
	public sealed record UpdateAggroStatus(EntityId Target, uint Category);
}
