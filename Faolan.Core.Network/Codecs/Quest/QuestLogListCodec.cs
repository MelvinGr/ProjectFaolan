using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// QuestLogListIIR (0x504B7924) - the active-quest LIST that POPULATES the J-panel. WITHOUT it the client never
	// shows an accepted quest in the log even though it received the per-quest QuestFullUpdate detail (the detail
	// alone is not enough - decoded from randochar frame 687 / strickenbla frame 3086). Retail sends it at every
	// grant, listing ALL the player's active quests (tracked quest first), plus the current area's journal-title slot.
	//
	// Wire:
	//   [subop][class][player][u8 0x01][u32 1][u32 areaId][u32 trackedQuestId][u32 count]
	//   count × { [u32 ordinal(1-based)][u32 questId] }
	//   [string areaTitle]                     <remoteformat areaId/50300>
	//   [u32 1][10 zero bytes]
	//
	// Reading sequentially is what surfaced two regions the old fixed-offset parser stepped straight over: the
	// [u32 1][u32 areaId] pair after the marker (areaId is real data it DISCARDED) and the per-entry ordinal.
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2).
	public static class QuestLogListCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.QuestLogListIIR;

		private const int TrailerZeroBytes = 10;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(QuestLogListIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint playerHandle, uint areaId, uint trackedQuestId, IReadOnlyList<uint> questIds, string areaTitle)
		{
			return Build(new QuestLogListIIR(playerHandle, areaId, trackedQuestId, questIds, areaTitle));
		}

		public static QuestLogListIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestLogListCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestLogListIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "QuestLogListIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU8(0x01, "record marker");
			r.ExpectU32Be(1, "leading constant");
			var areaId = r.ReadU32Be();
			var trackedQuestId = r.ReadU32Be();

			var count = r.ReadU32Be();
			var questIds = new List<uint>((int)count);
			for (var i = 0; i < count; i++)
			{
				r.ExpectU32Be((uint)(i + 1), "1-based log ordinal");
				questIds.Add(r.ReadU32Be());
			}

			var areaTitle = r.ReadStringU16Length();
			r.ExpectU32Be(1, "trailer constant");
			for (var i = 0; i < TrailerZeroBytes; i++)
				r.ExpectU8(0, "trailer padding");

			return new QuestLogListIIR(handle, areaId, trackedQuestId, questIds, areaTitle);
		}

		internal static void Write(FaolanStream s, QuestLogListIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteByte((byte)0x01).WriteUInt32(1u).WriteUInt32(v.AreaId).WriteUInt32(v.TrackedQuestId)
				.WriteUInt32(v.QuestIds.Count);

			for (var i = 0; i < v.QuestIds.Count; i++)
			{
				s.WriteUInt32(i + 1);
				s.WriteUInt32(v.QuestIds[i]);
			}

			s.WriteString(v.AreaTitle);
			s.WriteUInt32(1u);
			for (var i = 0; i < TrailerZeroBytes; i++)
				s.WriteByte((byte)0x00);
		}
	}

	// QuestLogListIIR (0x504B7924).
	public sealed record QuestLogListIIR(uint PlayerHandle, uint AreaId, uint TrackedQuestId, IReadOnlyList<uint> QuestIds, string AreaTitle);
}
