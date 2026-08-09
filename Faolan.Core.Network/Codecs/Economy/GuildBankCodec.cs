using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchGuildBankData (s2c GameCharInterfaceProject 0x01) - the guild vault's contents (#654).
	//
	//   [u32 blobLen][ProtoInventoryGame][u32 0]
	//
	// The payload is an ORDINARY inventory container, the same shape the personal bank and the login snapshot use -
	// so its items are the same `InventoryListEntry` entries `SnapshotItems` already produces, and nothing about a
	// guild-owned item is special on the wire.
	//
	// ⚠️ Its `inventory_id.instance` is the GUILD id, not a character id. Every other container on the wire is
	// addressed by the character that owns it; this is the one that is not, which is why `ItemInstance` needed an
	// `OwnerGuildId` rather than reusing the character owner.
	//
	// The captured push is an EMPTY vault, so the entry list itself is unexercised - but it is the same codec the
	// bank and snapshot prove, and the container header is byte-exact:
	//
	//   0000000c 0a06 086d 10c7d821 1064 1800 00000000
	//   len 12   inventory_id{type 109, instance 552007}  slots 100  f3 0  trailing 0
	//
	// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:45:09.520 - pushed unasked, in the same
	//  instant as the personal bank's slot registrations and DispatchOpenTradepost, so opening the banker opens all
	//  three. There is no `OpenGuildBank` (0x1B) on the wire at all.]
	public static class GuildBankCodec
	{
		public const uint Method = (uint)GameCharInterfaceProjectMethods.DispatchGuildBankData;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GuildBankData value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteArrayUInt32Length(value.Vault.ToProtobuf());
			s.WriteUInt32(value.Trailing);

			return s.ToArray();
		}

		// The server's form: a vault to push. The trailing u32 is 0 on the captured push and nothing is known to read
		// it, so a caller has no business choosing it - which is why this overload exists rather than making every
		// sender name a word it cannot reason about. It is carried on the value so a parsed push round-trips.
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

	// DispatchGuildBankData (0x01).
	public sealed record GuildBankData(ProtoInventoryGame Vault, uint Trailing);
}
