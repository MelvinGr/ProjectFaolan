namespace Faolan.Core.Network.Codecs.Text
{
	// The FIELDLESS feedback strings: captured `remoteformat` literals a FormatFeedbackIIR carries verbatim, with
	// nothing substituted into them. They are CONSTANTS, not codecs - there is no value to encode, so a Build(T)
	// would take a parameterless record purely to satisfy a signature. The feedback messages that do carry fields
	// each get their own rule-1a string codec next door (AttributeChangedCodec, AreaNameBannerCodec,
	// RespawnPointDiscoveredCodec).
	//
	// ⚠️ Both `key`s are client-validated content hashes harvested from the capture - they cannot be derived or
	// edited, which is exactly why these two messages have no fields.
	public static class FeedbackXml
	{
		// The "you have lost the grace" on-screen line retail shows the instant a player LOSES the Shield of the Risen
		// protection (an aggressive action), sent right before the buff-removal twin (NecroL80_..._conans-castle-exit
		// rec#508: LostGraceEffect feedback -> 0x37f38c01 shield remove, while the aggro-status stream shows the player
		// in combat). NOT sent on a playfield swap of the shield (rec#206/#1750 swap the 1h<->10h with no
		// LostGraceEffect). Framed with FormatFeedbackCodec.EffectLineTypeFlags.
		public const string LostGraceEffect = "<remoteformat token=\"LostGraceEffect\" category=\"100\" key=\"RO5SQlZn.h/DvvaH&#36;WGu\" knubot=\"0\"  ></remoteformat>";

		// The "trade completed" toast both parties get the instant a player trade commits (#101), immediately after the
		// empty 0x206D. Verbatim from login_2-clients_group-trade-friend f1335/f1337.
		public const string TradeCompletedSuccessfully = "<remoteformat token=\"TradeCompletedSuccessfully\" category=\"100\" key=\"O?&#36;+s,!?Ry4)b+&#39;?h(_n\" knubot=\"0\"  ></remoteformat>";
	}
}
