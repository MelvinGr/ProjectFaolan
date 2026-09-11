using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchClientAddGuildRenown</c> - "you just earned renown", the message behind the renown gain the client
	///     shows, as <c>[u32 renownType][u64 amount][u32 guildOnly][u8 capType]</c> (17 bytes).
	/// </summary>
	/// <remarks>
	///     The client's method table names its four fields <c>(renownType, amount, guildOnly, capType)</c>
	///     [V1: docs/re/aoc-client-rpc-methods.json], and the c2s twin <c>AddGuildRenown(const ID32&amp; character, uint8
	///     grType, uint64 amount, bool onlyGuild)</c> types three of them.
	///     <para>
	///         All ten captured records are 17 bytes and agree: renownType is always 2, guildOnly and capType always 0,
	///         and the only field that moves is the amount (1, 1, 1, 1, 1, 1, 1, 3, 139, 108). The amount slot is a u64
	///         rather than two u32 because the c2s twin declares <c>uint64 amount</c>; its high word is always zero.
	///     </para>
	///     <para>
	///         The renown type values are the guild window's renown tabs (Valor / Glory / Artistry), but only type 2 is
	///         captured, so which tab that is remains open. [V0: the guild renown type ids]
	///     </para>
	/// </remarks>
	public static class ClientAddGuildRenownCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchClientAddGuildRenown;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(ClientAddGuildRenown value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.RenownType).WriteUInt64(value.Amount).WriteUInt32(value.GuildOnly ? 1u : 0u).WriteByte(value.CapType);

			return s.ToArray();
		}

		public static byte[] Build(uint renownType, ulong amount)
		{
			return Build(new ClientAddGuildRenown(renownType, amount));
		}

		public static ClientAddGuildRenown Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ClientAddGuildRenownCodec));
			var renownType = r.ReadU32Be();
			var amount = r.ReadU64Be();
			var value = new ClientAddGuildRenown(renownType, amount, r.ReadU32Be() != 0, r.ReadU8());
			r.EnsureAtEnd();

			return value;
		}
	}

	// The only renown type any capture carries. Named for what it is rather than which tab it draws on, which is
	// undecided.
	public sealed record ClientAddGuildRenown(uint RenownType, ulong Amount, bool GuildOnly = false, byte CapType = 0)
	{
		public const uint CapturedRenownType = 2;
	}
}
