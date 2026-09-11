using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchGuildBankData</c> - the guild vault's contents:
	///     <c>[u32 blobLen][ProtoInventoryGame][u32 0]</c>.
	/// </summary>
	/// <remarks>
	///     The payload is an ordinary inventory container, the same shape the personal bank and the login snapshot use, so
	///     its items are the same <c>InventoryListEntry</c> entries <c>SnapshotItems</c> produces and nothing about a
	///     guild-owned item is special on the wire.
	///     <para>
	///         Its <c>inventory_id.instance</c> is the guild id rather than a character id. Every other container on the
	///         wire is addressed by the character that owns it, which is why <c>ItemInstance</c> needs an
	///         <c>OwnerGuildId</c> rather than reusing the character owner.
	///     </para>
	///     <para>
	///         The captured push is an empty vault, so the entry list itself is unexercised, but it is the same codec the
	///         bank and snapshot prove and the container header is byte-exact:
	///         <code>
	///         0000000c 0a06 086d 10c7d821 1064 1800 00000000
	///         len 12   inventory_id{type 109, instance 552007}  slots 100  f3 0  trailing 0
	///         </code>
	///         [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:45:09.520 - pushed unasked, in the same
	///         instant as the personal bank's slot registrations and DispatchOpenTradepost, so opening the banker opens
	///         all three. There is no <c>OpenGuildBank</c> on the wire at all.]
	///     </para>
	/// </remarks>
	public static class GuildBankCodec
	{
		public const uint Method = (uint)GameCharInterfaceProjectMethods.DispatchGuildBankData;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(GuildBankData value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteArrayUInt32Length(value.Vault.ToProtobuf());
			s.WriteUInt32(value.Trailing);

			return s.ToArray();
		}

		// The server's form: a vault to push. The trailing u32 is 0 on the captured push and nothing is known to read it, so
		// a caller has no business choosing it, which is why this overload exists rather than making every sender name a word
		// it cannot reason about. It is carried on the value so a parsed push round-trips.
		public static byte[] Build(ProtoInventoryGame vault)
		{
			return Build(new GuildBankData(vault, 0));
		}

		public static GuildBankData Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildBankCodec));
			var vault = r.ReadBytesU32Length().FromProtobuf<ProtoInventoryGame>();
			var trailing = r.ReadU32Be();
			r.EnsureAtEnd();

			return new GuildBankData(vault, trailing);
		}
	}

	/// <summary>The DispatchGuildBankData body.</summary>
	public sealed record GuildBankData(ProtoInventoryGame Vault, uint Trailing);
}
