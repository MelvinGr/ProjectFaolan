namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     One selectable reward as it appears in the QuestRewardNotifyIIR offer list:
	///     <c>[u32 template][u32 template dup][u64 level][4B code][16B key][16B key dup][8B guid][8B guid dup][u32 1]</c>.
	/// </summary>
	/// <remarks>
	///     The layout is the ClaimEntry shape minus the leading index and the trailing charge/flag block, which follows
	///     from both being "here is an item you have not been given yet, render it from your own RDB".
	///     <para>
	///         The 16-byte key is the item's ProtoDecryptionKey (Item.Key1..4), and that is what pins the layout rather than
	///         a plausible alignment: all three captured entries' keys match the shipped Items rows exactly - 27655
	///         Acolyte's Dagger 684e0be9 beb853fe 041347d7 660cdf13, 3145335 Acolyte's Longsword 1fb3f747 5b6f1297 3dcb757f
	///         7fbc62b5, 3394679 Acolyte's Staff 4735febc dcb11519 ded73456 fac62d21. The client decrypts each option's
	///         template from its RDB the same way it does a claim, and a wrong key renders the row as "No name". The 8-byte
	///         guid is constant across the three entries, a per-response batch id, and key and guid are each emitted twice,
	///         the same mirror pair the claim list uses.
	///     </para>
	///     <para>
	///         Level was 5 on all three captured options and Code four zero bytes - a quest reward carries no redemption
	///         code, so the slot the claim list uses for one is empty. Both are carried verbatim rather than assumed
	///         constant. Decoded byte-exact from xxx_mage-necromancer_login_play3 frame 2864, quest 10928 "The Trainer of
	///         Mages", whose three options the player confirmed in-client.
	///     </para>
	/// </remarks>
	public sealed record QuestRewardEntry(uint ItemTemplateId, ulong Level, byte[] Code, byte[] DecryptionKey, byte[] BatchGuid, uint Trailing = 1)
	{
		public const int BatchGuidLength = 8;
		public const int CodeLength = 4;
		public const int KeyLength = 16;

		/// <summary>
		///     The wire size of one option: two template words, the level, the code, the mirrored key and guid pairs, and the
		///     trailing word.
		/// </summary>
		public const int WireLength = 4 + 4 + 8 + CodeLength + (KeyLength * 2) + (BatchGuidLength * 2) + 4;
	}
}
