namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     Captured <c>remoteformat</c> literals a ChatTextIIR carries verbatim - the chat-window twin of
	///     <see cref="FeedbackXml" />, which holds the FormatFeedbackIIR ones. They are constants rather than codecs:
	///     there is no value to encode.
	/// </summary>
	/// <remarks>
	///     Every <c>key</c> is the string's LDB decryption key (<c>LdbKey</c>), so it cannot be derived, invented or
	///     edited - the client decrypts the localized text with it. That is also why a parameterised message can only be
	///     reproduced for the exact parameters a capture recorded: substituting a different buff or actor means
	///     substituting an LDB id with no key, and a wrong key renders blank.
	/// </remarks>
	public static class ChatTextXml
	{
		// The refusal retail sends when a mounted player tries to spellweave: LDB 100/562 is
		// `'%s must not have %s running.'`, parameterised with `CriteriaYou` (130) = "You" and 55200/700 = the mount buff.
		// The gate is therefore stated against an active buff rather than against "being mounted" as such - the message
		// names the buff that blocks the action.
		//
		// Verbatim from demol80-demonologist_login_gameplay_annotated_2 frames 2652 and 2704, both answering a
		// PerformSpecialAction from the saddle.
		// [V2: demol80-demonologist_login_gameplay_annotated_2.tshark.json frame 2704]
		public const string MustNotHaveMountRunning =
			"<remoteformat id=\"562\" category=\"100\" key=\"6segSK/UI?Kc`Tzg.tio\" knubot=\"0\"  >" +
			"<parameter value=\"7;&lt;localized token=&quot;CriteriaYou&quot; category=&quot;130&quot; flags=&quot;&quot; key=&quot;^4L2&amp;#36;dU5AGjcTX_xJ[*Q&quot; &gt;\" />" +
			"<parameter value=\"7;&lt;localized id=&quot;700&quot; category=&quot;55200&quot; flags=&quot;&quot; key=&quot;MpRf.Cx.%^MGdFXT&amp;quot;9X(&quot; &gt;\" />" +
			"</remoteformat>";
	}
}
