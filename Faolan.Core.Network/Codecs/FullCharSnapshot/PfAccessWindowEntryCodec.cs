using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The `PFAccessWindow_` playfield catalog of the FullChar snapshot tails - the game-static region emitted from
	// the typed AccessWindowCatalog rather than a captured blob (#258), re-carved to the CLIENT's own grammar (#464):
	//
	//   table = [u32 (count + 1) * 0x3F1]  count × entry            (a plain ListFrame list - see PfAccessWindowEntry
	//   entry = [u32 playfieldId][u8 flag][u32 accessType][u32 unknown]    for what the old lookahead carve got wrong)
	//           [u16 len][name][u32 childCount]  childCount × child
	//   child = [u8 flag][u32 id][u16 len][displayName][u16 len][parentName]
	//
	// The client reader is `PlayfieldAccessHolder`'s `FUN_0077d220`/`FUN_0077db70` (the snapshot-tails list the old
	// notes called "list D"); its sibling list ("E", `FUN_0077d5b0`, per record `[u32 key][str][u32 x3][u8][str]`)
	// follows the table and is EMPTY on every captured snapshot, so it rides the post-access block's leading empty
	// frame. The old carve's "no count and no terminator" was this table's own ListFrame word being misread as the
	// quest index's terminal (QuestIndexRegionCodec).
	public static class PfAccessWindowEntryCodec
	{
		// Every name in the shipped catalog carries this prefix - a property of the DATA the catalog tests assert,
		// not of the grammar (the client does not validate names).
		public const string NamePrefix = "PFAccessWindow_";

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(PfAccessWindowEntry value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The whole table, ListFrame word included.
		public static byte[] Build(IReadOnlyList<PfAccessWindowEntry> entries)
		{
			ArgumentNullException.ThrowIfNull(entries);
			using var s = new FaolanStream();
			WriteTable(s, entries);
			return s.ToArray();
		}

		// One entry (no frame word - an entry is only ever part of the table).
		public static PfAccessWindowEntry Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PfAccessWindowEntryCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static List<PfAccessWindowEntry> ReadTable(ref SpanReader r)
		{
			var count = r.ReadListCount();
			var entries = new List<PfAccessWindowEntry>(count);
			for (var i = 0; i < count; i++)
				entries.Add(Read(ref r));

			return entries;
		}

		internal static void WriteTable(FaolanStream s, IReadOnlyList<PfAccessWindowEntry> entries)
		{
			s.WriteUInt32(ListFrame.ForCount(entries.Count));
			foreach (var entry in entries)
				Write(s, entry);
		}

		internal static PfAccessWindowEntry Read(ref SpanReader r)
		{
			var playfieldId = r.ReadU32Be();
			var flag = r.ReadU8();
			var accessType = r.ReadU32Be();
			var unknown = r.ReadU32Be();
			var internalName = r.ReadStringU16Length();

			var childCount = r.ReadU32Be();
			// A count is bounded by the bytes a child needs (9 minimum), so a shifted read fails HERE rather than
			// producing a phantom model - the failure mode the old carve fell into.
			if (childCount > r.Remaining / 9)
				throw CodecParseException.Unexpected(nameof(PfAccessWindowEntryCodec), r.Position - 4, "a plausible child count", childCount.ToString());

			var children = new List<PfAccessWindowChild>((int)childCount);
			for (var i = 0; i < childCount; i++)
			{
				children.Add(new PfAccessWindowChild
				{
					Flag = r.ReadU8(),
					Id = r.ReadU32Be(),
					DisplayName = r.ReadStringU16Length(),
					ParentName = r.ReadStringU16Length()
				});
			}

			return new PfAccessWindowEntry
			{
				PlayfieldId = playfieldId,
				Flag = flag,
				AccessType = accessType,
				Unknown = unknown,
				InternalName = internalName,
				Children = children
			};
		}

		internal static void Write(FaolanStream s, PfAccessWindowEntry v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(v.PlayfieldId);
			s.WriteByte(v.Flag);
			s.WriteUInt32(v.AccessType);
			s.WriteUInt32(v.Unknown);
			s.WriteString(v.InternalName);
			s.WriteUInt32((uint)v.Children.Count);

			foreach (var child in v.Children)
			{
				s.WriteByte(child.Flag);
				s.WriteUInt32(child.Id);
				s.WriteString(child.DisplayName);
				s.WriteString(child.ParentName);
			}
		}
	}
}
