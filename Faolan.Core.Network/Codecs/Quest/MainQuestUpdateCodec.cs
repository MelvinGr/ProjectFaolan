using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// MainQuestUpdateIIR (0x504B7924) - the active-quest LIST that POPULATES the J-panel. WITHOUT it the client never
	// shows an accepted quest in the log even though it received the per-quest QuestFullUpdate detail (the detail
	// alone is not enough - decoded from randochar frame 687 / strickenbla frame 3086). Retail sends it at every
	// grant, listing ALL the player's active quests (tracked quest first), plus the current area's journal-title slot.
	//
	// The client class is `MainQuestUpdateIIR_c` (`BKDR-31("MainQuestUpdateIIR_c") == 0x504B7924`); its reader is
	// `FUN_00918560` and its consumer `MainQuestUpdateIIR_c::Activate()` = `FUN_00918cf0`.
	//
	// Wire (`FUN_00918560`, field offsets are the IIR object's):
	//   [subop][class][player][u8 0x01]
	//   [u32 operation]                        +0x2c - MainQuestUpdateOp, the Activate() switch selector
	//   [u32 areaId]                           +0x30
	//   [u32 trackedQuestId]                   +0x44
	//   [u32 count] count × { [u32 ordinal][u32 questId] }    the pair vector at +0x34
	//   [string areaTitle]                     <remoteformat areaId/50300>
	//   [u32 unknown0][u32 unknown1][u32 unknown2][u16 leavingMissionArea]   +0x64 +0x68 +0x6c +0x70
	//
	// ⚠️ The reader checks NOTHING - every field is a plain read, so three values pinned as constants from the early
	// captures rejected 8 of the 46 captured lists (#545):
	//   * the "leading constant 1" is the OPERATION (see MainQuestUpdateOp); it is 2 = Remove on every captured
	//     empty list, which is why "always 1" held for as long as every capture was of a quest being granted.
	//   * the per-entry word is not a 1-based loop index. Entries can SHARE it - NecroL80's King Conan castle list
	//     carries 15 entries whose words run 1,1,2,3,4,5,6,7,7,8,9,10,11,12,13 - so it is a group/sort key that is
	//     carried, not regenerated. It is the same field as `QuestJournalEntry.Ordinal`: area 1834's journal and log
	//     list in Hakotep_hox_login_gameplay0 carry an identical (1,14128) (2,14158).
	//   * the "trailer constant 1" is `unknown0`, which is 0 on every Remove and 4 in one capture (path-of-azura).
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2).
	public static class MainQuestUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.MainQuestUpdateIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(MainQuestUpdateIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The grant path's form: an Add naming every active quest in order, with the trailer retail sends for one.
		public static byte[] Build(uint playerHandle, uint areaId, uint trackedQuestId, IReadOnlyList<uint> questIds, string areaTitle)
		{
			ArgumentNullException.ThrowIfNull(questIds);
			var entries = questIds.Select((id, i) => new MainQuestUpdateEntry((uint)(i + 1), id)).ToList();
			return Build(new MainQuestUpdateIIR(playerHandle, MainQuestUpdateOp.Add, areaId, trackedQuestId, entries, areaTitle)
			{
				Unknown0 = 1
			});
		}

		public static MainQuestUpdateIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MainQuestUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static MainQuestUpdateIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "MainQuestUpdateIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU8(0x01, "record marker");

			// Carried, not validated: the client switches on it and simply does nothing for a value it has no case
			// for, so an unknown operation is not a malformed body.
			var operation = (MainQuestUpdateOp)r.ReadU32Be();
			var areaId = r.ReadU32Be();
			var trackedQuestId = r.ReadU32Be();

			var count = r.ReadU32Be();
			var entries = new List<MainQuestUpdateEntry>((int)count);
			for (var i = 0; i < count; i++)
			{
				var ordinal = r.ReadU32Be();
				entries.Add(new MainQuestUpdateEntry(ordinal, r.ReadU32Be()));
			}

			var areaTitle = r.ReadStringU16Length();

			return new MainQuestUpdateIIR(handle, operation, areaId, trackedQuestId, entries, areaTitle)
			{
				Unknown0 = r.ReadU32Be(),
				Unknown1 = r.ReadU32Be(),
				Unknown2 = r.ReadU32Be(),
				LeavingMissionArea = r.ReadU16Be()
			};
		}

		internal static void Write(FaolanStream s, MainQuestUpdateIIR v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteByte((byte)0x01)
				.WriteUInt32((uint)v.Operation).WriteUInt32(v.AreaId).WriteUInt32(v.TrackedQuestId).WriteUInt32(v.Entries.Count);

			foreach (var e in v.Entries)
				s.WriteUInt32(e.Ordinal).WriteUInt32(e.QuestId);

			s.WriteString(v.AreaTitle);
			s.WriteUInt32(v.Unknown0).WriteUInt32(v.Unknown1).WriteUInt32(v.Unknown2).WriteUInt16(v.LeavingMissionArea);
		}
	}

	// The `MainQuestUpdateIIR_c::Activate()` switch on the leading word (`FUN_00918cf0`). Each name is the client's
	// own log line for that case ("Activate() was called to add a new MainQuest.", ...); case 0 logs "was called with
	// Invalid type!". Captures carry only Add (every populated list) and Remove (every empty one).
	public enum MainQuestUpdateOp : uint
	{
		Invalid = 0,
		Add = 1,
		Remove = 2,
		UpdateStartTemplate = 3,
		UpdateTimeout = 4
	}

	// One line of the J-panel list. `Ordinal` is the client's group/sort key - see the codec note; it repeats across
	// entries, so it is carried rather than derived from the position.
	public sealed record MainQuestUpdateEntry(uint Ordinal, uint QuestId);

	// MainQuestUpdateIIR (0x504B7924).
	public sealed record MainQuestUpdateIIR(
		uint PlayerHandle,
		MainQuestUpdateOp Operation,
		uint AreaId,
		uint TrackedQuestId,
		IReadOnlyList<MainQuestUpdateEntry> Entries,
		string AreaTitle)
	{
		// The three trailing words. `Activate()`'s Add case forwards all three into the character's quest holder
		// (`FUN_00810840` stores two of them at holder+0x48 / +0x50); nothing names or checks them. Across the 46
		// captured lists: Unknown0 is 1 on every Add but 4 in one (path-of-azura) and 0 on every Remove, Unknown1 is
		// always 0, and Unknown2 is 0 except in the two post-Tortage captures, where it is 1.
		public uint Unknown0 { get; init; }
		public uint Unknown1 { get; init; }
		public uint Unknown2 { get; init; }

		// Read as a u16 at +0x70 and used ONLY by the UpdateTimeout case, which shows "Entering mission area" when it
		// is zero and "Leaving mission area" otherwise. 0 in every capture.
		public ushort LeavingMissionArea { get; init; }
	}
}
