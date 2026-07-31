using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// One `PFAccessWindow_` playfield record out of the FullChar snapshot tails - the game-static catalog region
	// (@1292..7917 on the beach template), emitted from the typed AccessWindowCatalog rather than a captured blob
	// (#258).
	//
	//   entry  = [u32 playfieldId][u8 sectionOrTier][u32 kind][u32 unknown][u16 len][name][u8 0]
	//            [u16 unknownBeforeChildMode][u8 childMode][ child, when childMode != 0 ]
	//   child  = [u8 variant] then a per-mode shape (see ReadChild)
	//   detail = [u8 detailUnknown][NUL-terminated shortCode][u8-len displayName][u8 0][u8-len parentName]
	//
	// ⚠️ The table has no count and no terminator: the reader consumes entries while the bytes AHEAD still look like
	// an entry start (LooksLikeEntryStart), which is also how childMode 3's detail list finds its end. That is the
	// client's own shape, not a shortcut - the catalog is followed directly by the post-access block.
	public static class PfAccessWindowEntryCodec
	{
		// Every name in the catalog carries this prefix, which is what makes the lookahead selective.
		public const string NamePrefix = "PFAccessWindow_";

		// [u32 playfieldId][u8 sectionOrTier][u32 kind][u32 unknown][u16 nameLength] - the fixed head the lookahead
		// validates before committing to an entry.
		private const int HeadLength = 4 + 1 + 4 + 4 + 2;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(PfAccessWindowEntry value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The whole catalog. Entries are self-delimiting and the table carries no count and no terminator, so it IS
		// the concatenation of its entries - which is why this delegates rather than encoding a second time.
		public static byte[] Build(IEnumerable<PfAccessWindowEntry> entries)
		{
			return [.. entries.SelectMany(e => Build(e))];
		}

		public static PfAccessWindowEntry Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PfAccessWindowEntryCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		// True when the bytes at the reader's position open a well-formed entry. Used both to find the end of the
		// catalog and to find the end of a childMode-3 detail list, which is why it is a lookahead over the
		// REMAINING span rather than a read.
		internal static bool LooksLikeEntryStart(in ReadOnlySpan<byte> ahead)
		{
			if (ahead.Length < HeadLength)
				return false;

			var playfieldId = ahead.ReadU32Be(0);
			var sectionOrTier = ahead[4];
			var kind = ahead.ReadU32Be(5);
			var nameLength = ahead.ReadU16Be(13);

			if (playfieldId is 0 or > 200_000 || sectionOrTier > 10 || kind > 10)
				return false;

			var nameEnd = HeadLength + nameLength;
			if (nameEnd >= ahead.Length || ahead[nameEnd] != 0)
				return false;

			return ahead.Slice(HeadLength, nameLength).StartsWith(Encoding.Latin1.GetBytes(NamePrefix));
		}

		// The whole catalog. It has no count and no terminator, so it ends where the bytes ahead stop looking like an
		// entry - which is the post-access block's leading empty list frame.
		internal static List<PfAccessWindowEntry> ReadTable(ref SpanReader r)
		{
			var entries = new List<PfAccessWindowEntry>();
			while (LooksLikeEntryStart(r.PeekRemaining()))
			{
				var save = r;
				try
				{
					entries.Add(Read(ref r));
				}
				catch (CodecParseException)
				{
					// The head looked like an entry but the body did not follow through - that is the end of the
					// table, not a corrupt catalog. Rewind so the parent sees the bytes.
					r = save;
					break;
				}
			}

			return entries;
		}

		internal static void WriteTable(FaolanStream s, IEnumerable<PfAccessWindowEntry> entries)
		{
			foreach (var entry in entries)
				Write(s, entry);
		}

		internal static PfAccessWindowEntry Read(ref SpanReader r)
		{
			var offset = r.Position;
			var playfieldId = r.ReadU32Be();
			var sectionOrTier = r.ReadU8();
			var kind = r.ReadU32Be();
			var unknown = r.ReadU32Be();
			var internalName = r.ReadStringU16Length();
			r.ExpectU8(0, "access-window name terminator");

			if (!internalName.StartsWith(NamePrefix, StringComparison.Ordinal))
				throw CodecParseException.Unexpected(nameof(PfAccessWindowEntryCodec), offset, $"a name starting with {NamePrefix}", internalName);

			var unknownBeforeChildMode = r.ReadU16Be();
			var childMode = r.ReadU8();
			if (childMode > 3)
				throw CodecParseException.Unexpected(nameof(PfAccessWindowEntryCodec), r.Position - 1, "child mode 0-3", childMode.ToString());

			return new PfAccessWindowEntry
			{
				Offset = offset,
				PlayfieldId = playfieldId,
				SectionOrTier = sectionOrTier,
				Kind = kind,
				Unknown = unknown,
				InternalName = internalName,
				UnknownBeforeChildMode = unknownBeforeChildMode,
				ChildMode = childMode,
				Child = childMode == 0 ? null : ReadChild(ref r, childMode)
			};
		}

		internal static void Write(FaolanStream s, PfAccessWindowEntry v)
		{
			ArgumentNullException.ThrowIfNull(v);

			s.WriteUInt32(v.PlayfieldId);
			s.WriteByte(v.SectionOrTier);
			s.WriteUInt32(v.Kind);
			s.WriteUInt32(v.Unknown);
			s.WriteString(v.InternalName);
			s.WriteByte(0);
			s.WriteUInt16(v.UnknownBeforeChildMode);
			s.WriteByte(v.ChildMode);

			if (v.ChildMode == 0)
				return;

			if (v.Child == null)
				throw new InvalidOperationException($"Access-window entry at 0x{v.Offset:X8} has childMode {v.ChildMode}, but Child == null.");

			WriteChild(s, v.ChildMode, v.Child);
		}

		private static PfAccessWindowChild ReadChild(ref SpanReader r, byte childMode)
		{
			var variant = r.ReadU8();

			if (childMode == 1)
			{
				// Variant 0 carries the id pair; anything else carries a single detail whose mode IS the variant.
				if (variant == 0)
				{
					return new PfAccessWindowChild
					{
						Variant = variant,
						Id = r.ReadU32Be(),
						Unknown = r.ReadU32Be(),
						DetailsMode = null,
						Details = []
					};
				}

				return new PfAccessWindowChild
				{
					Variant = variant,
					Id = null,
					Unknown = null,
					DetailsMode = variant,
					Details = [ReadChildDetail(ref r, variant)]
				};
			}

			var id = r.ReadU32Be();
			var unknown = r.ReadU32Be();

			if (childMode == 2)
			{
				var detailsMode = r.ReadU8();
				return new PfAccessWindowChild
				{
					Variant = variant,
					Id = id,
					Unknown = unknown,
					DetailsMode = detailsMode,
					Details = detailsMode == 0 ? [] : [ReadChildDetail(ref r, detailsMode)]
				};
			}

			// Mode 3 has been observed on playfield 6300 (PFAccessWindow_ChosainProvince, PFAccessWindow_Shaulun).
			// No explicit detail count has ever been observed, so the list ends where the next top-level entry
			// starts - the same lookahead the catalog loop itself uses.
			var details = new List<PfAccessWindowChildDetail>();
			while (!LooksLikeEntryStart(r.PeekRemaining()))
			{
				if (r.AtEnd)
					throw CodecParseException.Unexpected(nameof(PfAccessWindowEntryCodec), r.Position, "a childMode 3 detail or the next entry", "end of data");

				var detailsMode = r.ReadU8();
				if (detailsMode == 0)
					throw CodecParseException.Unexpected(nameof(PfAccessWindowEntryCodec), r.Position - 1, "a non-zero childMode 3 detail mode", "0");

				details.Add(ReadChildDetail(ref r, detailsMode));
			}

			return new PfAccessWindowChild
			{
				Variant = variant,
				Id = id,
				Unknown = unknown,
				DetailsMode = childMode,
				Details = details
			};
		}

		private static PfAccessWindowChildDetail ReadChildDetail(ref SpanReader r, byte detailsMode)
		{
			var detailUnknown = r.ReadU8();

			// Usually readable like "M2W", but some values use bytes above 0x7E (M2 uses 0x84) - so Latin1, and no
			// strict-ASCII validation.
			var shortCode = r.ReadStringNullTerminated();
			var displayName = r.ReadStringU8Length();
			r.ExpectU8(0, "access-window display-name terminator");

			// No NUL here: in many records the next 0 byte belongs to the next playfield id, not to this string.
			var parentName = r.ReadStringU8Length();

			return new PfAccessWindowChildDetail
			{
				DetailsMode = detailsMode,
				DetailUnknown = detailUnknown,
				ShortCode = shortCode,
				DisplayName = displayName,
				ParentName = parentName
			};
		}

		private static void WriteChild(FaolanStream s, byte childMode, PfAccessWindowChild child)
		{
			s.WriteByte(child.Variant);

			if (childMode == 1)
			{
				if (child.Variant == 0)
				{
					s.WriteUInt32(child.Id ?? throw new InvalidOperationException("childMode 1 variant 0 requires Id."));
					s.WriteUInt32(child.Unknown ?? throw new InvalidOperationException("childMode 1 variant 0 requires Unknown."));
					return;
				}

				WriteSingleDetail(s, child, child.Variant);
				return;
			}

			if (childMode is not (2 or 3))
				throw new InvalidOperationException($"Unsupported child mode {childMode}.");

			s.WriteUInt32(child.Id ?? throw new InvalidOperationException($"childMode {childMode} requires Id."));
			s.WriteUInt32(child.Unknown ?? throw new InvalidOperationException($"childMode {childMode} requires Unknown."));

			if (childMode == 2)
			{
				var detailsMode = child.DetailsMode ?? 0;
				s.WriteByte(detailsMode);
				if (detailsMode != 0)
					WriteSingleDetail(s, child, detailsMode);

				return;
			}

			foreach (var detail in child.Details)
			{
				s.WriteByte(detail.DetailsMode);
				WriteChildDetailBody(s, detail);
			}
		}

		private static void WriteChildDetailBody(FaolanStream s, PfAccessWindowChildDetail detail)
		{
			s.WriteByte(detail.DetailUnknown);
			s.WriteArray(Encoding.Latin1.GetBytes(detail.ShortCode));
			s.WriteByte(0);
			WriteU8LengthString(s, detail.DisplayName);
			s.WriteByte(0);
			WriteU8LengthString(s, detail.ParentName);
		}

		private static void WriteSingleDetail(FaolanStream s, PfAccessWindowChild child, byte expectedDetailsMode)
		{
			if (child.Details.Count != 1)
				throw new InvalidOperationException($"Expected exactly one detail for detailsMode {expectedDetailsMode}, got {child.Details.Count}.");

			var detail = child.Details[0];
			if (detail.DetailsMode != expectedDetailsMode)
				throw new InvalidOperationException($"Detail mode mismatch. Expected {expectedDetailsMode}, got {detail.DetailsMode}.");

			WriteChildDetailBody(s, detail);
		}

		private static void WriteU8LengthString(FaolanStream s, string value)
		{
			var bytes = Encoding.Latin1.GetBytes(value);
			s.WriteByte(checked((byte)bytes.Length));
			s.WriteArray(bytes);
		}
	}
}
