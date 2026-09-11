namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     The fieldless feedback strings: captured <c>remoteformat</c> literals a FormatFeedbackIIR carries verbatim,
	///     with nothing substituted into them.
	/// </summary>
	/// <remarks>
	///     They are constants rather than codecs - there is no value to encode, so a <c>Build(T)</c> would take a
	///     parameterless record purely to satisfy a signature. The feedback messages that do carry fields each get their
	///     own string codec next door (<see cref="AttributeChangedCodec" />, <see cref="AreaNameBannerCodec" />,
	///     <see cref="RespawnPointDiscoveredCodec" />).
	///     <para>
	///         Both <c>key</c> values are client-validated content hashes harvested from the capture: they cannot be
	///         derived or edited, which is exactly why these two messages have no fields.
	///     </para>
	/// </remarks>
	public static class FeedbackXml
	{
		// The "you have lost the grace" on-screen line retail shows the instant a player loses the Shield of the Risen
		// protection (an aggressive action), sent right before the buff-removal twin (NecroL80_..._conans-castle-exit
		// rec#508: LostGraceEffect feedback then the shield remove, while the aggro-status stream shows the player in
		// combat). It is not sent on a playfield swap of the shield. Framed with FormatFeedbackCodec.EffectLineTypeFlags.
		public const string LostGraceEffect = "<remoteformat token=\"LostGraceEffect\" category=\"100\" key=\"RO5SQlZn.h/DvvaH&#36;WGu\" knubot=\"0\"  ></remoteformat>";

		// The "trade completed" toast both parties get the instant a player trade commits, immediately after the empty
		// partner-cash update. Verbatim from login_2-clients_group-trade-friend f1335/f1337.
		public const string TradeCompletedSuccessfully = "<remoteformat token=\"TradeCompletedSuccessfully\" category=\"100\" key=\"O?&#36;+s,!?Ry4)b+&#39;?h(_n\" knubot=\"0\"  ></remoteformat>";
	}
}
