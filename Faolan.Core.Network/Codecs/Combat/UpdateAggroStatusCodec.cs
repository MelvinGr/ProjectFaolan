using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Combat
{
	// DispatchUpdateAggroStatus (GameCharInterfaceProject 0x2000) - the reply, and the SOLE driver of an NPC's
	// overhead name colour. `Category` is the client's `cCategory`: 0 friendly (green), 1 hostile (red), 2 neutral
	// (yellow) - proven by live gdb (#256), where DispatchUpdateAggroStatus -> FUN_0115eb10 emits the disposition
	// signal that writes `color_friendly_npc` / `color_hostile_npc` / `color_neutral_npc`.
	//
	// ⚠️ NOT an aggro toggle, and NOT the spawn's `community`: the client ignores the community for the nameplate.
	//
	// ⚠️ 0x2000 here is the RPC method index on GameCharInterfaceProject, NOT the PassBlob IIR carrier that shares the
	// number on GameInterface - the opcode space is PER INTERFACE. The server used to spell the send with
	// `GameInterfaceMethods.DispatchPassBlob`, which is the same word on the wire and the wrong name for it.
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

	// DispatchUpdateAggroStatus(targetId, cCategory) - 0 friendly / 1 hostile / 2 neutral.
	public sealed record UpdateAggroStatus(EntityId Target, uint Category);
}
