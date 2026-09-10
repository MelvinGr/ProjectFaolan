using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     One realm in <c>PlayerInterface.SetDimensionList</c> - the client's <c>Dimension_t</c>.
	/// </summary>
	/// <remarks>
	///     ⭐ Every member is named for the OFFSET it lands at in the client's own record, because that is the only
	///     thing about this message that is checkable. It was recovered from the per-entry reader at
	///     <c>0x005888C0</c>, which stores u32, u32, string, then SIX u32s, a bool, a u32 and a bool - the sequence
	///     below, exactly.
	///     <para />
	///     ⚠️ <c>DimensionList_t</c> carries hand-written getters for <c>+0x24</c> through <c>+0x38</c>, and they have
	///     ZERO callers - the client inlines the map lookup instead - so the accessor list says nothing about what is
	///     actually used. The consumers are <c>+0x04</c>, <c>+0x08</c>, <c>+0x24</c>, <c>+0x30</c>, <c>+0x38</c> and
	///     <c>+0x40</c>; <c>+0x28</c>, <c>+0x2c</c>, <c>+0x34</c>, <c>+0x3c</c> and <c>+0x44</c> have none.
	/// </remarks>
	public sealed record DimensionEntry(
		uint Id,
		RealmOnlineStatus OnlineStatus,
		string Name,
		RealmLoadStatus LoadStatus,
		bool IsFull,
		RealmType Type,
		RealmLanguage Language)
	{
		/// <summary>
		///     +0x24 - the realm's LANGUAGE FLAGS, a bitfield, and the field the client actually resolves a
		///     dimension's language from.
		/// </summary>
		/// <remarks>
		///     ⭐ Decoded from <c>FUN_00A69180</c>, which maps it to the client's own language id: bit 0 or bit 1 set
		///     returns 0 (English), <c>0x400</c> returns 5 (Russian), <c>0x800</c> returns 4 (Polish),
		///     <c>0x100000</c> returns 6 (Korean), and anything else falls back to 0. <c>FUN_00A68170</c> reads the
		///     same word and tests bits 0 and 1.
		///     <para />
		///     ⚠️ So this - NOT <see cref="DimensionEntry.Language" /> at +0x40 - is what the client consults. It used
		///     to be a hard-coded 1, which sets the English bit: right by accident for an English realm and impossible
		///     to change for any other. <see cref="RealmLanguageFlags" /> derives it from the realm's declared
		///     language now (#933), and carries why French, Spanish and German share the English-family bit.
		/// </remarks>
		public uint LanguageFlags { get; init; } = 1;

		/// <summary>+0x28 - no consumer, no decode.</summary>
		public uint Unknown28 { get; init; }

		/// <summary>+0x2c - no consumer, no decode.</summary>
		public uint Unknown2C { get; init; }

		/// <summary>+0x44 - no consumer, no decode.</summary>
		public byte Trailer { get; init; }
	}

	/// <summary>
	///     The realm-select screen's list - the client's <c>cDimensionList</c> parameter, whose class is
	///     <c>DimensionList_t</c>.
	/// </summary>
	/// <remarks>
	///     ⚠️ A plain u32 count, NOT a <c>ListFrame</c>. This message does not use the <c>(count + 1) * 0x3F1</c>
	///     framing - the client reads the count and loops - so do not "correct" it into one.
	/// </remarks>
	public sealed record DimensionList(DimensionEntry[] Realms);

	/// <summary>
	///     <c>PlayerInterface.SetDimensionList(cDimensionList)</c> - the realm-select screen's list.
	/// </summary>
	/// <remarks>
	///     ⚠️ The whole list crosses the wire as ONE opaque parameter - the client's only failure string for it is
	///     "stream bad after 'cDimensionList'" - so unlike most messages here there are no per-field signature
	///     strings to name anything by. Every name in <see cref="DimensionEntry" /> is ours, pinned to an offset.
	/// </remarks>
	public static class SetDimensionListCodec
	{
		public const uint Method = (uint)PlayerInterfaceMethods.SetDimensionList;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(DimensionList value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value.Realms.Length);

			foreach (var realm in value.Realms)
			{
				s.WriteUInt32(realm.Id);                       // +0x00
				s.WriteUInt32((uint)realm.OnlineStatus);       // +0x04 READ: the row renderer's `== 2` gate
				s.WriteString(realm.Name);                     // +0x08 READ
				s.WriteUInt32(realm.LanguageFlags);            // +0x24 READ: the language BITFIELD, see the remark
				s.WriteUInt32(realm.Unknown28);                // +0x28
				s.WriteUInt32(realm.Unknown2C);                // +0x2c

				// ⚠️ +0x30 is the LOAD LEVEL, and it takes the 0..4 enum rather than a flag derived from it. The
				// renderer FUN_011f4490 runs this value down a chain against 0, 1, 2, 3 and 4, rendering display
				// code value+1 and falling back to 0 (Offline) for anything else - so a boolean here can only ever
				// select the first two of five levels. Sending the `== Full` flag here, and the enum in the slot
				// below, was #931; it hid because every realm we serve is Recommended, which makes both 0.
				s.WriteUInt32((uint)realm.LoadStatus);         // +0x30 READ: 0..4, display = +1
				s.WriteUInt32(realm.IsFull ? 1u : 0u);         // +0x34 - no consumer

				// Type spans the u32 at +0x38 and the bool at +0x3c, high byte first. The client compares +0x38
				// against 7 at 0x11f6e0f; our three declared types put 0, 1 or 4 there, so that arm is never taken.
				s.WriteUInt16(0x0000);
				s.WriteByte(0x00);
				s.WriteUInt16((ushort)realm.Type);             // +0x38 READ (high byte) / +0x3c (low byte)

				// ⚠️ +0x40 IS read, but only for being NON-ZERO: FUN_011F3510 counts the realms whose +0x40 is
				// non-zero and shows or hides the "FlagButtons" widget row accordingly (0x011F41E3 and its
				// duplicated copy at 0x011F4304). The client never branches on the VALUE here - the language it
				// resolves per dimension comes from the +0x24 bitfield instead.
				s.WriteUInt32((uint)realm.Language);           // +0x40 READ: tested != 0 only
				s.WriteByte(realm.Trailer);                    // +0x44 - no consumer
			}

			return s.ToArray();
		}

		public static DimensionList Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SetDimensionListCodec));

			// ⚠️ A PLAIN u32 count, not a ListFrame - this message does not use that framing. The client reads the
			// count and loops; there is no (count + 1) * 0x3F1 anywhere in it.
			var count = r.ReadU32Be();
			var realms = new DimensionEntry[count];

			for (var i = 0; i < count; i++)
			{
				var id = r.ReadU32Be();
				var online = r.ReadU32Be();
				var name = r.ReadStringU16Length();
				var languageFlags = r.ReadU32Be();
				var unknown28 = r.ReadU32Be();
				var unknown2C = r.ReadU32Be();
				var load = r.ReadU32Be();
				var full = r.ReadU32Be();
				r.ReadU16Be();
				r.ReadU8();
				var type = r.ReadU16Be();
				var language = r.ReadU32Be();
				var trailer = r.ReadU8();

				realms[i] = new DimensionEntry(id, (RealmOnlineStatus)online, name, (RealmLoadStatus)load, full != 0, (RealmType)type, (RealmLanguage)language)
				{
					LanguageFlags = languageFlags,
					Unknown28 = unknown28,
					Unknown2C = unknown2C,
					Trailer = trailer
				};
			}

			r.EnsureAtEnd();

			return new DimensionList(realms);
		}
	}
}
