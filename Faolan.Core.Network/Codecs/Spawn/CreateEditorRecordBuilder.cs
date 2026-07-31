using System;
using System.Numerics;
using System.Text;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The create-editor burst's view of the IIR records it streams on its GameAgent->GameInterface 0x2000
	// conversation (issue #189): each one WITHOUT its leading sub-opcode word, because the burst helper
	// (CreateFlowService.SendIir) writes that itself from the enum it is handed.
	//
	// NOT a codec, and not an emitter (#530 wave 5b). It used to build eleven records itself, of which SEVEN are
	// owned by codecs elsewhere in the tree - BehaviorControlCenter, Energy, Feedback, ComboBook, Regen, Stat and
	// StatUpdate - so it was a second encoder for each of them, and none of those were in its `HandledTypes`, which
	// is why no gate could see it. That is the same blind spot wave 4 found in the 54-byte MagicBuff data region.
	// Its own four records (CharacterAction, CharInPlay, SetMorphValue, SetName) now have codecs of their own here.
	//
	// Everything below is a one-line DELEGATION that slices off the four bytes the burst helper re-writes. When the
	// burst learns to take a whole record, this file goes away.
	public static class CreateEditorRecordBuilder
	{
		// BehaviorControlCenterIIR (0x642CD3D6), registration form - see BehaviorControlCenterCodec for the grammar.
		// The protobuf is a CommandMessage; protobuf-net omits an unset optional and emits fields in ascending order,
		// so the optional f2/f4/f11/f12 land exactly where the hand-written writer used to put them.
		// `form` is 0x0004 for the common registration; the rarer 0x0007 subtype adds protobuf field 4 (a varint,
		// between phase and field5).
		public static byte[] BuildBehaviorControlCenterIir(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6, uint type = EntityClass.Character, uint? field2 = null,
			uint? field4 = null, uint? field11 = null, uint? field12 = null, uint? boundA = null, uint? boundB = null, ushort form = 0x0004)
		{
			return Record(BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(handle, slot, behaviorId, phase, field5, field6, field2, field4, field11, field12, boundA, boundB, form)));
		}

		// The LONG (116-byte) form: the same head plus an 88-byte behaviour-state tail that binds the behaviour to an
		// EQUIPPED ITEM (the three records in WorldPrep each pair with one of the burst's DispatchAddItem equipment
		// pieces - the item's handle, its 4-char entity tag and its transform all match):
		//   [u32 index][u32 someId][u32 itemHandle][u32 itemHandle][u32 0][u32 1][char[4] tag]
		//   [byte[16] transform][byte[16] transform][8-byte ownerStamp][8-byte ownerStamp][12 zero bytes]
		// The item handle + 4-char tag are repeated; the 16-byte transform is the item's pose packed as four
		// big-endian u32 (the same x/y/z + rotation values the paired DispatchAddItem carries as fixed32 protobuf
		// fields), also repeated; `ownerStamp` (0xB290805C 0x29E627CA) is the fixed creator/timestamp pair that
		// recurs across the whole WorldPrep burst (semantics unpinned). The trailing 3 u32 are 0 in the WorldPrep
		// equipment records but carry a small state pair (e.g. 0,1,1) in the create-world cutscene's mount/companion
		// long form. Decoded from WorldPrepBurst.bin by tools/capture/decode_create_iir_records.py.
		public static byte[] BuildBehaviorControlCenterIirLong(uint handle, uint slot, uint behaviorId, uint phase, uint field5, uint field6, uint index, uint someId, uint itemHandle, string tag,
			byte[] transform, uint type = EntityClass.Character, uint trailingA = 0, uint trailingB = 0, uint trailingC = 0)
		{
			var tagBytes = Encoding.Latin1.GetBytes(tag);
			if (tagBytes.Length != 4)
				throw new ArgumentException("behavior entity tag must be 4 ASCII chars", nameof(tag));
			if (transform.Length != 16)
				throw new ArgumentException("behavior transform must be 16 bytes", nameof(transform));

			using var itemTail = new FaolanStream();
			itemTail
				.WriteUInt32(index)
				.WriteUInt32(someId)
				.WriteUInt32(itemHandle)
				.WriteUInt32(itemHandle)
				.WriteUInt32(0u)
				.WriteUInt32(1u)
				.WriteArray(tagBytes)
				.WriteArray(transform)
				.WriteArray(transform)
				.WriteArray(NullIdentity.Bytes)
				.WriteArray(NullIdentity.Bytes)
				.WriteUInt32(trailingA)
				.WriteUInt32(trailingB)
				.WriteUInt32(trailingC);

			var head = BehaviorAnimations.Registration(handle, slot, behaviorId, phase, field5, field6);
			return Record(BehaviorControlCenterCodec.Build(head with { Tail = itemTail.ToArray() }));
		}

		// CharInPlayIIR (0x864CFEF8) - CharInPlayCodec owns the layout.
		public static byte[] BuildCharInPlayIir(uint handle, byte flag = 0, uint type = EntityClass.Character)
		{
			return Record(CharInPlayCodec.Build(new CharInPlayIIR(handle, flag)));
		}

		// CharacterActionIIR (0x6B6FD368) - CharacterActionCodec owns the layout.
		public static byte[] BuildCharacterActionIir(uint handle, uint actionId, uint type = EntityClass.Character)
		{
			return Record(CharacterActionCodec.Build(new CharacterActionIIR(handle, actionId)));
		}

		// ComboBookIIR (0xA8BBEB7F) - ComboBookCodec owns the layout.
		public static byte[] BuildComboBookIir(uint handle, uint comboId, uint type = EntityClass.Character)
		{
			return Record(ComboBookCodec.Build(new ComboBookIIR(handle, comboId)));
		}

		// EnergyIIR (0x40749B8A) - EnergyCodec owns the layout.
		public static byte[] BuildEnergyIir(uint type, uint handle, byte flag, uint value)
		{
			return Record(EnergyCodec.Build(new EnergyIIR(handle, flag, value)));
		}

		// FeedbackIIR (0xBA1FD222) - FeedbackCodec owns the layout.
		public static byte[] BuildFeedbackIir(uint handle, byte flag, uint a, uint b, uint c, byte d, uint type = EntityClass.Character)
		{
			return Record(FeedbackCodec.Build(new FeedbackIIR(handle, flag, a, b, c, d)));
		}

		// GameTimeIIR (0x4F57DC08) - GameTimeCodec owns the layout.
		public static byte[] BuildGameTimeIir(float dayTimeSeconds, uint word, uint gameDay, uint unixSeconds, bool isToBePassed = true, float rate = 1.0f)
		{
			return Record(GameTimeCodec.Build(new GameTimeIIR(dayTimeSeconds, word, gameDay, unixSeconds, isToBePassed, rate)));
		}

		// PlayfieldFIIR (0x5D85BFC7, "MapChange") - PlayfieldFCodec owns the layout.
		public static byte[] BuildPlayfieldFiir(float posX, float posY, float posZ, uint mapId, uint charInstance, uint count, uint playfieldToken, float nearClip, float farClip, uint flags,
			uint sceneWord = 0u, uint blobLastWord = 0u, uint fieldA = 0u, uint fieldC = 7u)
		{
			return Record(PlayfieldFCodec.Build(new Vector3(posX, posY, posZ), mapId, charInstance, count, playfieldToken, nearClip, farClip, flags, sceneWord, blobLastWord, fieldA, fieldC));
		}

		// RegenIIR (0x28D0AC99) - RegenCodec owns the layout.
		public static byte[] BuildRegenIir(uint handle, uint v1, uint v2, uint v3, byte flag = 0, uint type = EntityClass.Character)
		{
			return Record(RegenCodec.Build(new RegenIIR(handle, flag, (int)v1, (int)v2, (int)v3)));
		}

		// SetMorphValueIIR (0xBADF5A4B) - SetMorphValueCodec owns the layout.
		public static byte[] BuildSetMorphValueIir(uint handle, string name, float weight, byte p, uint q, uint type = EntityClass.Character)
		{
			return Record(SetMorphValueCodec.Build(new SetMorphValueIIR(handle, name, weight, p, q)));
		}

		// SetNameIIR (0xADCE0CDA), the create-editor's "name accepted" record - SetNameCodec owns the layout.
		public static byte[] BuildSetNameIir(uint handle, string name, uint type = EntityClass.Character)
		{
			return Record(SetNameCodec.Build(SetNames.Accepted(handle, name)));
		}

		// StatIIR (0xF98E10B3) - StatCodec owns the layout.
		public static byte[] BuildStatIir(uint type, uint handle, byte flag, uint statId, uint value)
		{
			return Record(StatCodec.Build(new StatIIR(handle, flag, statId, value)));
		}

		// StatUpdateIIR (0x96B8DC59) - StatUpdateCodec owns the layout.
		public static byte[] BuildStatUpdateIir(uint handle, (uint StatId, uint Value)[] stats, uint type = EntityClass.Character)
		{
			var block = new CharacterStatBlock();
			foreach (var (statId, value) in stats)
				block.SetRaw((CharacterStatId)statId, value);

			return Record(StatUpdateCodec.Build(new StatUpdateIIR(handle, block)));
		}

		// The in-WORLD SetNameIIR (#363) - the same record with the nameplate-refresh bool set.
		public static byte[] BuildWorldSetNameIir(uint handle, string name, uint type = EntityClass.Character)
		{
			return Record(SetNameCodec.Build(SetNames.Nameplate(handle, name)));
		}

		// The burst helper writes the sub-opcode itself, so it wants the record WITHOUT it.
		private static byte[] Record(byte[] withSubOpcode)
		{
			return withSubOpcode[4..];
		}
	}
}
