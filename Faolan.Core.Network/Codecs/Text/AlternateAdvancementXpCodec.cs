namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     The on-screen line retail shows every time a character earns alternate-advancement xp - "you gained N AA
	///     experience". A string codec, framed by <see cref="FormatFeedbackCodec" /> like the banners next door.
	/// </summary>
	/// <remarks>
	///     Retail announces every grant and the amount is stated verbatim in the first <c>parameter</c>. The
	///     <c>AAPvEXP</c> (1000007) StatIIR push in the same frame moves by exactly that much on all 14 kill-grants in the
	///     corpus, which is what ties the announcement to the pool. See <c>--report aa-xp</c> and
	///     docs/re/alternate-advancement-perks.md.
	///     <para>
	///         The <c>key</c> is a client-validated content hash: harvested rather than derived, and constant per token
	///         across every captured record (19 of them, three different characters, four play days). The two forms differ
	///         only in whether a bonus percent rides along - a quest turn-in through a <c>PvEAAXPModifier</c> sends the
	///         twin with the modifier as its second parameter (Hakotep's daily paid 35 at 30%, and 35 = 27 x 1.3).
	///     </para>
	///     <para>
	///         There is no PvP form here. <c>AlternateAdvancementPvPXpGranted</c> exists in the client, but no capture
	///         carries one, so its key hash is unknown and cannot be computed.
	///     </para>
	/// </remarks>
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

	/// <summary>
	///     An AA xp grant, as the value <see cref="FormatFeedbackCodec" /> carries. <c>BonusPercent</c> is null on a plain
	///     grant - a kill - and set when the amount has been through a per-character modifier stat.
	/// </summary>
	/// <remarks>
	///     The framing is the message's own: all 19 captured records carry typeFlags 0x4200000B and footer 0, with no
	///     subject entity. That is the buff and attribute line framing with a different low byte, not the FIFO banner's.
	/// </remarks>
	public sealed record AlternateAdvancementXpGranted(uint Amount, int? BonusPercent = null) : FeedbackPayload
	{
		public override uint TypeFlags => 0x4200000B;
		public override uint Footer => 0;
	}
}
