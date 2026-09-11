using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     Maps a world object's stored columns to and from the typed messages inside its full-update body.
	/// </summary>
	/// <remarks>
	///     Not a codec: nothing here owns a message - <see cref="WorldObjectCodec" /> owns the body,
	///     <see cref="SimpleDynelIdentityCodec" /> the identity message and LootBagContentsCodec the container tail.
	///     This is the importer's lift and the builder's row mapping.
	///     <para>
	///         The lift is deliberately all-or-nothing: a record that does not rebuild byte-exact from the columns
	///         leaves the row un-lifted rather than half-imported, because a wrong display string would be fed to the
	///         client as-is.
	///     </para>
	/// </remarks>
	public static class WorldObjectColumns
	{
		/// <summary>Copies a parsed identity message into a <see cref="WorldObject" /> row's columns.</summary>
		/// <remarks>
		///     The nullable columns stay null when the field is absent, so <see cref="ToMessage" /> round-trips back to
		///     the same wire bytes.
		/// </remarks>
		public static void ApplyColumns(WorldObject row, ProtoSimpleDynelIIR message)
		{
			row.Kdtree = message.Kdtree;
			row.Name = message.Name;
			WorldObjectDisplay.TryApply(row, message.DisplayName);
			row.CarsGroup = message.CarsGroup;
			// Field 12 ("is closed") is deliberately not lifted - it is live door state, not a placement.
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

		/// <summary>
		///     Extracts the object body from a captured `Data` blob - a full Conan packet
		///     ([u32 len][u32 crc][u32 headerLen][header][body]), so the body starts after the 12-byte prefix plus the
		///     header.
		/// </summary>
		/// <returns>
		///     Null when the framing is malformed. The body still carries its own [u32 recordLen]; <see cref="RecordOf" />
		///     strips that down to what <see cref="WorldObjectCodec" /> parses.
		/// </returns>
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

		/// <summary>
		///     Identity discriminator for deduping and matching world objects: the SimpleDynel `display_name` (a stable
		///     localized id), or "" for object classes that carry no such id.
		/// </summary>
		/// <remarks>
		///     Two distinct props can sit at exactly the same coordinates - the map-4000 editor scene has localized
		///     347659 and 3072626 at one spot - so position alone is not a unique key: the dedup and the burst /
		///     world-entry matching both key by (position, identity). The result is "" when the class has no
		///     display_name or the body is malformed; such objects do not collide, so position alone keeps
		///     disambiguating them.
		/// </remarks>
		public static string ReadIdentity(ReadOnlySpan<byte> carrierBody)
		{
			return carrierBody.Length <= 4 || !TryReadIdentity(RecordOf(carrierBody), out var message)
				? string.Empty
				: message.DisplayName ?? string.Empty;
		}

		/// <summary>
		///     The stored row's object identity - its display element, rebuilt from the columns. Keys the table rows the
		///     same way the burst bodies are keyed.
		/// </summary>
		public static string ReadIdentity(WorldObject row)
		{
			return row == null
				? string.Empty
				: WorldObjectDisplay.ToWire(row) ?? string.Empty;
		}

		/// <summary>Reduces a captured carrier body ([u32 recordLen][record]) to the record <see cref="WorldObjectCodec" /> parses.</summary>
		public static ReadOnlySpan<byte> RecordOf(ReadOnlySpan<byte> carrierBody)
		{
			return carrierBody[4..];
		}

		/// <summary>Builds the typed identity message from a <see cref="WorldObject" /> row's columns.</summary>
		/// <param name="isClosed">
		///     The object's live door state, which has no column: a capture is evidence of what a door was doing when the
		///     packet was sent, not of its placement - the map 4020 "Wharf Door" is captured closed in one session and
		///     open in another. It defaults to closed, the resting state and the state all 2400 shipped objects were
		///     captured in, and the live world overrides it per object (LiveWorldObject.IsClosed).
		/// </param>
		/// <remarks>
		///     Kdtree is always 0 on world dynels; the nullable columns are emitted only when present, reproducing the
		///     captured field set. Field 12 is written only when closed - retail omits it on an open door, so "absent"
		///     needs no separate flag.
		///     <para>
		///         This stays here rather than on <see cref="WorldObject" />: the entity is a plain Core.Database row and
		///         wire/protobuf construction is the codec layer's job, so putting it on the row would couple the DB
		///         layer to the protobuf message type.
		///     </para>
		/// </remarks>
		public static ProtoSimpleDynelIIR ToMessage(WorldObject row, bool isClosed = true)
		{
			var message = new ProtoSimpleDynelIIR
			{
				Kdtree = row.Kdtree,
				Name = row.Name,
				DisplayName = WorldObjectDisplay.ToWire(row),
				CarsGroup = row.CarsGroup
			};
			if (isClosed)
				message.IsClosed = true;
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

		/// <summary>
		///     Lifts the identity and geometry columns from a captured `record` (the <see cref="WorldObjectCodec" />
		///     body). Idempotent.
		/// </summary>
		/// <remarks>
		///     Self-validating: the typed columns are populated only if the column rebuild reproduces the record
		///     byte-exact, otherwise the row is left un-lifted and cannot build at all - so a shape that is not fully
		///     modelled carries no risk of being sent wrong.
		/// </remarks>
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
			// The captured door state is fed into the comparison rather than stored: every other identity field still has
			// to rebuild byte-exact, so the lift stays all-or-nothing without persisting what the door happened to be
			// doing in this capture.
			if (SimpleDynelIdentityCodec.Build(ToMessage(row, message.ShouldSerializeIsClosed() && message.IsClosed)).AsSpan().SequenceEqual(identity))
			{
				// Columnize the geometry record that follows, keeping the lift all-or-nothing. A container's tail carries
				// its loot contents after the geometry record; those are session state and are not persisted, so the
				// rebuild is compared against the part the columns own.
				if (WorldObjectGeometryColumns.TryParse(row, geometryTail, out var geometryLength) &&
					WorldObjectGeometryColumns.ToWire(row).AsSpan().SequenceEqual(geometryTail[..geometryLength]))
					return true;
			}

			WorldObjectDisplay.Clear(row); // un-faithful lift -> fall back to the verbatim slice
			return false;
		}

		/// <summary>Locates and parses the trailing ProtoLootBagIIR out of a loot-bag record.</summary>
		/// <remarks>
		///     The contents blob is the final length-prefixed message in the tail: its [u32 len] is the unique offset
		///     where len + 4 consumes exactly to the end.
		/// </remarks>
		/// <param name="contentsStart">The blob's offset within the record's tail.</param>
		/// <returns>False when no such framing exists.</returns>
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

		/// <summary>The typed identity message out of a record's tail, without touching the row's columns.</summary>
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

		/// <summary>
		///     Replaces the identity message inside a record with one constructed from the <see cref="WorldObject" />'s
		///     columns, keeping the header, transform and geometry tail.
		/// </summary>
		/// <remarks>Byte-identical to the original when the columns match the captured message, for every captured field shape.</remarks>
		/// <returns>False when the record cannot be framed as a dynel, or the row has no lifted identity.</returns>
		public static bool TryReplaceIdentity(ReadOnlySpan<byte> record, WorldObject row, [NotNullWhen(true)] out byte[]? result, bool isClosed = true)
		{
			result = null;
			if (row?.DisplayKind == null || !TrySplitTail(record, out _, out var geometryTail))
				return false;

			var iir = WorldObjectCodec.Parse(record);
			result = WorldObjectCodec.Build(iir with { Tail = [.. SimpleDynelIdentityCodec.Build(ToMessage(row, isClosed)), .. geometryTail] });
			return true;
		}

		/// <summary>
		///     The wire typeTag (the 0x0000C8xx EntityClass ack in the object header) for a world-object class.
		/// </summary>
		/// <remarks>
		///     Class-stable - one distinct tag per SubOpcode across the whole capture set - so it is derived here rather
		///     than stored.
		/// </remarks>
		public static uint TypeTagForClass(uint subOpcode)
		{
			return subOpcode switch
			{
				(uint)IirTypes.ClimbingDynelFullUpdateIIR => EntityClass.ClimbingDynel,
				(uint)IirTypes.DoorFullUpdateIIR => EntityClass.ZoneLabel,
				(uint)IirTypes.SimpleDynelFullUpdateIIR => EntityClass.SimpleDynel,
				(uint)IirTypes.DestructibleFullUpdateIIR => EntityClass.Destructible,
				(uint)IirTypes.LootBagFullUpdateIIR => EntityClass.LootBag,
				_ => EntityClass.SimpleDynel
			};
		}

		/// <summary>Splits a record's tail into its leading length-prefixed identity message and everything after it.</summary>
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
