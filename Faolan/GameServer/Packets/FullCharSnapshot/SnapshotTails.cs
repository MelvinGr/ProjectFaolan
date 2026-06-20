using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Faolan.Core.Data;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	public sealed class SnapshotTails
	{
		[JsonIgnore]
		public bool HasKnownRepeatedStatTable => PostAccessBlock?.LargestAdjacentRepeatedRun is not null;

		[JsonIgnore]
		public byte[] OriginalData { get; private init; } = [];

		// The quest JOURNAL region at the START of the tails (version + 0x3F1-framed Quest_c list), modeled from
		// the recovered FullPlayerCharacterIIR grammar (docs/fullplayer-snapshot-reader.md) instead of an opaque
		// header+string blob. Replaces the former Header0/Unknown*/RemoteFormat* fields, which were really the
		// first Quest_c record's header + its 5 <remoteformat> strings.
		public QuestcRegion Quests { get; init; } = new();

		// The @609 per-goal objective-detail region (chapterTag + per-goal records), modeled from the recovered
		// grammar (QuestObjectiveRegion, docs/fullplayer-snapshot-reader.md) instead of riding in the opaque blob.
		// Null when the tails carry no such region (no chapterTag at the boundary) - then it stays in UnknownBefore.
		public QuestObjectiveRegion? QuestObjectives { get; init; }

		// The quest INDEX region (the [0x3F1] index + the "W6QT" goal-id table) between the @609 objective region and
		// the access-window table; modeled by QuestIndexRegion. Null when not present (then it rides UnknownBefore).
		public QuestIndexRegion? QuestIndex { get; init; }

		public byte[] UnknownAfterAccessWindowTable { get; init; }

		// Everything between the @609 objective region and the access-window table (the quest INDEX + any tail);
		// still opaque (decoded by Captures/scripts/decode_snapshot_quests.py).
		public byte[] UnknownBeforeAccessWindowTable { get; init; }

		public int KnownHeaderLength { get; init; }

		public List<PfAccessWindowEntry> AccessWindowEntries { get; init; }

		public TailsPostAccessBlock? PostAccessBlock { get; init; }

		// Back-compat shim: the lead quest's STATE word (@20). World entry historically patched the live
		// character "level" here - the field is actually the first Quest_c record's state (for a fresh beach
		// char level == state == 1, so the write was a no-op). Kept so BeachTailsBuilder/RetailLoginReplay
		// continue to compile and produce identical bytes.
		[JsonIgnore]
		public uint Level
		{
			get => Quests.Records.Count > 0 ? Quests.Records[0].State : 0;
			set { if (Quests.Records.Count > 0) Quests.Records[0].State = value; }
		}

		public byte[] ToArray()
		{
			using var writer = new FaolanStream();

			writer.WriteArray(Quests.ToArray());

			if (QuestObjectives is not null)
				writer.WriteArray(QuestObjectives.ToArray());

			if (QuestIndex is not null)
				writer.WriteArray(QuestIndex.ToArray());

			writer.WriteArray(UnknownBeforeAccessWindowTable);

			foreach (var entry in AccessWindowEntries)
				WriteAccessWindowEntry(writer, entry);

			if (PostAccessBlock is not null)
				WritePostAccessBlock(writer, PostAccessBlock);
			else
				writer.WriteArray(UnknownAfterAccessWindowTable);

			return writer.ToArray();
		}

		// Serializes a sequence of access-window entries to their wire bytes (the inverse of the parse loop in
		// Deserialize). Used by the from-scratch tails builder to emit the game-static PFAccessWindow_ catalog
		// (AccessWindowCatalog) instead of a captured byte blob; byte-identical to the captured region (#258).
		public static byte[] SerializeAccessWindowEntries(IEnumerable<PfAccessWindowEntry> entries)
		{
			using var writer = new FaolanStream();
			foreach (var entry in entries)
				WriteAccessWindowEntry(writer, entry);
			return writer.ToArray();
		}

		public static SnapshotTails Deserialize(byte[] data)
		{
			ArgumentNullException.ThrowIfNull(data);

			// The tails open with the Quest_c journal region (version + 0x3F1-framed Quest_c list); parse it as a
			// typed region and take the boundary where it ends as the start of the remaining opaque blob.
			var quests = QuestcRegion.Parse(data, out var knownHeaderLength);

			// Then the @609 per-goal objective-detail region (when present - it opens with the 0x0BD3 chapterTag).
			// Modeled typed; on anything unexpected fall back to leaving it in the opaque UnknownBefore blob so a
			// tails shape we don't fully recognise is never corrupted.
			var questObjectives = TryParseQuestObjectives(data, knownHeaderLength, out var afterObjectives);

			var accessWindowNameOffset = IndexOfAscii(data, "PFAccessWindow_");

			if (accessWindowNameOffset < 0)
			{
				var questIndexNoAw = TryParseQuestIndex(data, afterObjectives, data.Length, out var afterIndexNoAw);
				var unknownBeforeAccessWindowTable = data[afterIndexNoAw..];

				return new SnapshotTails
				{
					Quests = quests,
					QuestObjectives = questObjectives,
					QuestIndex = questIndexNoAw,
					KnownHeaderLength = knownHeaderLength,
					UnknownBeforeAccessWindowTable = unknownBeforeAccessWindowTable,
					AccessWindowEntries = [],
					UnknownAfterAccessWindowTable = [],
					PostAccessBlock = null,
					OriginalData = data
				};
			}

			// Top-level access entry prefix before the first PFAccessWindow_* string:
			//
			// u32 playfieldId
			// u8  sectionOrTier
			// u32 kind
			// u32 unknown
			// u16 internalNameLength
			var accessWindowTableStart = accessWindowNameOffset - 15;

			if (accessWindowTableStart < afterObjectives)
				accessWindowTableStart = afterObjectives;

			// The quest index sits between the @609 region and the access-window table; model it typed (its trailer
			// runs to the table start). On anything unexpected it stays in the opaque UnknownBefore blob.
			var questIndex = TryParseQuestIndex(data, afterObjectives, accessWindowTableStart, out var afterIndex);
			var unknownBeforeAccessWindowTable0 = data[afterIndex..accessWindowTableStart];

			using var accessReader = new FaolanStream(data, false) { Position = checked((uint)accessWindowTableStart) };

			var accessWindowEntries = new List<PfAccessWindowEntry>();

			while (TryReadAccessWindowEntry(accessReader, data, out var accessWindowEntry))
				accessWindowEntries.Add(accessWindowEntry);

			var unknownAfterAccessWindowTable = data[checked((int)accessReader.Position)..];
			var postAccessBlock = TryReadPostAccessBlock(unknownAfterAccessWindowTable);

			return new SnapshotTails
			{
				Quests = quests,
				QuestObjectives = questObjectives,
				QuestIndex = questIndex,
				KnownHeaderLength = knownHeaderLength,
				UnknownBeforeAccessWindowTable = unknownBeforeAccessWindowTable0,
				AccessWindowEntries = accessWindowEntries,
				UnknownAfterAccessWindowTable = unknownAfterAccessWindowTable,
				PostAccessBlock = postAccessBlock,
				OriginalData = data
			};
		}

		// Parses the @609 per-goal objective-detail region when the boundary opens with the 0x0BD3 chapterTag;
		// reports the offset just past it (= the start of the quest index / remaining opaque blob). Returns null and
		// leaves `afterObjectives == boundary` for any tails without that region or that fail to parse cleanly.
		private static QuestObjectiveRegion? TryParseQuestObjectives(byte[] data, int boundary, out int afterObjectives)
		{
			afterObjectives = boundary;
			if (boundary + 4 > data.Length)
				return null;
			// The objective region opens with the goal-list framing word (goals+1)*0x3F1 (#255); the beach's is 0xBD3.
			var lead = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(boundary, 4));
			if (lead == 0 || lead % QuestObjectiveRegion.Frame != 0)
				return null;

			try
			{
				var region = QuestObjectiveRegion.Parse(data, boundary, out var consumed);
				afterObjectives = boundary + consumed;
				return region;
			}
			catch (Exception)
			{
				afterObjectives = boundary;
				return null;
			}
		}

		// Parses the quest index region when the boundary opens with the 0x3F1 index tag; its trailer runs to
		// `endOffset` (the access-window table start). Returns null (and leaves `afterIndex == offset`) for tails
		// without it or that fail to parse cleanly, so an unrecognised shape stays in the opaque blob.
		private static QuestIndexRegion? TryParseQuestIndex(byte[] data, int offset, int endOffset, out int afterIndex)
		{
			afterIndex = offset;
			if (offset + 4 > data.Length || endOffset < offset
				|| BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)) != QuestIndexRegion.IndexTag)
				return null;

			try
			{
				var region = QuestIndexRegion.Parse(data, offset, endOffset);
				afterIndex = endOffset; // the region consumes to the boundary (its trailer is the remainder)
				return region;
			}
			catch (Exception)
			{
				afterIndex = offset;
				return null;
			}
		}

		private static List<uint> CopyRange(List<uint> values, int startIndex, int length)
		{
			var result = new List<uint>(length);

			for (var i = 0; i < length; i++)
				result.Add(values[startIndex + i]);

			return result;
		}

		private static void ExpectByte(FaolanStream reader, byte expected)
		{
			var actualOffset = reader.Position;
			var actual = reader.ReadByte();

			if (actual != expected)
				throw new InvalidOperationException($"Unexpected byte at offset 0x{actualOffset:X8}. Expected 0x{expected:X2}, got 0x{actual:X2}.");
		}

		private static TailsRepeatedUInt32Run? FindLargestAdjacentRepeatedRun(List<uint> values)
		{
			TailsRepeatedUInt32Run? bestRun = null;

			for (var startIndex = 0; startIndex < values.Count; startIndex++)
			{
				var maxLength = (values.Count - startIndex) / 2;

				for (var length = maxLength; length >= 8; length--)
				{
					if (!UInt32RangesEqual(values, startIndex, startIndex + length, length))
						continue;

					if (bestRun is null || length > bestRun.Length)
					{
						bestRun = new TailsRepeatedUInt32Run
						{
							StartIndex = startIndex,
							Length = length,
							RepeatCount = 2
						};
					}

					break;
				}
			}

			return bestRun;
		}

		private static int IndexOfAscii(byte[] data, string text, int startOffset = 0)
		{
			var pattern = Encoding.ASCII.GetBytes(text);

			for (var i = startOffset; i <= data.Length - pattern.Length; i++)
			{
				if (data
				    .AsSpan(i, pattern.Length)
				    .SequenceEqual(pattern))
					return i;
			}

			return -1;
		}

		private static bool LooksLikeAccessWindowEntryStart(byte[] data, int offset)
		{
			const int minimumPrefixLength = 4 + 1 + 4 + 4 + 2;

			if (offset < 0 || offset + minimumPrefixLength > data.Length)
				return false;

			var playfieldId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
			var sectionOrTier = data[offset + 4];
			var kind = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 5, 4));
			var nameLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 13, 2));

			if (playfieldId is 0 or > 200_000)
				return false;

			if (sectionOrTier > 10)
				return false;

			if (kind > 10)
				return false;

			var nameOffset = offset + 15;
			var nameEndOffset = nameOffset + nameLength;

			if (nameEndOffset >= data.Length)
				return false;

			if (data[nameEndOffset] != 0)
				return false;

			return data
				.AsSpan(nameOffset, nameLength)
				.StartsWith("PFAccessWindow_"u8);
		}

		private static PfAccessWindowChild ReadAccessWindowChild(FaolanStream reader, byte[] data, byte childMode)
		{
			var variant = reader.ReadByte();

			if (childMode == 1)
			{
				if (variant == 0)
				{
					var id = reader.ReadUInt32();
					var unknown = reader.ReadUInt32();

					return new PfAccessWindowChild
					{
						Variant = variant,
						Id = id,
						Unknown = unknown,
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
					Details =
					[
						ReadAccessWindowChildDetail(reader, variant)
					]
				};
			}

			if (childMode == 2)
			{
				var id = reader.ReadUInt32();
				var unknown = reader.ReadUInt32();
				var detailsMode = reader.ReadByte();

				if (detailsMode == 0)
				{
					return new PfAccessWindowChild
					{
						Variant = variant,
						Id = id,
						Unknown = unknown,
						DetailsMode = detailsMode,
						Details = []
					};
				}

				return new PfAccessWindowChild
				{
					Variant = variant,
					Id = id,
					Unknown = unknown,
					DetailsMode = detailsMode,
					Details =
					[
						ReadAccessWindowChildDetail(reader, detailsMode)
					]
				};
			}

			if (childMode == 3)
			{
				var id = reader.ReadUInt32();
				var unknown = reader.ReadUInt32();

				var details = new List<PfAccessWindowChildDetail>();

				// Mode 3 has been observed on playfield 6300:
				// PFAccessWindow_ChosainProvince and PFAccessWindow_Shaulun.
				// No explicit detail count has been observed yet, so read detail
				// blocks until the next top-level access-window entry starts.
				while (!LooksLikeAccessWindowEntryStart(data, checked((int)reader.Position)))
				{
					if (Remaining(reader) <= 0)
						throw new InvalidOperationException("Unexpected end while reading childMode 3 details.");

					var detailsMode = reader.ReadByte();

					if (detailsMode == 0)
						throw new InvalidOperationException($"Unexpected childMode 3 detail mode 0 at offset 0x{reader.Position - 1:X8}.");

					details.Add(ReadAccessWindowChildDetail(reader, detailsMode));
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

			throw new InvalidOperationException($"Unsupported child mode {childMode} at offset 0x{reader.Position:X8}.");
		}

		private static PfAccessWindowChildDetail ReadAccessWindowChildDetail(FaolanStream reader, byte detailsMode)
		{
			var detailUnknown = reader.ReadByte();

			// These are usually readable strings like "M2W", but some values use bytes
			// above 0x7E, for example M2 0x84. Do not validate as strict ASCII here.
			var shortCode = ReadNullTerminatedLatin1String(reader);

			var displayName = ReadUInt8LengthPrefixedNullTerminatedLatin1String(reader);

			// No NUL here. In many records, the next 0 byte belongs to the next
			// playfield id, not to this string.
			var parentName = ReadUInt8LengthPrefixedLatin1String(reader);

			return new PfAccessWindowChildDetail
			{
				DetailsMode = detailsMode,
				DetailUnknown = detailUnknown,
				ShortCode = shortCode,
				DisplayName = displayName,
				ParentName = parentName
			};
		}

		private static string ReadNullTerminatedLatin1String(FaolanStream reader)
		{
			var bytes = new List<byte>();

			while (Remaining(reader) > 0)
			{
				var value = reader.ReadByte();

				if (value == 0)
					return Encoding.Latin1.GetString([.. bytes]);

				bytes.Add(value);
			}

			throw new InvalidOperationException($"Unexpected end of data while reading NUL-terminated string at offset 0x{reader.Position:X8}.");
		}

		private static string ReadUInt8LengthPrefixedLatin1String(FaolanStream reader)
		{
			var length = reader.ReadByte();
			var bytes = reader.ReadArray(length);

			return Encoding.Latin1.GetString(bytes);
		}

		private static string ReadUInt8LengthPrefixedNullTerminatedLatin1String(FaolanStream reader)
		{
			var text = ReadUInt8LengthPrefixedLatin1String(reader);
			ExpectByte(reader, 0);

			return text;
		}

		private static uint Remaining(FaolanStream stream)
		{
			return stream.Length - stream.Position;
		}

		private static TailsPostAccessLayout? TryBuildPostAccessLayout(List<uint> values)
		{
			var repeatedRun = FindLargestAdjacentRepeatedRun(values);

			if (repeatedRun is null)
				return null;

			var prefix = CopyRange(values, 0, repeatedRun.StartIndex);
			var repeatedValues = CopyRange(values, repeatedRun.StartIndex, repeatedRun.Length);
			var footerStartIndex = repeatedRun.StartIndex + repeatedRun.Length * repeatedRun.RepeatCount;
			var footer = CopyRange(values, footerStartIndex, values.Count - footerStartIndex);

			return new TailsPostAccessLayout
			{
				PrefixValues = prefix,
				RepeatedValues = repeatedValues,
				RepeatCount = repeatedRun.RepeatCount,
				FooterValues = footer
			};
		}

		private static bool TryReadAccessWindowEntry(FaolanStream reader, byte[] data, out PfAccessWindowEntry entry)
		{
			entry = null!;

			var startOffset = checked((int)reader.Position);

			if (!LooksLikeAccessWindowEntryStart(data, startOffset))
				return false;

			try
			{
				var playfieldId = reader.ReadUInt32();
				var sectionOrTier = reader.ReadByte();
				var kind = reader.ReadUInt32();
				var unknown = reader.ReadUInt32();
				var internalName = reader.ReadString();

				ExpectByte(reader, 0);

				if (!internalName.StartsWith("PFAccessWindow_", StringComparison.Ordinal))
				{
					reader.Position = checked((uint)startOffset);
					return false;
				}

				var unknownBeforeChildMode = reader.ReadUInt16();
				var childMode = reader.ReadByte();

				if (childMode > 3)
				{
					reader.Position = checked((uint)startOffset);
					return false;
				}

				PfAccessWindowChild? child = null;

				if (childMode != 0)
					child = ReadAccessWindowChild(reader, data, childMode);

				entry = new PfAccessWindowEntry
				{
					Offset = startOffset,
					PlayfieldId = playfieldId,
					SectionOrTier = sectionOrTier,
					Kind = kind,
					Unknown = unknown,
					InternalName = internalName,
					UnknownBeforeChildMode = unknownBeforeChildMode,
					ChildMode = childMode,
					Child = child
				};

				return true;
			}
			catch
			{
				reader.Position = checked((uint)startOffset);
				return false;
			}
		}

		private static TailsPostAccessBlock? TryReadPostAccessBlock(byte[] data)
		{
			if (data.Length < 12)
				return null;

			var marker0 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
			var marker1 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
			var blockType = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));

			// Observed post-access block starts with:
			// 0x000003F1, 0x000003F1, 0x000046F2
			if (marker0 != 1009 || marker1 != 1009)
				return null;

			var payloadLength = data.Length - 12;
			var trailerLength = -1;

			// Treat this as a uint stream first, not as key/value pairs. The previous
			// pair view was useful diagnostically, but the output shows repeated numeric
			// ranges that do not behave like a flat dictionary.
			for (var candidateTrailerLength = 0; candidateTrailerLength <= 16; candidateTrailerLength++)
			{
				if (payloadLength >= candidateTrailerLength && (payloadLength - candidateTrailerLength) % 4 == 0)
				{
					trailerLength = candidateTrailerLength;
					break;
				}
			}

			if (trailerLength < 0)
				return null;

			var valueBytesLength = payloadLength - trailerLength;
			var valueCount = valueBytesLength / 4;
			var values = new List<uint>(valueCount);

			var offset = 12;

			for (var index = 0; index < valueCount; index++)
			{
				values.Add(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
				offset += 4;
			}

			var trailer = data[offset..];

			return new TailsPostAccessBlock
			{
				Marker0 = marker0,
				Marker1 = marker1,
				BlockType = blockType,
				Values = values,
				LargestAdjacentRepeatedRun = FindLargestAdjacentRepeatedRun(values),
				Layout = TryBuildPostAccessLayout(values),
				Trailer = trailer
			};
		}

		private static bool UInt32RangesEqual(List<uint> values, int leftStart, int rightStart, int length)
		{
			for (var i = 0; i < length; i++)
			{
				if (values[leftStart + i] != values[rightStart + i])
					return false;
			}

			return true;
		}

		private static void WriteAccessWindowChild(FaolanStream writer, byte childMode, PfAccessWindowChild child)
		{
			writer.WriteByte(child.Variant);

			if (childMode == 1)
			{
				if (child.Variant == 0)
				{
					writer.WriteUInt32(child.Id ?? throw new InvalidOperationException("childMode 1 variant 0 requires Id."));
					writer.WriteUInt32(child.Unknown ?? throw new InvalidOperationException("childMode 1 variant 0 requires Unknown."));
					return;
				}

				WriteSingleAccessWindowChildDetail(writer, child, child.Variant);
				return;
			}

			if (childMode == 2)
			{
				writer.WriteUInt32(child.Id ?? throw new InvalidOperationException("childMode 2 requires Id."));
				writer.WriteUInt32(child.Unknown ?? throw new InvalidOperationException("childMode 2 requires Unknown."));

				var detailsMode = child.DetailsMode ?? 0;
				writer.WriteByte(detailsMode);

				if (detailsMode != 0)
					WriteSingleAccessWindowChildDetail(writer, child, detailsMode);

				return;
			}

			if (childMode == 3)
			{
				writer.WriteUInt32(child.Id ?? throw new InvalidOperationException("childMode 3 requires Id."));
				writer.WriteUInt32(child.Unknown ?? throw new InvalidOperationException("childMode 3 requires Unknown."));

				foreach (var detail in child.Details)
				{
					writer.WriteByte(detail.DetailsMode);
					WriteAccessWindowChildDetailBody(writer, detail);
				}

				return;
			}

			throw new InvalidOperationException($"Unsupported child mode {childMode}.");
		}

		private static void WriteAccessWindowChildDetailBody(FaolanStream writer, PfAccessWindowChildDetail detail)
		{
			writer.WriteByte(detail.DetailUnknown);
			WriteNullTerminatedLatin1String(writer, detail.ShortCode);
			WriteUInt8LengthPrefixedNullTerminatedLatin1String(writer, detail.DisplayName);
			WriteUInt8LengthPrefixedLatin1String(writer, detail.ParentName);
		}

		private static void WriteAccessWindowEntry(FaolanStream writer, PfAccessWindowEntry entry)
		{
			writer.WriteUInt32(entry.PlayfieldId);
			writer.WriteByte(entry.SectionOrTier);
			writer.WriteUInt32(entry.Kind);
			writer.WriteUInt32(entry.Unknown);
			writer.WriteString(entry.InternalName);
			writer.WriteByte(0);
			writer.WriteUInt16(entry.UnknownBeforeChildMode);
			writer.WriteByte(entry.ChildMode);

			if (entry.ChildMode != 0)
			{
				if (entry.Child is null)
					throw new InvalidOperationException($"Access-window entry at 0x{entry.Offset:X8} has childMode {entry.ChildMode}, but Child is null.");

				WriteAccessWindowChild(writer, entry.ChildMode, entry.Child);
			}
		}

		private static void WriteNullTerminatedLatin1String(FaolanStream writer, string value)
		{
			writer.WriteArray(Encoding.Latin1.GetBytes(value));
			writer.WriteByte(0);
		}

		private static void WritePostAccessBlock(FaolanStream writer, TailsPostAccessBlock block)
		{
			writer.WriteUInt32(block.Marker0);
			writer.WriteUInt32(block.Marker1);
			writer.WriteUInt32(block.BlockType);

			foreach (var value in block.Values)
				writer.WriteUInt32(value);

			writer.WriteArray(block.Trailer);
		}

		private static void WriteSingleAccessWindowChildDetail(FaolanStream writer, PfAccessWindowChild child, byte expectedDetailsMode)
		{
			if (child.Details.Count != 1)
				throw new InvalidOperationException($"Expected exactly one detail for detailsMode {expectedDetailsMode}, got {child.Details.Count}.");

			var detail = child.Details[0];

			if (detail.DetailsMode != expectedDetailsMode)
				throw new InvalidOperationException($"Detail mode mismatch. Expected {expectedDetailsMode}, got {detail.DetailsMode}.");

			WriteAccessWindowChildDetailBody(writer, detail);
		}

		private static void WriteUInt8LengthPrefixedLatin1String(FaolanStream writer, string value)
		{
			var bytes = Encoding.Latin1.GetBytes(value);
			writer.WriteByte(checked((byte)bytes.Length));
			writer.WriteArray(bytes);
		}

		private static void WriteUInt8LengthPrefixedNullTerminatedLatin1String(FaolanStream writer, string value)
		{
			WriteUInt8LengthPrefixedLatin1String(writer, value);
			writer.WriteByte(0);
		}
	}

	public sealed class PfAccessWindowEntry
	{
		public byte ChildMode { get; init; }

		public byte SectionOrTier { get; init; }
		public int Offset { get; init; }

		public PfAccessWindowChild? Child { get; init; }

		public string InternalName { get; init; }

		public uint Kind { get; init; }

		public uint PlayfieldId { get; init; }

		public uint Unknown { get; init; }

		public ushort UnknownBeforeChildMode { get; init; }

		public override string ToString()
		{
			var child = Child is null
				? ""
				: $" | child: {Child}";

			return $"0x{Offset:X8}: {PlayfieldId} {InternalName} section={SectionOrTier} kind={Kind} childMode={ChildMode}{child}";
		}
	}

	public sealed class PfAccessWindowChild
	{
		public bool HasDetails => Details.Count != 0;
		public byte Variant { get; init; }

		public byte? DetailsMode { get; init; }

		public List<PfAccessWindowChildDetail> Details { get; init; } = [];

		public uint? Id { get; init; }

		public uint? Unknown { get; init; }

		public override string ToString()
		{
			if (!HasDetails)
			{
				return Id is null
					? $"variant={Variant}"
					: $"variant={Variant}, id=0x{Id:X8} ({Id})";
			}

			var id = Id is null
				? ""
				: $"id=0x{Id:X8} ({Id}), ";

			if (Details.Count == 1)
				return $"variant={Variant}, {id}{Details[0]}";

			return $"variant={Variant}, {id}details={Details.Count}";
		}
	}

	public sealed class PfAccessWindowChildDetail
	{
		public byte DetailsMode { get; init; }

		public byte DetailUnknown { get; init; }

		public string DisplayName { get; init; }

		public string ParentName { get; init; }

		public string ShortCode { get; init; }

		public override string ToString()
		{
			return $"mode={DetailsMode}, code={ShortCode}, display={DisplayName}, parent={ParentName}";
		}
	}

	public sealed class TailsPostAccessBlock
	{
		private const int RepeatedBlockEndIndex = 182;
		private const int RepeatedBlockOffset = 148;
		private const int RepeatedBlockStartIndex = 35;

		public byte[] Trailer { get; init; } = [];

		public List<uint> Values { get; init; } = [];

		public TailsPostAccessLayout? Layout { get; init; }

		public TailsRepeatedUInt32Run? LargestAdjacentRepeatedRun { get; init; }

		public uint BlockType { get; init; }

		public uint CurrentHp
		{
			get => GetValue(CharacterValueIndex.CurrentHp) / 100;
			set
			{
				var scaledValue = value * 100;

				Values[(int)CharacterValueIndex.CurrentHpExtra] = scaledValue;
				SetValue(CharacterValueIndex.CurrentHp, scaledValue);
			}
		}

		public uint CurrentMana
		{
			get => GetValue(CharacterValueIndex.CurrentMana) / 100;
			set => SetValue(CharacterValueIndex.CurrentMana, value * 100);
		}

		public uint CurrentStamina
		{
			get => GetValue(CharacterValueIndex.CurrentStamina) / 100;
			set => SetValue(CharacterValueIndex.CurrentStamina, value * 100);
		}

		// Authors the WHOLE per-class vitals block (#4/#186): for HP/Mana/Stamina it sets the DISPLAYED max AND the
		// lower base/secondary the client reads into a parallel slot, each ×100, keeping the +148 repeated copies in
		// sync. The old per-stat setters (CurrentHp/Mana/Stamina) touched only the displayed slot, leaving the base
		// slots - and the second mana copy [60] - at the captured Strie (Rogue) template's values; that internal
		// inconsistency is the suspected cause of #4 (a Necromancer showed ~0 mana because [60] stayed 0 while [64]
		// held 24600) and the in-client "gear vanished" snapshot reject. Verified slot map across four archetypes'
		// retail LOGIN snapshots (decode_statblock.py): HP displayed [23]/[54], base [21]/[56]; mana displayed
		// [60]=[64], secondary [62]; stamina displayed [68], secondary [66]. Pass CharacterMaxStats.SnapshotVitals().
		public void ApplyClassVitals(uint hp, uint hpBase, uint mana, uint manaBase, uint stamina, uint staminaBase)
		{
			// HP: [21]/[23] are pre-repeated-block standalone slots (like CurrentHpExtra, no +148 copy); [54]/[56] live
			// inside the repeated block, so SetValue also writes their [202]/[204] copies.
			Values[21] = hpBase * 100;
			Values[(int)CharacterValueIndex.CurrentHpExtra] = hp * 100; // [23]
			SetValue(CharacterValueIndex.CurrentHp, hp * 100);          // [54] (+[202])
			SetValue((CharacterValueIndex)56, hpBase * 100);            // [56] (+[204])

			// Mana: the displayed max lives in BOTH [60] and [64]; [62] is the ~6-lower secondary.
			SetValue((CharacterValueIndex)60, mana * 100);              // [60] (+[208])
			SetValue(CharacterValueIndex.ManaSecondary, manaBase * 100); // [62] (+[210])
			SetValue(CharacterValueIndex.CurrentMana, mana * 100);       // [64] (+[212])

			// Stamina: displayed [68], secondary [66].
			SetValue((CharacterValueIndex)66, staminaBase * 100);       // [66] (+[214])
			SetValue(CharacterValueIndex.CurrentStamina, stamina * 100); // [68] (+[216])
		}

		public uint CurrentXp
		{
			get => GetValue(CharacterValueIndex.CurrentXp) / 10;
			set => SetValue(CharacterValueIndex.CurrentXp, value * 10);
		}

		public uint Marker0 { get; init; }

		public uint Marker1 { get; init; }

		//public uint PvpXpTillNextLevel
		//{
		//	get => GetValue(CharacterValueIndex.PvpXpTillNextLevel);
		//	set => SetValue(CharacterValueIndex.PvpXpTillNextLevel, value);
		//}

		/*public uint PvpKillsOverall
		{
			get => GetValue(CharacterValueIndex.PvpKillsOverall);
			set => SetValue(CharacterValueIndex.PvpKillsOverall, value);
		}*/

		public uint PvpDeathsOverall
		{
			get => GetValue(CharacterValueIndex.PvpDeathsOverall);
			set => SetValue(CharacterValueIndex.PvpDeathsOverall, value);
		}

		public enum CharacterValueIndex : uint
		{
			// HP is special:
			// - Extra pre-block slot: 23
			// - Main repeated-block slot: 54
			// - Copy slot: 202
			CurrentHpExtra = 23,

			Unknown35 = 35,
			Unknown36 = 36,
			Unknown37 = 37,
			Unknown38 = 38,
			Unknown39 = 39,
			Unknown40 = 40,
			Unknown41 = 41,
			Unknown42 = 42,
			Unknown43 = 43,
			Unknown44 = 44, // PvpXpTillNextLevel?
			Unknown45 = 45,
			PvpXp = 46,
			Unknown47 = 47,
			Unknown48 = 48,
			Unknown49 = 49,
			Unknown50 = 50,
			Unknown51 = 51,
			Unknown52 = 52,
			Unknown53 = 53,
			CurrentHp = 54,
			Unknown55 = 55,
			Unknown56 = 56,
			Unknown57 = 57,
			Unknown58 = 58,
			Unknown59 = 59,
			Unknown60 = 60,
			Unknown61 = 61,

			// Index 62 is a SECONDARY mana value (~6 lower than displayed): retail Necro [62]=24000 vs displayed
			// 24600, Bear Shaman [62]=16500 vs displayed 17100. NOT the displayed max — see CurrentMana below.
			ManaSecondary = 62,
			Unknown63 = 63,
			// The displayed MAX mana (×100). Verified against retail LOGIN snapshots: Necro [64]=24600 (=246 mana,
			// matches the client sheet), Bear Shaman [64]=17100 (=171). Index 60 carries the same value; [62] is a
			// ~6-lower secondary. (The earlier "[64]=2276 / use 62" note came from an UNSETTLED create snapshot.)
			CurrentMana = 64,
			Unknown65 = 65,
			Unknown66 = 66,
			Unknown67 = 67,
			CurrentStamina = 68,
			Unknown69 = 69,
			Unknown70 = 70,
			Unknown71 = 71,
			Unknown72 = 72,
			Unknown73 = 73,
			Unknown74 = 74,
			Unknown75 = 75,
			Unknown76 = 76,
			Unknown77 = 77,
			Unknown78 = 78,
			Unknown79 = 79,
			CurrentXp = 80, // factor 10
			Unknown81 = 81,
			Unknown82 = 82,
			Unknown83 = 83,
			Unknown84 = 84,
			Unknown85 = 85,
			PvpLevel = 86,
			Unknown87 = 87,
			Unknown88 = 88,
			Unknown89 = 89,
			Unknown90 = 90,
			Unknown91 = 91,
			Unknown92 = 92,
			Unknown93 = 93,
			Unknown94 = 94,
			Unknown95 = 95,
			Unknown96 = 96,
			Unknown97 = 97,
			Unknown98 = 98,
			Unknown99 = 99,
			Unknown100 = 100,
			Unknown101 = 101,
			Unknown102 = 102,
			Unknown103 = 103,
			Unknown104 = 104,
			Unknown105 = 105,
			Unknown106 = 106,
			Unknown107 = 107,
			Unknown108 = 108,
			Unknown109 = 109,
			Unknown110 = 110,
			Unknown111 = 111,
			Unknown112 = 112,
			Unknown113 = 113,
			Unknown114 = 114,
			Unknown115 = 115,
			Unknown116 = 116,
			Unknown117 = 117,
			Unknown118 = 118,
			Unknown119 = 119,
			Unknown120 = 120,
			Unknown121 = 121,
			Unknown122 = 122,
			Unknown123 = 123,
			Unknown124 = 124,
			Unknown125 = 125,
			Unknown126 = 126,
			Unknown127 = 127,
			Unknown128 = 128,
			Unknown129 = 129,
			Unknown130 = 130,
			Unknown131 = 131,
			Unknown132 = 132,
			Unknown133 = 133,
			Unknown134 = 134,
			Unknown135 = 135,
			Unknown136 = 136,
			Unknown137 = 137,
			Unknown138 = 138,
			Unknown139 = 139,
			Unknown140 = 140,
			Unknown141 = 141,
			Unknown142 = 142,
			Unknown143 = 143,
			PvpMinigamesAbandonedSeason = 144,
			Unknown145 = 145,
			PvpMinigamesAbandonedOverall = 146,
			Unknown147 = 147,
			PvpSiegeLosesSeason = 148,
			Unknown149 = 149,
			PvpSiegeLosesOverall = 150,
			Unknown151 = 151,
			PvpSiegeWinsSeason = 152,
			Unknown153 = 153,
			PvpSiegeWinsOverall = 154,
			Unknown155 = 155,
			PvpCaptureTheSkullLossesSeason = 156,
			Unknown157 = 157,
			PvpCaptureTheSkullLossesOverall = 158,
			Unknown159 = 159,
			PvpCaptureTheSkullWinsSeason = 160,
			Unknown161 = 161,
			PvpCaptureTheSkullWinsOverall = 162,
			Unknown163 = 163,
			PvpAnnihilationLossesSeason = 164,
			Unknown165 = 165,
			PvpAnnihilationLossesOverall = 166,
			Unknown167 = 167,
			PvpAnnihilationWinsSeason = 168,
			Unknown169 = 169,
			PvpAnnihilationWinsOverall = 170,
			Unknown171 = 171,
			PvpFatalitiesSeason = 172,
			Unknown173 = 173,
			PvpFatalitiesOverall = 174,
			Unknown175 = 175,
			PvpDeathSeason = 176,
			Unknown177 = 177,
			PvpDeathsOverall = 178,
			Unknown179 = 179,
			PvpKillsSeason = 180, // factor 10
			Unknown181 = 181,
			Unknown182 = 182 // PvpKillsOverall ?
		}

		public uint GetValue(CharacterValueIndex index)
		{
			return Values[(int)index];
		}

		public void SetValue(CharacterValueIndex index, uint value)
		{
			Values[(int)index] = value;
			Values[GetRepeatedCopyIndex(index)] = value;
		}

		private static int GetRepeatedCopyIndex(CharacterValueIndex index)
		{
			var indexx = (int)index;

			if (indexx is < RepeatedBlockStartIndex or > RepeatedBlockEndIndex)
				throw new ArgumentOutOfRangeException(nameof(indexx), indexx, $"Index must be inside repeated block {RepeatedBlockStartIndex}..{RepeatedBlockEndIndex}.");

			return indexx + RepeatedBlockOffset;
		}
	}

	public sealed class TailsPostAccessLayout
	{
		public int RepeatCount { get; init; }

		public List<uint> FooterValues { get; init; } = [];
		public List<uint> PrefixValues { get; init; } = [];

		/// <summary>
		///     Repeated post-access uint payload.
		///     Strong observations from screenshots + parsed post-access data:
		///     - RepeatedValues[18] = 27, RepeatedValues[19] = current health scaled by 100.
		///     bigboy-1: 594900 -> 5949 health.
		///     bigboy-2: 9800 -> 98 health.
		///     bigboy-3 screenshot expects 8900 -> 89 health.
		///     bigboy-4 screenshot expects 594900 -> 5949 health.
		///     - RepeatedValues[24] = 506, RepeatedValues[25] = current mana scaled by 100.
		///     bigboy-1: 583000 -> 5830 mana.
		///     bigboy-2: 0 -> 0 mana.
		///     bigboy-3 screenshot expects 24600 -> 246 mana.
		///     bigboy-4 screenshot expects 583000 -> 5830 mana.
		///     - RepeatedValues[28] = 506, RepeatedValues[29] = another mana-like copy in some files,
		///     but not proven to be max mana. bigboy-2 and bigboy-3 show max mana separately in UI.
		///     bigboy-1: 583000 -> same as current mana.
		///     bigboy-2: 0 -> same as current mana.
		///     bigboy-3 max mana screenshot value is 346000, so search for that separately.
		///     bigboy-4: 583000 -> same as current mana.
		///     - RepeatedValues[30] = 505, RepeatedValues[31] = stamina-like value scaled by 100.
		///     bigboy-2 screenshot: 23000 -> 230, but UI shows 236 / 230, so this is likely max stamina.
		///     - RepeatedValues[32] = 504, RepeatedValues[33] = stamina-like value scaled by 100.
		///     bigboy-2 screenshot: 23600 -> 236, so this is likely current stamina.
		///     - RepeatedValues[48] = 54, RepeatedValues[49] = level.
		///     bigboy-1: 80.
		///     bigboy-2: 1.
		///     bigboy-3 screenshot expects 1.
		///     bigboy-4 screenshot expects 80.
		///     Keep these as comments for now; this block is still preserved as raw uints
		///     so write-back stays lossless while the exact stat table format is decoded.
		/// </summary>
		public List<uint> RepeatedValues { get; init; } = [];
	}

	public sealed class TailsRepeatedUInt32Run
	{
		public int Length { get; init; }

		public int RepeatCount { get; init; }

		public int SecondStartIndex => StartIndex + Length;
		public int StartIndex { get; init; }
	}
}
