using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The <c>PFAccessWindow_</c> playfield catalog of the FullChar snapshot tails - the game-static region emitted
	///     from the typed <see cref="Constants.AccessWindowCatalog" /> rather than a captured blob, carved to the client's
	///     own grammar:
	///     <code>
	///     table = [list frame]  count x entry
	///     entry = [u32 playfieldId][u8 flag][u32 accessType][u32 unknown]
	///             [u16 len][name][u32 childCount]  childCount x child
	///     child = [u8 flag][u32 id][u16 len][displayName][u16 len][parentName]
	///     </code>
	/// </summary>
	/// <remarks>
	///     The client reader is <c>PlayfieldAccessHolder</c>'s <c>FUN_0077d220</c>/<c>FUN_0077db70</c>. Its sibling list
	///     (<c>FUN_0077d5b0</c>, per record <c>[u32 key][str][u32 x3][u8][str]</c>) follows the table and is empty on
	///     every captured snapshot, so it rides the post-access block's leading empty frame.
	/// </remarks>
	public static class PfAccessWindowEntryCodec
	{
		// Every name in the shipped catalog carries this prefix - a property of the data the catalog tests assert, not of
		// the grammar; the client does not validate names.
		public const string NamePrefix = "PFAccessWindow_";

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(PfAccessWindowEntry value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The whole table, list frame word included.
		public static byte[] Build(IReadOnlyList<PfAccessWindowEntry> entries)
		{
			ArgumentNullException.ThrowIfNull(entries);
			using var s = new FaolanStream();
			WriteTable(s, entries);
			return s.ToArray();
		}

		// One entry; no frame word, since an entry is only ever part of the table.
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
			var requiresToken = r.ReadU8();
			var accessType = r.ReadU32Be();
			var secondsRemaining = r.ReadU32Be();
			var internalName = r.ReadStringU16Length();

			var childCount = r.ReadU32Be();
			// A count is bounded by the bytes a child needs (9 minimum), so a shifted read fails here rather than producing a
			// phantom model.
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
				RequiresToken = requiresToken,
				AccessType = accessType,
				SecondsRemaining = secondsRemaining,
				InternalName = internalName,
				Children = children
			};
		}

		internal static void Write(FaolanStream s, PfAccessWindowEntry v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(v.PlayfieldId);
			s.WriteByte(v.RequiresToken);
			s.WriteUInt32(v.AccessType);
			s.WriteUInt32(v.SecondsRemaining);
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
