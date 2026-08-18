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
		public const uint Opcode = (uint)IirTypes.PetIIR;

		public const uint ModeAssign = 0;
		public const uint ModeRemove = 1;

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

	// The two owner-binding forms, as VALUE factories - and the KIND triples they take.
	//
	// ⚠️ These live here rather than on `PetCodec` because a codec's public surface is `Build` and `Parse` and
	// nothing else (CLAUDE.md's codec rule 1, gated by `CodecSurfaceConventionTests`). A named form of a value is
	// exactly what a value factory beside the codec is for, the same way `MoveTaskNodes.Path`/`.Patrol` sit beside
	// `MoveTaskCodec`.
	public static class PetBindings
	{
		/// <summary>The SOCIAL pet triple: no point cost, and <c>f1 = 5</c> is what marks it social.</summary>
		public static readonly (uint F1, uint F2, uint F3) SocialKind = (5, 0, 1);

		/// <summary>
		///     The COMBAT pet triple for a pet costing <paramref name="pointCost" /> pet points.
		/// </summary>
		/// <remarks>
		///     ⭐⭐ <b><c>f2</c> IS THE POINT COST</b> (#823). This was a constant <c>(0, 2, 2)</c>, generalised from
		///     the one combat pet in the decode capture - and that is precisely why every necromancer pet showed as
		///     costing 2 in the client's pet-point bar however expensive it was. The accounting is client-side (stat
		///     <c>0x1C8 PetPoints</c> appears in ZERO updates across all 59 necromancer captures), so this triple is
		///     the only place the server ever states a cost, and it was stating the same one every time.
		///     <para />
		///     Measured over the corpus by <c>--report pet-cost</c> - 62 owner-assigns resolved to a creature whose
		///     summon spell states a cost, and <c>f2</c> equals that cost on <b>61</b> of them:
		///     <list type="table">
		///         <item><description><c>(0,1,1)</c> ×14 — every one a cost-1 pet (Blighted One, Corruptor, Harvester, Life-stealer, Mutilator, Necrotic Bomb)</description></item>
		///         <item><description><c>(0,2,2)</c> ×12 — every one a cost-2 pet (Arcanist, Magus, Reaper)</description></item>
		///         <item><description><c>(0,4,2)</c> ×36 — 35 cost-4 pets (Archmagus, Blood Arcanist) and one exception</description></item>
		///     </list>
		///     The exception is <c>Demon Avenger</c>, which retail assigns at 4 while the spell that names it states
		///     1. That is our own name→spell join, not a counterexample: the cost is read off the summoning SPELL and
		///     several spells can name one creature. The server does not use that join - it passes the cost of the
		///     spell it actually cast.
		///     <para />
		///     ⚠️ <b><c>f3</c> is fitted and says so.</b> It is 1 on the cost-1 pets and 2 on both the cost-2 and the
		///     cost-4 ones, so over the three cost classes the corpus holds it reads as a coarse tier rather than a
		///     second copy of the cost - but 2 and 4 sharing a value is the only evidence separating those readings,
		///     and a cost-3 pet would decide it. A social pet carries 1 here too, which fits the tier reading.
		/// </remarks>
		public static (uint F1, uint F2, uint F3) CombatKind(uint pointCost)
		{
			return (0, pointCost, pointCost <= 1 ? 1u : 2u);
		}


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
