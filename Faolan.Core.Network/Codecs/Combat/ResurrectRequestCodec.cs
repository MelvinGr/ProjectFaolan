using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// s2c DispatchResurrectRequest (0x2014) - "someone is offering to resurrect you". It is an OFFER, not the
	// resurrection: the client holds it and answers on the GameCharAgent tier, with `ResurrectMe(resurrectSpellID)`
	// to accept or `CancelResurrectionRequest(resurrectRequesterID, resurrectSpellID)` to decline - which is why the
	// spell id here is what comes back, and why it needs to resolve in nothing for the exchange to work.
	//
	//   [ID32 resurrector][u32 spellTemplate][u8 castSpellOnResurrect]
	//
	// ⭐ The three field NAMES are the client's own (#890): `GameCharInterfaceDispatch_t::DispatchResurrectRequest`
	// (`FUN_004c0160`) reads them in order and asserts "stream bad after 'i_cResurrectorID'", "after
	// 'i_nSpellTemplate'", "after 'i_castSpellOnResurrect'" - two u32 reads for the ID32, one for the template, one
	// byte for the flag, then a virtual call at slot 0x64 == (0x18 + 1) * 4, the interface's own slot rule.
	//
	// ⚠️ The corpus carries exactly ONE of these, in `NecroL80_..._annotated_9`: a SELF-resurrection, where the
	// resurrector is the player themselves. It is sent the instant the Self-Resurrection Potion is USED - while alive
	// - because that is what the potion does: the item's tooltip says it "provides the imbiber with an option to
	// resurrect themselves at the location of their death... The option to self-resurrect will persist until it is
	// used." So the offer is armed at use time and answered at death. Nothing about the DEATH side of the exchange is
	// captured, and a player-to-player resurrect (where the resurrector is somebody else) is not captured at all.
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

	// `ResurrectorId` is the character offering the resurrection - the player themselves for a self-resurrection.
	// `SpellTemplate` is the token the answer echoes back, and `CastSpellOnResurrect` says whether that spell is cast
	// when the offer is taken (1 in the captured request, and 1 in the field the client's own effect carries).
	public sealed record ResurrectRequest(uint ResurrectorId, uint SpellTemplate, bool CastSpellOnResurrect);
}
