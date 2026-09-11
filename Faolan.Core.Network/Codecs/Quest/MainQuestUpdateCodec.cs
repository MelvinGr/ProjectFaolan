using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     MainQuestUpdateIIR - the active-quest list that populates the J-panel. Without it the client never shows an
	///     accepted quest in the log, even having received the per-quest QuestFullUpdate detail; the detail alone is not
	///     enough (decoded from randochar frame 687 and strickenbla frame 3086). Retail sends it at every grant, listing all
	///     the player's active quests with the tracked quest first, plus the current area's journal-title slot.
	/// </summary>
	/// <remarks>
	///     The client class is MainQuestUpdateIIR_c; its reader is FUN_00918560 and its consumer
	///     MainQuestUpdateIIR_c::Activate (FUN_00918cf0). The field offsets below are the IIR object's:
	///     <code>
	///     [subop][class][player][u8 0x01]
	///     [u32 operation]                       +0x2c - MainQuestUpdateOp, the Activate() switch selector
	///     [u32 areaId]                          +0x30
	///     [u32 trackedQuestId]                  +0x44
	///     [u32 count] count x { [u32 ordinal][u32 questId] }   the pair vector at +0x34
	///     [string areaTitle]                    remoteformat areaId / 50300
	///     [u32 unknown0][u32 unknown1][u32 unknown2][u16 leavingMissionArea]   +0x64 +0x68 +0x6c +0x70
	///     </code>
	///     <para>
	///         The reader validates nothing - every field is a plain read - and three of the fields look constant in a
	///         grant-only corpus while 8 of the 46 captured lists disagree:
	///         <list type="bullet">
	///             <item>
	///                 The leading word is the operation (see <see cref="MainQuestUpdateOp" />). It is Remove on every
	///                 captured empty list, so it only looks like a constant 1 while every capture is of a quest being
	///                 granted.
	///             </item>
	///             <item>
	///                 The per-entry word is not a 1-based loop index: entries can share it - NecroL80's King Conan castle
	///                 list carries 15 entries whose words run 1,1,2,3,4,5,6,7,7,8,9,10,11,12,13 - so it is a group and sort
	///                 key that is carried rather than regenerated. It is the same field as QuestJournalEntry.Ordinal: area
	///                 1834's journal and log list in Hakotep_hox_login_gameplay0 carry an identical (1, 14128) (2, 14158).
	///             </item>
	///             <item><c>unknown0</c> is 0 on every Remove and 4 in one capture (path-of-azura).</item>
	///         </list>
	///     </para>
	///     <para>The u32 length prefix belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class MainQuestUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.MainQuestUpdateIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(MainQuestUpdateIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     The grant path's form: an Add naming every active quest in order, with the trailer retail sends for one.
		/// </summary>
		/// <remarks>
		///     <paramref name="line" /> is the granted line's row and supplies the two per-line trailer words - the same
		///     MainQuest_c slots the login snapshot's index block fills, so the two producers must agree or the client's
		///     tracker sorts one way at login and another after a grant. Null keeps the solo values (1, 0), which is what a
		///     line with no captured block gets.
		/// </remarks>
		public static byte[] Build(uint playerHandle, uint areaId, uint trackedQuestId, IReadOnlyList<uint> questIds, string areaTitle, QuestLine? line = null)
		{
			ArgumentNullException.ThrowIfNull(questIds);
			var entries = questIds.Select((id, i) => new MainQuestUpdateEntry((uint)(i + 1), id)).ToList();
			return Build(new MainQuestUpdateIIR(playerHandle, MainQuestUpdateOp.Add, areaId, trackedQuestId, entries, areaTitle)
			{
				Unknown0 = line?.TrailerCategory ?? 1,
				Unknown2 = line?.TrailerFlag ?? 0
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

			// Carried, not validated: the client switches on it and does nothing for a value it has no case for, so an
			// unknown operation is not a malformed body.
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
				.WriteUInt32((uint)v.Operation).WriteUInt32(v.QuestLineId).WriteUInt32(v.TrackedQuestId).WriteUInt32(v.Entries.Count);

			foreach (var e in v.Entries)
				s.WriteUInt32(e.Ordinal).WriteUInt32(e.QuestId);

			s.WriteString(v.QuestLineTitle);
			s.WriteUInt32(v.Unknown0).WriteUInt32(v.Unknown1).WriteUInt32(v.Unknown2).WriteUInt16(v.LeavingMissionArea);
		}
	}

	/// <summary>
	///     The MainQuestUpdateIIR_c::Activate switch on the leading word (FUN_00918cf0). Each name is the client's own log
	///     line for that case ("Activate() was called to add a new MainQuest.", ...); case 0 logs "was called with Invalid
	///     type!". Captures carry only Add, on every populated list, and Remove, on every empty one.
	/// </summary>
	public enum MainQuestUpdateOp : uint
	{
		Invalid = 0,
		Add = 1,
		Remove = 2,
		UpdateStartTemplate = 3,
		UpdateTimeout = 4
	}

	/// <summary>
	///     One line of the J-panel list. <c>Ordinal</c> is the client's group and sort key; it repeats across entries, so it
	///     is carried rather than derived from the position.
	/// </summary>
	public sealed record MainQuestUpdateEntry(uint Ordinal, uint QuestId);

	/// <summary>The MainQuestUpdateIIR body.</summary>
	public sealed record MainQuestUpdateIIR(
		uint PlayerHandle,
		MainQuestUpdateOp Operation,
		uint QuestLineId,
		uint TrackedQuestId,
		IReadOnlyList<MainQuestUpdateEntry> Entries,
		string QuestLineTitle)
	{
		/// <summary>
		///     The three trailing words, per-line fields of the client's MainQuest_c: Unknown0 at +0x48, Unknown1 at +0x44,
		///     Unknown2 at +0x50. The Add case builds that object through CoreQuestHolder::UpdateMainQuest (FUN_00810840)
		///     and the constructor FUN_00921980, and the login snapshot's quest index fills the same slots per line
		///     (FUN_00922210), so each word is one field with two producers: QuestIndexRegion.Block.TrailerA is Unknown0,
		///     TableField3 is Unknown1 and TrailerC is Unknown2.
		/// </summary>
		/// <remarks>
		///     What the client does with them, the snapshot side carrying the variance across 97 distinct captured blocks:
		///     <list type="bullet">
		///         <item>
		///             Unknown0 (+0x48, constructor default 1): the only consumer is the quest tracker's insert-position
		///             comparator (FUN_012cde80 / FUN_012cbbb0 compare two QJQuestNode_c +0x70, where FUN_012ce1c0 parked
		///             this), a sort tiebreaker after the level band. It is constant per line and only 1, 4 and 8 occur: 1 on
		///             every solo line (61), 4 on every raid line (6) and 8 on every group-dungeon line (30). It is not a
		///             patch-era marker - the Catacombs shipped at launch and carries 8, while Khitai solo lines carry 1 - and
		///             it is 0 on every Remove, whose path never reads it.
		///         </item>
		///         <item>
		///             Unknown1 (+0x44): stored by both producers and serialized back out by MainQuest_c::WriteToStream, but
		///             read by no consumer; 0 on all 46 captured lists and all 97 captured index blocks.
		///         </item>
		///         <item>
		///             Unknown2 (+0x50): no consumer found. A per-line flag, set on 6 of 97 captured lines - five of them
		///             content newer than the client's 1000104 catalog, plus [Kheshatta_Kalanthes] The Immortal Hand. It does
		///             not mean another line chains from this one; measured, that holds on 85 of 97 with misses both ways.
		///         </item>
		///     </list>
		/// </remarks>
		public uint Unknown0 { get; init; }
		public uint Unknown1 { get; init; }
		public uint Unknown2 { get; init; }

		/// <summary>
		///     Read as a u16 at +0x70 and used only by the UpdateTimeout case, which shows "Entering mission area" when it is
		///     zero and "Leaving mission area" otherwise. Zero in every capture.
		/// </summary>
		public ushort LeavingMissionArea { get; init; }
	}
}
