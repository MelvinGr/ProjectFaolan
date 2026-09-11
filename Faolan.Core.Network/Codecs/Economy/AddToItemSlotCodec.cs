using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchAddToItemSlot</c> - a gem went into one socket of one item. 84 bytes, and not protobuf: the cell it
	///     happened in, the socket index, then the gem's whole ACG identity flattened.
	/// </summary>
	/// <remarks>
	///     <code>
	///     [u32 container][u32 charInstance][u32 slot]   the item receiving the gem
	///     [u32 socketIndex]                             0-based
	///     [u32 template1][u32 template2][u32 template3] the gem's templates - the level pair, template3 always 0
	///     [u32 level][u32 hash]                         the gem's own level; hash is 0 on a socketed gem
	///     [16B key1][16B key2][16B key3]                the three decryption keys, in ProtoAcgItem order
	///     </code>
	///     <para>
	///         The trailing 68 bytes are exactly a <c>ProtoAcgItem</c>'s fields with the protobuf framing taken off, in
	///         the schema's own field order - which is what identifies the layout rather than a byte-count coincidence:
	///         the captured reply's <c>template1</c>/<c>template2</c> are 3983088/3983089, a real pair, and its
	///         <c>key2</c> is byte-identical to the <c>PairKey</c> harvested for that gem from a different packet in a
	///         different capture.
	///     </para>
	///     <para>
	///         <c>key3</c> is not always <c>key1</c> here. It is on all 344 captured snapshot pairs, but this reply
	///         carries an 8-byte value repeated, the same third key the crafted item's own <c>itemref</c> chat link shows
	///         in another capture. It is therefore a real third key on crafted and generated items; what fills it is
	///         undecoded, and <c>Build(ItemInstanceSocket)</c> sends <c>key1</c>, which is what the catalog can justify.
	///     </para>
	///     <para>
	///         The socketed item's own container entry is not re-sent; the gem leaving the bag is an ordinary
	///         <c>DispatchRemoveItem</c>.
	///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_8.tshark.json fr8248]
	///     </para>
	/// </remarks>
	public static class AddToItemSlotCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchAddToItemSlot;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(AddToItemSlot value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value.Container).WriteUInt32(value.CharInstance).WriteUInt32(value.Slot).WriteUInt32(value.SocketIndex);
			s.WriteUInt32(value.Template1).WriteUInt32(value.Template2).WriteUInt32(value.Template3);
			s.WriteUInt32(value.Level).WriteUInt32(value.Hash);
			Write(s, value.Key1);
			Write(s, value.Key2);
			Write(s, value.Key3);

			return s.ToArray();

			static void Write(FaolanStream stream, ProtoDecryptionKey key)
			{
				stream.WriteUInt32(key.Key1).WriteUInt32(key.Key2).WriteUInt32(key.Key3).WriteUInt32(key.Key4);
			}
		}

		// The server's form: this socket, on this cell. Reads the gem's identity off the catalog row exactly as the
		// snapshot's `slot_map` block does, so the live message and the next login's snapshot describe the same gem.
		public static byte[] Build(ContainerKind container, uint charInstance, uint slot, ItemInstanceSocket socket)
		{
			ArgumentNullException.ThrowIfNull(socket);
			var gem = socket.Gem;

			return Build(new AddToItemSlot(container, charInstance, slot, socket.SocketIndex,
				gem.Id, gem.WireTemplate2, 0, gem.Level, 0, gem.Key, gem.WireKey2, gem.Key));
		}

		public static AddToItemSlot Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(AddToItemSlotCodec));
			var value = new AddToItemSlot((ContainerKind)r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(),
				r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(),
				ReadKey(ref r), ReadKey(ref r), ReadKey(ref r));
			r.EnsureAtEnd();

			return value;
		}

		private static ProtoDecryptionKey ReadKey(ref SpanReader r)
		{
			return new ProtoDecryptionKey
			{
				Key1 = r.ReadU32Be(),
				Key2 = r.ReadU32Be(),
				Key3 = r.ReadU32Be(),
				Key4 = r.ReadU32Be()
			};
		}
	}

	/// <summary>The DispatchAddToItemSlot body.</summary>
	public sealed record AddToItemSlot(
		ContainerKind Container, uint CharInstance, uint Slot, uint SocketIndex,
		uint Template1, uint Template2, uint Template3, uint Level, uint Hash,
		ProtoDecryptionKey Key1, ProtoDecryptionKey Key2, ProtoDecryptionKey Key3);
}
