using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.World
{
	// A world object's stored COLUMNS <-> the typed messages inside its full-update body. NOT a codec (#530 rule 1):
	// nothing here owns a message - WorldObjectCodec owns the body, SimpleDynelIdentityCodec the identity message and
	// LootBagContentsCodec the container tail. This is the importer's lift and the builder's row mapping, which #533
	// is about.
	//
	// The lift is deliberately ALL-OR-NOTHING: a record that does not rebuild byte-exact from the columns leaves the
	// row UN-LIFTED rather than half-imported (#410), because a wrong display string would be fed to the client as-is.
	public static class WorldObjectColumns
	{
		// Copies a parsed identity message into a WorldObject row's columns (the importer path). The nullable columns
		// stay null when the field is absent, so ToMessage round-trips back to the same wire bytes.
		public static void ApplyColumns(WorldObject row, ProtoSimpleDynelIIR message)
		{
			row.Kdtree = message.Kdtree;
			row.Name = message.Name;
			WorldObjectDisplay.TryApply(row, message.DisplayName);
			row.CarsGroup = message.CarsGroup;
			row.IsClosed = message.IsClosed;
			row.ShowPopupOnUse = message.ShouldSerializeShowPopupOnUse()
				? message.ShowPopupOnUse
				: null;
			row.QuestItemEffect = message.ShouldSerializeQuestItemEffect()
				? message.QuestItemEffect
				: null;
			row.WaypointType = message.ShouldSerializeWaypointType()
				? message.WaypointType
				: null;
			row.TemplateItemId = message.ShouldSerializeTemplateItemId()
				? message.TemplateItemId
				: null;
		}

		// The captured `Data` is the full Conan packet ([u32 len][u32 crc][u32 headerLen][header][body]); the object
		// body starts after the 12-byte prefix + the header. Returns null if the framing is malformed. The body it
		// yields still carries its own [u32 recordLen] - RecordOf strips that to what WorldObjectCodec parses.
		public static byte[]? ExtractBody(byte[] data)
		{
			if (data == null || data.Length < 12)
				return null;

			var headerLen = (int)data.ReadU32Be(8);
			var bodyStart = 12 + headerLen;
			return bodyStart <= data.Length
				? data[bodyStart..]
				: null;
		}

		// Identity discriminator for deduping/matching world objects: the SimpleDynel `display_name` (a stable
		// localized id), or "" for object classes that carry no such id. Two distinct props can sit at the EXACT
		// same coordinates (e.g. the map-4000 editor scene has localized 347659 and 3072626 at one spot, #222), so
		// position alone is not a unique key - the dedup and the burst/world-entry matching both key by (position,
		// identity). "" when the class has no display_name or the body is malformed (such objects don't collide, so
		// position alone keeps disambiguating them).
		public static string ReadIdentity(ReadOnlySpan<byte> carrierBody)
		{
			return carrierBody.Length <= 4 || !TryReadIdentity(RecordOf(carrierBody), out var message)
				? string.Empty
				: message.DisplayName ?? string.Empty;
		}

		// The stored row's object identity - its display element, rebuilt from the columns (was the lifted
		// DisplayName string, and before that the captured blob; both retired). Used to key the table rows the same
		// way the burst bodies are keyed.
		public static string ReadIdentity(WorldObject row)
		{
			return row == null
				? string.Empty
				: WorldObjectDisplay.ToWire(row) ?? string.Empty;
		}

		// A captured carrier body ([u32 recordLen][record]) reduced to the record WorldObjectCodec parses.
		public static ReadOnlySpan<byte> RecordOf(ReadOnlySpan<byte> carrierBody)
		{
			return carrierBody[4..];
		}

		// Builds the typed identity message from a WorldObject row's columns (the DB-source path). Kdtree is always 0
		// on world dynels; the two nullable columns are emitted only when present, reproducing the captured field-set.
		// Stays here, not on WorldObject: the entity is a plain Core.Database row, and wire/protobuf construction is
		// the codec's job - putting it on the row would couple the DB layer to the protobuf message type.
		public static ProtoSimpleDynelIIR ToMessage(WorldObject row)
		{
			var message = new ProtoSimpleDynelIIR
			{
				Kdtree = row.Kdtree,
				Name = row.Name,
				DisplayName = WorldObjectDisplay.ToWire(row),
				CarsGroup = row.CarsGroup,
				IsClosed = row.IsClosed
			};
			if (row.ShowPopupOnUse.HasValue)
				message.ShowPopupOnUse = row.ShowPopupOnUse.Value;
			if (row.QuestItemEffect.HasValue)
				message.QuestItemEffect = row.QuestItemEffect.Value;
			if (row.WaypointType.HasValue)
				message.WaypointType = row.WaypointType.Value;
			if (row.TemplateItemId.HasValue)
				message.TemplateItemId = row.TemplateItemId.Value;
			return message;
		}

		// Lifts the identity + geometry columns from a captured `record` (the WorldObjectCodec body). Self-validating:
		// populates the typed columns only if the column-rebuild reproduces the record byte-exact, else leaves the row
		// un-lifted (it then can't build - zero risk for any shape not fully modelled). Idempotent.
		public static bool TryLiftColumns(WorldObject row, ReadOnlySpan<byte> record)
		{
			WorldObjectDisplay.Clear(row);
			if (!TrySplitTail(record, out var identity, out var geometryTail))
				return false;

			ProtoSimpleDynelIIR message;
			try
			{
				message = SimpleDynelIdentityCodec.Parse(identity);
			}
			catch (CodecParseException)
			{
				return false;
			}

			ApplyColumns(row, message);
			if (SimpleDynelIdentityCodec.Build(ToMessage(row)).AsSpan().SequenceEqual(identity))
			{
				// Columnize the geometry record that follows, and keep the lift ALL-OR-NOTHING (#410). A container's
				// tail carries its loot CONTENTS after the geometry record; those are session state and are not
				// persisted, so the rebuild is compared against the part the columns own.
				if (WorldObjectGeometryColumns.TryParse(row, geometryTail, out var geometryLength) &&
					WorldObjectGeometryColumns.ToWire(row).AsSpan().SequenceEqual(geometryTail[..geometryLength]))
					return true;
			}

			WorldObjectDisplay.Clear(row); // un-faithful lift -> fall back to the verbatim slice
			return false;
		}

		// Locates and parses the trailing ProtoLootBagIIR out of a loot-bag record. The contents blob is the FINAL
		// length-prefixed message in the tail: its [u32 len] is the unique offset where len + 4 consumes exactly to
		// the end. Returns false if no such framing exists. `contentsStart` is its offset WITHIN the record's tail.
		public static bool TryLocateContents(ReadOnlySpan<byte> record, [NotNullWhen(true)] out ProtoLootBagIIR? contents, out int contentsStart)
		{
			contents = null;
			contentsStart = 0;

			WorldObjectIIR iir;
			try
			{
				iir = WorldObjectCodec.Parse(record);
			}
			catch (CodecParseException)
			{
				return false;
			}

			var tail = iir.Tail;
			for (var k = 0; k + 4 <= tail.Length; k++)
			{
				var len = tail.AsSpan().ReadU32Be(k);
				if (len == 0 || k + 4 + len != tail.Length)
					continue;

				contents = LootBagContentsCodec.Parse(tail.AsSpan(k));
				contentsStart = k;
				return true;
			}

			return false;
		}

		// The typed identity message out of a record's tail, without touching the row's columns.
		public static bool TryReadIdentity(ReadOnlySpan<byte> record, [NotNullWhen(true)] out ProtoSimpleDynelIIR? message)
		{
			message = null;
			if (!TrySplitTail(record, out var identity, out _))
				return false;

			try
			{
				message = SimpleDynelIdentityCodec.Parse(identity);
				return true;
			}
			catch (CodecParseException)
			{
				return false;
			}
		}

		// Replaces the identity message inside a record with one constructed from the WorldObject's columns, keeping
		// the header + transform + geometry tail. Byte-identical to the original when the columns match the captured
		// message (proven for every captured field-shape). Returns false when the record can't be framed as a dynel or
		// the row has no lifted identity.
		public static bool TryReplaceIdentity(ReadOnlySpan<byte> record, WorldObject row, [NotNullWhen(true)] out byte[]? result)
		{
			result = null;
			if (row?.DisplayKind == null || !TrySplitTail(record, out _, out var geometryTail))
				return false;

			var iir = WorldObjectCodec.Parse(record);
			result = WorldObjectCodec.Build(iir with { Tail = [.. SimpleDynelIdentityCodec.Build(ToMessage(row)), .. geometryTail] });
			return true;
		}

		// The wire typeTag (the 0x0000C8xx EntityClass ack in the object header) for a world-object CLASS. Class-stable
		// (one distinct tag per SubOpcode across the whole capture set), so it is derived here rather than stored.
		public static uint TypeTagForClass(uint subOpcode)
		{
			return subOpcode switch
			{
				(uint)GameServer0X2000Opcodes.ClimbingDynelFullUpdateIIR => EntityClass.ClimbingDynel,
				(uint)GameServer0X2000Opcodes.DoorFullUpdateIIR => EntityClass.ZoneLabel,
				(uint)GameServer0X2000Opcodes.SimpleDynelFullUpdateIIR => EntityClass.SimpleDynel,
				(uint)GameServer0X2000Opcodes.DestructibleFullUpdateIIR => EntityClass.Destructible,
				(uint)GameServer0X2000Opcodes.LootBagFullUpdateIIR => EntityClass.LootBag,
				_ => EntityClass.SimpleDynel
			};
		}

		// Splits a record's tail into its leading length-prefixed identity message and everything after it.
		private static bool TrySplitTail(ReadOnlySpan<byte> record, out ReadOnlySpan<byte> identity, out ReadOnlySpan<byte> geometryTail)
		{
			identity = default;
			geometryTail = default;

			WorldObjectIIR iir;
			try
			{
				iir = WorldObjectCodec.Parse(record);
			}
			catch (CodecParseException)
			{
				return false;
			}

			var tail = iir.Tail.AsSpan();
			if (tail.Length < 4)
				return false;

			var length = 4 + (int)tail.ReadU32Be(0);
			if (length > tail.Length)
				return false;

			identity = tail[..length];
			geometryTail = tail[length..];
			return true;
		}
	}
}
