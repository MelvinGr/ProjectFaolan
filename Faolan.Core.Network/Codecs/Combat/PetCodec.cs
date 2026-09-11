using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     PetIIR - binds a summoned creature to its owner, telling the client that this creature is its pet, which is what
	///     activates the pet UI and QueuePetCommand. Layout:
	///     <c>[subop][class][owner][u8 env=0][u32 mode]</c>, then <c>[class][pet]</c> for modes 0 and 1, and for mode 0 also
	///     <c>[f1][f2][f3]</c>.
	/// </summary>
	/// <remarks>
	///     Mode 0 assigns, on summon; mode 1 removes or updates, on dismiss. The (f1, f2, f3) triple is the pet kind - see
	///     <see cref="PetBindings" />. PetIIR_c::ReadSubClass only reads the two handles for modes 0 and 1, so a higher mode
	///     is a decode failure rather than a shorter record. Decoded byte-exact from
	///     NecroL80_..._casting-with-chat-notes.pcapng (frames 470/679/919 assign, 1167 remove); see
	///     docs/subsystems/summon-pet-decode.md.
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class PetCodec
	{
		public const uint Opcode = (uint)IirTypes.PetIIR;

		public const uint ModeAssign = 0;
		public const uint ModeRemove = 1;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The PetIIR body. <c>Kind</c> is only on the wire for <see cref="PetCodec.ModeAssign" />.</summary>
	public sealed record PetIIR(uint Owner, byte Env, uint Mode, uint Pet, (uint F1, uint F2, uint F3) Kind);

	/// <summary>
	///     The two owner-binding forms, as value factories, and the kind triples they take. They live beside the codec
	///     rather than on it because a codec's public surface is Build and Parse and nothing else - the same arrangement
	///     MoveTaskNodes has beside MoveTaskCodec.
	/// </summary>
	public static class PetBindings
	{
		/// <summary>The social pet triple: no point cost, and <c>f1 = 5</c> is what marks it social.</summary>
		public static readonly (uint F1, uint F2, uint F3) SocialKind = (5, 0, 1);

		/// <summary>
		///     The combat pet triple for a pet costing <paramref name="pointCost" /> pet points.
		/// </summary>
		/// <remarks>
		///     <c>f2</c> is the point cost, and must stay a parameter: pet-point accounting is client-side - stat
		///     <c>0x1C8 PetPoints</c> appears in no update across all 59 necromancer captures - so this triple is the only
		///     place the server ever states a cost, and a constant here makes every pet cost the same in the client's
		///     pet-point bar.
		///     <para />
		///     Measured over the corpus by <c>--report pet-cost</c>: 62 owner-assigns resolve to a creature whose summon
		///     spell states a cost, and <c>f2</c> equals that cost on 61 of them.
		///     <list type="table">
		///         <item><description><c>(0,1,1)</c> x14 - every one a cost-1 pet (Blighted One, Corruptor, Harvester, Life-stealer, Mutilator, Necrotic Bomb)</description></item>
		///         <item><description><c>(0,2,2)</c> x12 - every one a cost-2 pet (Arcanist, Magus, Reaper)</description></item>
		///         <item><description><c>(0,4,2)</c> x36 - 35 cost-4 pets (Archmagus, Blood Arcanist) and one exception</description></item>
		///     </list>
		///     The exception is Demon Avenger, which retail assigns at 4 while the spell that names it states 1. That is an
		///     artefact of joining creature name to spell: several spells can name one creature, and the server passes the
		///     cost of the spell it actually cast rather than using that join.
		///     <para />
		///     <c>f3</c> is fitted. It is 1 on the cost-1 pets and 2 on both the cost-2 and the cost-4 ones, so over the three
		///     cost classes the corpus holds it reads as a coarse tier rather than a second copy of the cost - but 2 and 4
		///     sharing a value is the only evidence separating those readings, and a cost-3 pet would decide it. A social pet
		///     carries 1 here too, which fits the tier reading.
		/// </remarks>
		public static (uint F1, uint F2, uint F3) CombatKind(uint pointCost)
		{
			return (0, pointCost, pointCost <= 1 ? 1u : 2u);
		}


		/// <summary>Mode-0 owner-assign: binds the pet to its owner with a combat or social kind triple.</summary>
		public static PetIIR Assign(uint owner, uint pet, (uint F1, uint F2, uint F3) kind)
		{
			return new PetIIR(owner, 0x00, PetCodec.ModeAssign, pet, kind);
		}

		/// <summary>Mode-1 remove: sent on dismiss alongside the n3ToClientQuit despawn.</summary>
		public static PetIIR Remove(uint owner, uint pet)
		{
			return new PetIIR(owner, 0x00, PetCodec.ModeRemove, pet, (0, 0, 0));
		}
	}
}
