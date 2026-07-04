using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.Core;
using Faolan.Core.Network.Packets;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	public static class InventorySnapshotInspector
	{
		private const uint TailOwnerMarker = 0x110099BD; // opcode

		public enum ContainerKind : uint
		{
			Equipped = 101,
			Bag = 104,
			Bank = 105, // personal bank storage - delivered on-demand via BankIIR (protocol_bank_storage), same codec
			Resource = 112,
			Quest = 113
		}

		// Deep-clones an item entry (field 4) via a protobuf round-trip, so it can be re-slotted and re-recorded
		// and added as a new entry without aliasing the original's nested messages.
		public static ProtoField CloneEntry(ProtoField entry)
		{
			return new ProtoField
			{
				FieldNumber = 4,
				WireType = 2,
				MessageValue = ProtoMessage.Parse(entry.MessageValue!.ToArray())
			};
		}

		public static void DumpContainerSummary(ProtoMessage? container)
		{
			foreach (var entry in GetContainerSummary(container))
				Console.WriteLine($"slot={entry.Slot}, " + $"itemId={entry.ItemId}, " + $"props=[{string.Join(", ", entry.Properties)}], " + $"f4={entry.TemplateA}, " + $"f5={entry.TemplateB}, " + $"f8=0x{entry.Archetype:X8}");
		}

		public static void DumpEntry(ProtoField entry)
		{
			var itemDetails = GetItemDetails(entry);
			if (itemDetails == null)
				return;

			Console.WriteLine($"Slot: {GetEntrySlot(entry)}");
			Console.WriteLine($"itemDetails raw = {Convert.ToHexString(itemDetails.ToArray())}");

			foreach (var field in itemDetails.Fields)
			{
				Console.WriteLine($"itemDetails.f{field.FieldNumber}, wire = {field.WireType}");

				if (field.WireType == 0)
				{
					Console.WriteLine($"  varint = {field.VarintValue}");
					continue;
				}

				var raw = field.ToArray();
				Console.WriteLine($"  raw[{raw.Length}] = {Convert.ToHexString(raw)}");

				if (field.MessageValue == null)
					continue;

				Console.WriteLine("  as message:");

				foreach (var child in field.MessageValue.Fields)
				{
					Console.WriteLine($"    f{child.FieldNumber}, wire = {child.WireType}");

					if (child.WireType == 0)
						Console.WriteLine($"      varint = {child.VarintValue}");

					if (child.WireType == 5)
						Console.WriteLine($"      fixed32 = {child.Fixed32Value}");

					if (child.WireType == 1)
						Console.WriteLine($"      fixed64 = {child.Fixed64Value}");

					var childRaw = child.ToArray();
					Console.WriteLine($"      raw[{childRaw.Length}] = {Convert.ToHexString(childRaw)}");
				}
			}
		}

		public static void DumpTailRecords(IEnumerable<ITailRecord> records, int maxRecords = 10000, int maxXmlPreviewLength = 160, bool includeXml = true)
		{
			var index = 0;

			foreach (var record in records)
			{
				if (index >= maxRecords)
				{
					Console.WriteLine($"Dump stopped after {maxRecords} records.");
					return;
				}

				index++;

				switch (record)
				{
					case TailXmlRecord xml when includeXml:
					{
						var preview = xml.Value.Length > maxXmlPreviewLength
							? xml.Value[..maxXmlPreviewLength] + "..."
							: xml.Value;

						Console.WriteLine($"XML tail+0x{xml.Offset:X}: " + $"length={xml.Value.Length}, " + $"value={preview}");

						break;
					}

					case TailXmlRecord:
						break;

					case TailHashedValueRecord hashed:
					{
						Console.WriteLine($"HASHED tail+0x{hashed.Offset:X}: " + $"owner=0x{hashed.Owner:X8}, " + $"context=0x{hashed.Context:X8}, " + $"value={hashed.Value} / 0x{hashed.Value:X8}, " +
						                  $"key={Convert.ToHexString(hashed.Key)}");

						break;
					}

					case TailStructRecord structured:
					{
						Console.WriteLine($"STRUCT tail+0x{structured.Offset:X}: " + $"owner=0x{structured.Owner:X8}, " + $"context=0x{structured.Context:X8}, " + $"descriptor=0x{structured.Descriptor:X8}, " +
						                  $"fields=[{structured.Field0}, {structured.Field1}, {structured.Field2}, {structured.Field3}, {structured.Field4}]");

						break;
					}
				}
			}
		}

		public static IEnumerable<ProtoField> FindEntriesByTemplateId(ProtoMessage? container, uint templateId)
		{
			return GetContainerEntries(container).Where(x => GetEntryTemplateId(x) == templateId);
		}

		public static IEnumerable<TailHashedValueRecord> FindHashedTailValues(IEnumerable<ITailRecord> records, params uint[] values)
		{
			var set = values.ToHashSet();

			return records.OfType<TailHashedValueRecord>().Where(x => set.Contains(x.Value));
		}

		public static IEnumerable<TailStructRecord> FindStructTailValues(IEnumerable<ITailRecord> records, params uint[] values)
		{
			var set = values.ToHashSet();

			return records.OfType<TailStructRecord>().Where(x => set.Contains(x.Field0) || set.Contains(x.Field1) || set.Contains(x.Field2) || set.Contains(x.Field3) || set.Contains(x.Field4));
		}

		public static ProtoMessage? GetContainer(ProtoMessage root, ContainerKind kind)
		{
			return root.Fields.Where(x => x.FieldNumber == 6 && x.MessageValue != null).Select(x => x.MessageValue!).FirstOrDefault(x => GetContainerKind(x) == (ulong)kind);
		}

		public static IEnumerable<ProtoField> GetContainerEntries(ProtoMessage? container)
		{
			if (container == null)
				return [];

			return container.Fields.Where(x => x.FieldNumber == 4 && x.MessageValue != null);
		}

		public static ulong? GetContainerKind(ProtoMessage container)
		{
			var wrapper = container.Fields.FirstOrDefault(x => x.FieldNumber == 1 && x.MessageValue != null);

			return wrapper?.MessageValue?.Fields.FirstOrDefault(x => x.FieldNumber == 1 && x.WireType == 0)?.VarintValue;
		}

		public static IReadOnlyList<InventoryEntrySummary> GetContainerSummary(ProtoMessage? container)
		{
			return GetContainerEntries(container).Select(ToEntrySummary).ToArray();
		}

		public static uint? GetEntryArchetype(ProtoField entry)
		{
			var tail = GetEntryTail(entry);

			return tail?.Fields.FirstOrDefault(x => x.FieldNumber == 8 && x.WireType == 5)?.Fixed32Value;
		}

		public static ProtoField? GetEntryBySlot(ProtoMessage? container, ulong slot)
		{
			return GetContainerEntries(container).FirstOrDefault(x => GetEntrySlot(x) == slot);
		}

		public static ulong? GetEntryCount(ProtoField entry)
		{
			var itemDetails = GetItemDetails(entry);
			if (itemDetails == null)
				return null;

			var countMessage = itemDetails.Fields.Where(x => x.FieldNumber == 2 && x.MessageValue != null).Select(x => x.MessageValue!).FirstOrDefault(x => IsTinyMessageKind(x, 26));

			return countMessage?.Fields.FirstOrDefault(x => x.FieldNumber == 2 && x.WireType == 0)?.VarintValue;
		}

		// The item's stable identity quad (Data1..Data4) = the four fixed32s of the tail's first identity payload
		// (tail field 1). The same value appears in `p_items.Data1..4`; harvesting it from captures makes any item a
		// player has held buildable without a p_items row.
		public static (uint Data1, uint Data2, uint Data3, uint Data4)? GetEntryIdentityQuad(ProtoField entry)
		{
			var tail = GetEntryTail(entry);
			var payload = tail?.Fields.FirstOrDefault(x => x.FieldNumber == 1 && x.MessageValue != null)?.MessageValue;
			if (payload == null)
				return null;

			var f = payload.Fields.Where(x => x.WireType == 5).Select(x => x.Fixed32Value).ToArray();
			return f.Length < 4
				? null
				: (f[0], f[1], f[2], f[3]);
		}

		public static ulong? GetEntryItemId(ProtoField entry)
		{
			var itemDetails = GetItemDetails(entry);

			return itemDetails?.Fields.FirstOrDefault(x => x.FieldNumber == 1 && x.WireType == 0)?.VarintValue;
		}

		// Property 23 = the item's local inventory record id (present on every item).
		public static ulong? GetEntryRecordId(ProtoField entry)
		{
			var details = GetItemDetails(entry);
			var record = details?.Fields.Where(x => x.FieldNumber == 2 && x.MessageValue != null).Select(x => x.MessageValue!).FirstOrDefault(x => IsTinyMessageKind(x, 23));
			return record?.Fields.FirstOrDefault(x => x.FieldNumber == 2 && x.WireType == 0)?.VarintValue;
		}

		public static ulong? GetEntrySlot(ProtoField entry)
		{
			return entry.MessageValue?.Fields.FirstOrDefault(x => x.FieldNumber == 1 && x.WireType == 0)?.VarintValue;
		}

		public static uint? GetEntryTemplateId(ProtoField entry)
		{
			var tail = GetEntryTail(entry);

			return tail?.Fields.FirstOrDefault(x => x.FieldNumber == 4 && x.WireType == 5)?.Fixed32Value;
		}

		// Offsets the item's "stable identity" tail GUID (tail fields 1/2/3 are three identical 20-byte payloads,
		// each four fixed32s) by a delta, so a cloned entry has a distinct per-instance identity, not just a
		// distinct itemId/record-id. Bumps the first fixed32 of all three payloads by the same amount.
		public static void OffsetEntryIdentity(ProtoField entry, uint delta)
		{
			var tail = GetEntryTail(entry);
			if (tail == null)
				return;

			foreach (var payload in tail.Fields.Where(x => x.FieldNumber is 1 or 2 or 3 && x.MessageValue != null).Select(x => x.MessageValue!))
			{
				var first = payload.Fields.FirstOrDefault(x => x.WireType == 5);
				if (first != null)
					first.Fixed32Value += delta;
			}
		}

		public static InventoryPacketParts ParseInventoryPacketBody(FaolanStream bodyStream)
		{
			var bodyStart = bodyStream.Position;

			var inventoryRootOffset = FindInventoryRootOffset(bodyStream);
			var rootEndOffset = FindInventoryRootEndOffset(bodyStream, inventoryRootOffset);

			bodyStream.Position = bodyStart;
			var prefix = ReadExact(bodyStream, checked((int)(inventoryRootOffset - bodyStart)));

			bodyStream.Position = (uint)inventoryRootOffset;
			var rootBytes = ReadExact(bodyStream, checked((int)(rootEndOffset - inventoryRootOffset)));

			bodyStream.Position = (uint)rootEndOffset;
			var tails = ReadExact(bodyStream, checked((int)(bodyStream.Length - rootEndOffset)));

			using var rootStream = new MemoryStream(rootBytes, false);
			var root = ProtoMessage.Parse(rootStream);

			return new InventoryPacketParts(prefix, root, SnapshotTails.Deserialize(tails));
		}

		// Parses ONLY the inventory root protobuf (skips the tails). The tails of a high-level caster's snapshot carry
		// the whole spellbook/pet/quest data and can trip SnapshotTails.Deserialize; callers that only need the
		// inventory (the importer's carried-inventory pass) must not lose the root to a tails-parse failure.
		public static ProtoMessage ParseInventoryRoot(FaolanStream bodyStream)
		{
			var inventoryRootOffset = FindInventoryRootOffset(bodyStream);
			var rootEndOffset = FindInventoryRootEndOffset(bodyStream, inventoryRootOffset);

			bodyStream.Position = (uint)inventoryRootOffset;
			var rootBytes = ReadExact(bodyStream, checked((int)(rootEndOffset - inventoryRootOffset)));

			using var rootStream = new MemoryStream(rootBytes, false);
			return ProtoMessage.Parse(rootStream);
		}

		public static TailScanResult ReadTailRecords(FaolanStream tailsStream)
		{
			var records = new List<ITailRecord>();

			while (tailsStream.Position + 2 <= tailsStream.Length)
			{
				var recordStart = tailsStream.Position;

				if (TryReadTailXmlRecord(tailsStream, recordStart, out var xmlRecord))
				{
					records.Add(xmlRecord);
					continue;
				}

				if (tailsStream.Position + 32 <= tailsStream.Length)
				{
					var owner = tailsStream.ReadUInt32();

					if (owner == TailOwnerMarker)
					{
						var context = tailsStream.ReadUInt32();

						if (TryReadTailHashedValueRecord(tailsStream, recordStart, owner, context, out var hashedRecord))
						{
							records.Add(hashedRecord);
							continue;
						}

						if (TryReadTailStructRecord(tailsStream, recordStart, owner, context, out var structRecord))
						{
							records.Add(structRecord);
							continue;
						}
					}
				}

				// XML strings can be unaligned, so this scanner advances byte-by-byte.
				// TryReadTailXmlRecord is deliberately cheap before it decodes UTF-8.
				tailsStream.Position = recordStart + 1;
			}

			var finalPosition = tailsStream.Position;
			var remaining = tailsStream.Position < tailsStream.Length
				? tailsStream.ReadRemainingArray()
				: [];

			return new TailScanResult(records, finalPosition, remaining);
		}

		public static bool SetEntryCount(ProtoField entry, ulong count, bool addMissingCountProperty = false)
		{
			var itemDetails = GetItemDetails(entry);
			if (itemDetails == null)
				return false;

			var countMessageField = itemDetails.Fields.Where(x => x.FieldNumber == 2 && x.MessageValue != null).FirstOrDefault(x => IsTinyMessageKind(x.MessageValue!, 26));

			if (countMessageField != null)
			{
				var countField = countMessageField.MessageValue!.Fields.FirstOrDefault(x => x.FieldNumber == 2 && x.WireType == 0);

				if (countField == null)
					return false;

				countField.VarintValue = count;
				return true;
			}

			// Quantity 1 is often represented by omitting property 26.
			if (count == 1)
				return true;

			// Be careful: some client states do not like newly added item properties.
			if (!addMissingCountProperty)
				return false;

			var countMessage = new ProtoMessage();

			countMessage.Fields.Add(new ProtoField
			{
				FieldNumber = 1,
				WireType = 0,
				VarintValue = 26
			});

			countMessage.Fields.Add(new ProtoField
			{
				FieldNumber = 2,
				WireType = 0,
				VarintValue = count
			});

			itemDetails.Fields.Add(new ProtoField
			{
				FieldNumber = 2,
				WireType = 2,
				MessageValue = countMessage
			});

			return true;
		}

		public static void SetEntryItemId(ProtoField entry, ulong itemId)
		{
			var details = GetItemDetails(entry);
			var field = details?.Fields.FirstOrDefault(x => x.FieldNumber == 1 && x.WireType == 0);
			if (field != null)
				field.VarintValue = itemId;
		}

		public static void SetEntryRecordId(ProtoField entry, ulong recordId)
		{
			var details = GetItemDetails(entry);
			var record = details?.Fields.Where(x => x.FieldNumber == 2 && x.MessageValue != null).Select(x => x.MessageValue!).FirstOrDefault(x => IsTinyMessageKind(x, 23));
			var field = record?.Fields.FirstOrDefault(x => x.FieldNumber == 2 && x.WireType == 0);
			if (field != null)
				field.VarintValue = recordId;
		}

		public static void SetEntrySlot(ProtoField entry, ulong slot)
		{
			var slotField = entry.MessageValue?.Fields.FirstOrDefault(x => x.FieldNumber == 1 && x.WireType == 0);
			if (slotField != null)
				slotField.VarintValue = slot;
		}

		public static InventoryEntrySummary ToEntrySummary(ProtoField entry)
		{
			var itemDetails = GetItemDetails(entry);

			var properties = itemDetails
				?.Fields
				.Where(x => x.FieldNumber == 2 && x.MessageValue != null)
				.Select(x =>
				{
					var message = x.MessageValue!;

					var kind = message.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 0)?.VarintValue;

					var value = message.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarintValue;

					return $"{kind}:{value}";
				})
				.ToArray() ?? [];

			var tail = GetEntryTail(entry);

			var templateA = tail?.Fields.FirstOrDefault(x => x.FieldNumber == 4 && x.WireType == 5)?.Fixed32Value;

			var templateB = tail?.Fields.FirstOrDefault(x => x.FieldNumber == 5 && x.WireType == 5)?.Fixed32Value;

			var archetype = tail?.Fields.FirstOrDefault(x => x.FieldNumber == 8 && x.WireType == 5)?.Fixed32Value;

			return new InventoryEntrySummary(GetEntrySlot(entry), GetEntryItemId(entry), properties, templateA, templateB, archetype);
		}

		private static long FindInventoryRootEndOffset(FaolanStream bodyStream, long inventoryRootOffset)
		{
			var originalPosition = bodyStream.Position;

			try
			{
				bodyStream.Position = (uint)inventoryRootOffset;

				_ = ProtoMessage.Parse(bodyStream, 4);

				TryConsumeRootVarintField(bodyStream, 7);
				TryConsumeRootVarintField(bodyStream, 8);

				return bodyStream.Position;
			}
			finally
			{
				bodyStream.Position = originalPosition;
			}
		}

		private static long FindInventoryRootOffset(FaolanStream bodyStream)
		{
			var originalPosition = bodyStream.Position;

			try
			{
				// Inventory containers follow a variable-length quest-journal prefix in the root; a high-quest char
				// (NecroL80, 43 quests) pushes them to ~offset 400 - past the old 128-byte window, which silently dropped
				// her carried inventory. 8192 has headroom for 1000+ quests; the scan fails fast on non-container offsets.
				var scanEnd = Math.Min(bodyStream.Length, originalPosition + 8192);

				for (var offset = originalPosition; offset < scanEnd; offset++)
				{
					if (LooksLikeInventoryRootAt(bodyStream, offset))
						return offset;
				}

				throw new InvalidOperationException("Could not find inventory root protobuf offset.");
			}
			finally
			{
				bodyStream.Position = originalPosition;
			}
		}

		private static ProtoMessage? GetEntryTail(ProtoField entry)
		{
			var itemDetails = GetItemDetails(entry);

			return itemDetails?.Fields.FirstOrDefault(x => x.FieldNumber == 3 && x.MessageValue != null)?.MessageValue;
		}

		private static ProtoMessage? GetItemDetails(ProtoField entry)
		{
			return entry.MessageValue?.Fields.FirstOrDefault(x => x.FieldNumber == 2 && x.MessageValue != null)?.MessageValue;
		}

		private static bool IsTinyMessageKind(ProtoMessage message, ulong kind)
		{
			return message.Fields.FirstOrDefault(x => x.FieldNumber == 1 && x.WireType == 0)?.VarintValue == kind;
		}

		private static bool LooksLikeInventoryRootAt(FaolanStream bodyStream, long offset)
		{
			var originalPosition = bodyStream.Position;

			try
			{
				bodyStream.Position = (uint)offset;

				var expectedKinds = new[]
				{
					(ulong)ContainerKind.Equipped,
					(ulong)ContainerKind.Bag,
					(ulong)ContainerKind.Resource,
					(ulong)ContainerKind.Quest
				};

				foreach (var expectedKind in expectedKinds)
				{
					var oneField = ProtoMessage.Parse(bodyStream, 1);

					if (oneField.Fields.Count != 1)
						return false;

					var field = oneField.Fields[0];

					if (field.FieldNumber != 6 || field.MessageValue == null)
						return false;

					if (GetContainerKind(field.MessageValue) != expectedKind)
						return false;
				}

				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				bodyStream.Position = originalPosition;
			}
		}

		private static bool LooksLikeRealTailXmlString(string text)
		{
			var trimmed = text.Trim();

			if (trimmed.Length < 8)
				return false;

			if (!trimmed.StartsWith("<remoteformat ", StringComparison.Ordinal) && !trimmed.StartsWith("<localized ", StringComparison.Ordinal) && !trimmed.StartsWith("<root", StringComparison.Ordinal) &&
			    !trimmed.StartsWith("<?xml", StringComparison.Ordinal))
				return false;

			if (!trimmed.EndsWith(">", StringComparison.Ordinal))
				return false;

			var printable = 0;

			foreach (var c in text)
			{
				if (!char.IsControl(c) || c is '\r' or '\n' or '\t')
					printable++;
			}

			return (double)printable / text.Length >= 0.98;
		}

		private static byte[] ReadExact(FaolanStream stream, int length)
		{
			if (length < 0)
				throw new InvalidOperationException("Cannot read a negative number of bytes.");

			if (stream.Position + length > stream.Length)
				throw new InvalidOperationException($"Attempted to read {length} bytes beyond stream bounds.");

			var bytes = new byte[length];
			var read = stream.Read(bytes, 0, length);

			if (read != length)
				throw new InvalidOperationException("Unexpected end of stream.");

			return bytes;
		}

		private static bool TryConsumeRootVarintField(FaolanStream stream, int fieldNumber)
		{
			var start = stream.Position;

			try
			{
				var message = ProtoMessage.Parse(stream, 1);

				if (message.Fields.Count != 1)
				{
					stream.Position = start;
					return false;
				}

				var field = message.Fields[0];

				if (field.FieldNumber != fieldNumber || field.WireType != 0)
				{
					stream.Position = start;
					return false;
				}

				return true;
			}
			catch
			{
				stream.Position = start;
				return false;
			}
		}

		private static bool TryReadTailHashedValueRecord(FaolanStream tailsStream, uint recordStart, uint owner, uint context, out TailHashedValueRecord record)
		{
			record = null!;

			if (recordStart + 60 > tailsStream.Length)
			{
				tailsStream.Position = recordStart + 4;
				return false;
			}

			tailsStream.Position = recordStart + 8;

			var keyA = tailsStream.ReadArray(24);
			var keyB = tailsStream.ReadArray(24);

			if (!keyA.SequenceEqual(keyB))
			{
				tailsStream.Position = recordStart + 8;
				return false;
			}

			var value = tailsStream.ReadUInt32();

			record = new TailHashedValueRecord(recordStart, owner, context, keyA, value);

			return true;
		}

		private static bool TryReadTailStructRecord(FaolanStream tailsStream, uint recordStart, uint owner, uint context, out TailStructRecord record)
		{
			record = null!;

			if (recordStart + 32 > tailsStream.Length)
			{
				tailsStream.Position = recordStart + 4;
				return false;
			}

			tailsStream.Position = recordStart + 8;

			var descriptor = tailsStream.ReadUInt32();
			var field0 = tailsStream.ReadUInt32();
			var field1 = tailsStream.ReadUInt32();
			var field2 = tailsStream.ReadUInt32();
			var field3 = tailsStream.ReadUInt32();
			var field4 = tailsStream.ReadUInt32();

			record = new TailStructRecord(recordStart, owner, context, descriptor, field0, field1, field2, field3, field4);

			return true;
		}

		private static bool TryReadTailXmlRecord(FaolanStream tailsStream, uint recordStart, out TailXmlRecord record)
		{
			record = null!;

			if (recordStart + 3 > tailsStream.Length)
				return false;

			var originalPosition = tailsStream.Position;

			try
			{
				tailsStream.Position = recordStart;

				var length = tailsStream.ReadUInt16();

				if (length < 8 || length > 64_000)
					return false;

				if (recordStart + 2 + length > tailsStream.Length)
					return false;

				// Fast rejection: length-prefixed XML should start immediately with '<'.
				var firstStringByte = tailsStream.ReadByte();
				if (firstStringByte != '<')
					return false;

				tailsStream.Position = recordStart + 2;
				var bytes = tailsStream.ReadArray(length);

				string text;
				try
				{
					text = new UTF8Encoding(false, true).GetString(bytes);
				}
				catch
				{
					return false;
				}

				if (!LooksLikeRealTailXmlString(text))
					return false;

				record = new TailXmlRecord(recordStart, text);
				return true;
			}
			finally
			{
				if (record == null)
					tailsStream.Position = originalPosition;
			}
		}

		public sealed record InventoryPacketParts(byte[] Prefix, ProtoMessage Root, SnapshotTails Tails);

		public sealed record InventoryEntrySummary(ulong? Slot, ulong? ItemId, string[] Properties, uint? TemplateA, uint? TemplateB, uint? Archetype);

		public interface ITailRecord
		{
			uint Offset { get; }
		}

		public sealed record TailXmlRecord(uint Offset, string Value) : ITailRecord;

		public sealed record TailHashedValueRecord(uint Offset, uint Owner, uint Context, byte[] Key, uint Value) : ITailRecord;

		public sealed record TailStructRecord(uint Offset, uint Owner, uint Context, uint Descriptor, uint Field0, uint Field1, uint Field2, uint Field3, uint Field4) : ITailRecord;

		public sealed record TailScanResult(IReadOnlyList<ITailRecord> Records, uint FinalPosition, byte[] Remaining);
	}
}
