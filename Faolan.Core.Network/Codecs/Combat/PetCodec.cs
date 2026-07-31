using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// PetIIR (0x1F4CB0F7) - the server packet that binds a summoned creature to its OWNER, i.e. tells the client
	// "this creature is YOUR pet" (so the pet UI + QueuePetCommand 0x205b become active). Decoded byte-exact from
	// NecroL80_..._casting-with-chat-notes.pcapng (docs/subsystems/summon-pet-decode.md, frames 470/679/919 assign, 1167 remove).
	//
	//   [subop][class][owner][u8 env=0][u32 mode] ; mode in {0,1} -> [class][pet] ; mode==0 additionally -> [f1][f2][f3]
	//
	// mode 0 = ASSIGN (on summon); mode 1 = REMOVE/update (on dismiss). The (f1,f2,f3) triple is the pet KIND:
	// the capture shows a COMBAT pet = (0,2,2) and a SOCIAL pet = (5,0,1). PetIIR_c::ReadSubClass only reads the two
	// handles for mode 0/1, so a higher mode is a decode failure rather than a shorter record.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class PetCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.PetIIR;

		public const uint ModeAssign = 0;
		public const uint ModeRemove = 1;

		// (f1,f2,f3) kind triples observed in the capture.
		public static readonly (uint F1, uint F2, uint F3) CombatKind = (0, 2, 2);
		public static readonly (uint F1, uint F2, uint F3) SocialKind = (5, 0, 1);

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(PetIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static PetIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PetCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static PetIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "PetIIR subop");
			r.ExpectU32Be(EntityClass.Character, "owner class");
			var owner = r.ReadU32Be();
			var env = r.ReadU8();
			var mode = r.ReadU32Be();
			if (mode > ModeRemove)
				throw CodecParseException.Unexpected(nameof(PetCodec), r.Position - sizeof(uint), "mode 0 (assign) or 1 (remove)", $"{mode}");

			r.ExpectU32Be(EntityClass.Character, "pet class");
			var pet = r.ReadU32Be();
			if (mode != ModeAssign)
				return new PetIIR(owner, env, mode, pet, (0, 0, 0));

			return new PetIIR(owner, env, mode, pet, (r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be()));
		}

		internal static void Write(FaolanStream s, PetIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Owner).WriteByte(v.Env).WriteUInt32(v.Mode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Pet);
			if (v.Mode == ModeAssign)
				s.WriteUInt32(v.Kind.F1).WriteUInt32(v.Kind.F2).WriteUInt32(v.Kind.F3);
		}
	}

	// PetIIR (0x1F4CB0F7). `Kind` is only on the wire for `ModeAssign`.
	public sealed record PetIIR(uint Owner, byte Env, uint Mode, uint Pet, (uint F1, uint F2, uint F3) Kind);

	// The two owner-binding forms, as VALUE factories.
	public static class PetBindings
	{
		// mode-0 owner-assign: binds `pet` to `owner` with a kind triple (combat/social).
		public static PetIIR Assign(uint owner, uint pet, (uint F1, uint F2, uint F3) kind)
		{
			return new PetIIR(owner, 0x00, PetCodec.ModeAssign, pet, kind);
		}

		// mode-1 remove/update: sent on dismiss alongside the n3ToClientQuit despawn.
		public static PetIIR Remove(uint owner, uint pet)
		{
			return new PetIIR(owner, 0x00, PetCodec.ModeRemove, pet, (0, 0, 0));
		}
	}
}
