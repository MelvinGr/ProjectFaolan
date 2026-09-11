using System;
using Faolan.Core.Extensions;

// The DB row and this folder share the name `Guild`; alias the row so the value factories below can take it.
using GuildRow = Faolan.Core.Database.Tables.Guild;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     The <c>i_pcGuild</c> argument shared by <c>DispatchGuildUpdate</c> and <c>DispatchGuildCreated</c> - the
	///     <c>CoreGuild_c::Read</c> (FUN_00565900) streaming form, version 3, govForm 1 (case 1 falling through case 3):
	///     <code>
	///     [u8 version=3][u32 guildId][u8 govForm=1][u64 cash][u8 form][u32 0][u8 0]
	///     [name s16][description s16][motd s16][renown block]
	///     </code>
	/// </summary>
	/// <remarks>
	///     The renown block is self-delimiting (<see cref="GuildRenownBlockCodec" />), which is what lets one reader serve
	///     both the 277-byte full-update form and the 225-byte create-time one.
	///     <para>
	///         A sub-record codec: it is always composed into one of those two bodies, so it declares no opcode.
	///         <c>Form</c> is the one field the two parents differ in - the full update writes 0x03, the create echoes the
	///         governing form the player picked in the registrar window. Byte-exact in <c>GuildPacketTests</c>.
	///     </para>
	/// </remarks>
	public static class GuildHeaderCodec
	{
		// The first body byte - the CoreGuild_c streaming version. The client's reader rejects anything else.
		public const byte StreamVersion = 0x03;

		// The byte after guildId: the form selecting the case 1 -> 3 layout this codec writes.
		public const byte GovernmentForm = 0x01;

		// The `Form` byte the full update carries; the create-guild reply carries the request's own form instead.
		public const byte FullUpdateForm = 0x03;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(GuildHeader value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static GuildHeader Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildHeaderCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildHeader Read(ref SpanReader r)
		{
			r.ExpectU8(StreamVersion, "CoreGuild stream version");
			var guildId = r.ReadU32Be();
			r.ExpectU8(GovernmentForm, "CoreGuild governing form");
			var cash = r.ReadU64Be();
			var form = r.ReadU8();
			var reserved = r.ReadU32Be();
			var trailer = r.ReadU8();

			return new GuildHeader(guildId, cash, form, reserved, trailer, r.ReadStringU16Length(), r.ReadStringU16Length(), r.ReadStringU16Length(),
				GuildRenownBlockCodec.Read(ref r));
		}

		internal static void Write(FaolanStream s, GuildHeader v)
		{
			s
				.WriteByte(StreamVersion)
				.WriteUInt32(v.GuildId)
				.WriteByte(GovernmentForm)
				.WriteUInt64(v.Cash)
				.WriteByte(v.Form)
				.WriteUInt32(v.Reserved)
				.WriteByte(v.Trailer)
				.WriteString(v.Name)
				.WriteString(v.Description)
				.WriteString(v.MessageOfTheDay);
			GuildRenownBlockCodec.Write(s, v.Renown);
		}
	}

	/// <summary>
	///     The CoreGuild header a guild body carries. <c>Reserved</c> and <c>Trailer</c> are the two words after
	///     <c>Form</c> that are zero in every capture and are carried rather than asserted.
	/// </summary>
	public sealed record GuildHeader(
		uint GuildId,
		ulong Cash,
		byte Form,
		uint Reserved,
		byte Trailer,
		string Name,
		string Description,
		string MessageOfTheDay,
		GuildRenownBlock Renown);

	/// <summary>The semantic forms of a GuildHeader, as value factories.</summary>
	public static class GuildHeaders
	{
		// The full-update form: the fixed 0x03 form byte and the guild's own renown spliced into the full-update renown
		// block.
		public static GuildHeader ForFullUpdate(GuildRow guild)
		{
			ArgumentNullException.ThrowIfNull(guild);

			return Header(guild, GuildHeaderCodec.FullUpdateForm, GuildRenownBlock.ForRenown(guild.Renown));
		}

		// The create-reply form: the request's governing form and the create-time renown block.
		public static GuildHeader ForGuildCreate(GuildRow guild, byte form)
		{
			ArgumentNullException.ThrowIfNull(guild);

			return Header(guild, form, GuildRenownBlock.ForGuildCreate());
		}

		private static GuildHeader Header(GuildRow guild, byte form, GuildRenownBlock renown)
		{
			return new GuildHeader(guild.Id, guild.Cash, form, 0, 0x00, guild.Name, guild.Description ?? "", guild.MessageOfTheDay ?? "", renown);
		}
	}
}
