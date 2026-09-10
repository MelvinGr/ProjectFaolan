namespace Faolan.Core.Network.Codecs.Text
{
	// The on-screen line retail shows every time a character earns ALTERNATE-ADVANCEMENT xp (#150) - "you gained N
	// AA experience". A rule-1a STRING codec (#530), framed by FormatFeedbackCodec like the banners next door.
	//
	// ⭐ This message is why the AA accrual rate was never capture-blocked: retail ANNOUNCES every grant, and the
	// amount is stated verbatim in the first `<parameter>`. The `AAPvEXP` (1000007) StatIIR push in the same frame
	// moves by exactly that much on all 14 kill-grants in the corpus, which is what ties the announcement to the
	// pool. See `--report aa-xp` and docs/re/alternate-advancement-perks.md.
	//
	// ⚠️ The `key` is a client-validated content hash: it is harvested, not derived, and it is CONSTANT per token
	// across every captured record (19 of them, three different characters, four play days). The two forms differ
	// only in whether a bonus PERCENT rides along - a quest turn-in through a `PvEAAXPModifier` sends the twin with
	// the modifier as its second parameter (Hakotep's daily paid `(35, 30%)`, and 35 = 27 x 1.3).
	//
	// ⛔ There is no PvP form here. `AlternateAdvancementPvPXpGranted` exists in the client, but no capture carries
	// one, so its key hash is unknown and cannot be computed - building one would be inventing a packet.
	public static class AlternateAdvancementXpCodec
	{
		public static string Build(AlternateAdvancementXpGranted value)
		{
			return value.BonusPercent is { } bonus
				? "<remoteformat token=\"AlternateAdvancementPvEXpGrantedWithBonusPercent\" category=\"100\" "
				  + $"key=\"bAMpex`PIz&#92;yxtM7wU^B\" knubot=\"0\"  ><parameter value=\"2;{value.Amount}\" /><parameter value=\"1;{bonus}\" /></remoteformat>"
				: "<remoteformat token=\"AlternateAdvancementPvEXpGranted\" category=\"100\" "
				  + $"key=\"mgIKCns38CFWy#)9Xb@e\" knubot=\"0\"  ><parameter value=\"2;{value.Amount}\" /></remoteformat>";
		}

		public static string Build(uint amount)
		{
			return Build(new AlternateAdvancementXpGranted(amount));
		}
	}

	// An AA xp grant, as the thing FormatFeedbackCodec carries. `BonusPercent` is null on a plain grant - a kill - and
	// set when the amount has been through a per-character modifier stat.
	//
	// The framing is the message's own (#539): all 19 captured records carry typeFlags 0x4200000B and footer 0, with
	// no subject entity. That is the buff/attribute LINE framing with a different low byte, not the FIFO banner's.
	public sealed record AlternateAdvancementXpGranted(uint Amount, int? BonusPercent = null) : FeedbackPayload
	{
		public override uint TypeFlags => 0x4200000B;
		public override uint Footer => 0;
	}
}
