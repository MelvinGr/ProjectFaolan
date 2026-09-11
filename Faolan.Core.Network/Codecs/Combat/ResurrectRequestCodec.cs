using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     s2c DispatchResurrectRequest - "someone is offering to resurrect you". It is an offer, not the resurrection: the
	///     client holds it and answers on the GameCharAgent tier with <c>ResurrectMe(resurrectSpellID)</c> to accept or
	///     <c>CancelResurrectionRequest(resurrectRequesterID, resurrectSpellID)</c> to decline, so the spell id here is the
	///     token that comes back and need not resolve to anything for the exchange to work. Body:
	///     <c>[ID32 resurrector][u32 spellTemplate][u8 castSpellOnResurrect]</c>.
	/// </summary>
	/// <remarks>
	///     The three field names are the client's own: GameCharInterfaceDispatch_t::DispatchResurrectRequest (FUN_004c0160)
	///     reads them in order and asserts "stream bad after 'i_cResurrectorID'", "after 'i_nSpellTemplate'" and "after
	///     'i_castSpellOnResurrect'" - two u32 reads for the ID32, one for the template, one byte for the flag, then a
	///     virtual call at slot 0x64.
	///     <para>
	///         The corpus carries exactly one of these, in NecroL80_..._annotated_9: a self-resurrection, where the resurrector
	///         is the player. It is sent the instant the Self-Resurrection Potion is used, while alive, because that is what
	///         the potion does - its tooltip says the option "will persist until it is used" - so the offer is armed at use
	///         time and answered at death. The death side of the exchange is not captured, nor is a player-to-player
	///         resurrect.
	///     </para>
	/// </remarks>
	public static class ResurrectRequestCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchResurrectRequest;

		public static byte[] Build(ResurrectRequest value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(value.ResurrectorId).WriteUInt32(value.SpellTemplate).WriteByte(value.CastSpellOnResurrect);

			return s.ToArray();
		}

		public static byte[] Build(uint resurrectorId, uint spellTemplate, bool castSpellOnResurrect = true)
		{
			return Build(new ResurrectRequest(resurrectorId, spellTemplate, castSpellOnResurrect));
		}

		public static ResurrectRequest Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ResurrectRequestCodec));
			r.ExpectU32Be(EntityClass.Character, "the resurrector's entity class");
			var value = new ResurrectRequest(r.ReadU32Be(), r.ReadU32Be(), r.ReadU8() != 0);
			r.EnsureAtEnd();

			return value;
		}
	}

	/// <summary>
	///     The DispatchResurrectRequest body. <c>ResurrectorId</c> is the character offering the resurrection - the player
	///     themselves for a self-resurrection. <c>SpellTemplate</c> is the token the answer echoes back, and
	///     <c>CastSpellOnResurrect</c> says whether that spell is cast when the offer is taken; it is 1 in the captured
	///     request and in the field the client's own effect carries.
	/// </summary>
	public sealed record ResurrectRequest(uint ResurrectorId, uint SpellTemplate, bool CastSpellOnResurrect);
}
