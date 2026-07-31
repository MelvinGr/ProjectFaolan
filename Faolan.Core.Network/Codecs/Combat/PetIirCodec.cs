using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// PetIIR_c (0x1F4CB0F7) — the server packet that binds a summoned creature to its OWNER, i.e. tells the client
	// "this creature is YOUR pet" (so the pet UI + QueuePetCommand 0x205b become active). Decoded byte-exact from
	// NecroL80_..._casting-with-chat-notes.pcapng (docs/subsystems/summon-pet-decode.md, frames 470/679/919 assign, 1167 remove).
	//
	// Record (the bytes after the 0x2000 carrier's u32 length prefix), all big-endian:
	//   [subop 0x1F4CB0F7][u32 type=0xC350][u32 ownerHandle][u8 env=0] then the reader payload (PetIIR_c::ReadSubClass):
	//     [u32 mode] ; mode∈{0,1} → [u32 type=0xC350][u32 petHandle] ; mode==0 additionally → [u32 f1][u32 f2][u32 f3]
	//   mode 0 = ASSIGN (on summon); mode 1 = REMOVE/update (on dismiss). The (f1,f2,f3) triple is the pet KIND:
	//   the capture shows a COMBAT pet = (0,2,2) and a SOCIAL pet = (5,0,1).
	public static class PetIirCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.PetIIR;

		private const byte Env = 0x00;

		// (f1,f2,f3) kind triples observed in the capture.
		public static readonly (uint F1, uint F2, uint F3) CombatKind = (0, 2, 2);
		public static readonly (uint F1, uint F2, uint F3) SocialKind = (5, 0, 1);

		// mode-0 owner-assign: binds `pet` to `owner` with a kind triple (combat/social).
		public static byte[] BuildAssign(uint owner, uint pet, (uint F1, uint F2, uint F3) kind)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.PetIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(owner)
				.WriteByte(Env)
				.WriteUInt32(0) // mode 0 = assign
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(pet)
				.WriteUInt32(kind.F1)
				.WriteUInt32(kind.F2)
				.WriteUInt32(kind.F3)
				.ToArray();
		}

		// Read counterpart to the two builders: recovers the owner-binding from a captured record. Takes the IIR
		// content ([u32 subop][...], i.e. the carrier body PAST its u32 length prefix), like PlayfieldFCodec.Parse.
		//
		// This is what lets the importer answer "is this entity somebody's summon?" from the WIRE rather than from a
		// roster of pet NAMES: a creature is a pet exactly when the server bound it to an owner, whatever it is called.
		// The kind triple is deliberately not read - mode and the two handles are the whole owner-binding.
		public static bool TryParse(ReadOnlySpan<byte> iirContent, [NotNullWhen(true)] out Parsed? parsed)
		{
			parsed = null;
			if (iirContent.Length < 25 || iirContent.ReadU32Be(0) != Opcode)
				return false;

			var mode = iirContent.ReadU32Be(13);
			if (mode > 1)
				return false; // PetIIR_c::ReadSubClass only reads the two handles for mode 0/1

			parsed = new Parsed(mode, iirContent.ReadU32Be(8), iirContent.ReadU32Be(21));
			return true;
		}

		// An owner-binding as it appeared on the wire. Mode 0 = assign (on summon), 1 = remove (on dismiss); either
		// way `Pet` is a summoned creature, which is the question the importer asks.
		public sealed record Parsed(uint Mode, uint Owner, uint Pet);

		// mode-1 remove/update: sent on dismiss alongside the n3ToClientQuit despawn.
		public static byte[] BuildRemove(uint owner, uint pet)
		{
			return new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(owner)
				.WriteByte(Env)
				.WriteUInt32(1) // mode 1 = remove/update
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(pet)
				.ToArray();
		}
	}
}
