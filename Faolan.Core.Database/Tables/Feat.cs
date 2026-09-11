using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// One entry of the client's feat catalog - the talent trees. Keyed by its RDB template id.
	//
	// This is not the id space the wire uses. The player spawn's unlocked list (CharacterFeat) and the FeatIIR
	// train handshake carry a different space: 0 of the 56 captured CharacterFeat ids appear here, and the
	// featBase*100+rank decomposition does not bridge them either. It is the space the rest of the client data uses
	// (45 of the 482 Combo.FeatId values resolve here), so what is missing is the wire->catalog mapping. Until that
	// exists, this table is a catalog to read, not something to join a character against.
	//
	// Source: RDB type 1000020, the plaintext records (magic "TB", 0x000F4254). That type holds items and feats
	// together and the items are the TEA-encrypted ~79%, so the plaintext share is essentially the feat catalog -
	// 15,834 of 16,784 named plaintext records carry the feat Flags value. No key crack and no capture needed for
	// these; the encrypted remainder stays blocked (see the issue).
	//
	// Tree/Group/BaseName/Rank are parsed from the name, which is how the client's own data encodes them
	// ("Soldier: Optional Training: Thrown Weapons 5"). They are a convenience over Name, not independent facts - if
	// a name does not fit the pattern the columns stay null/0 and Name is still complete. TemplateCategory and Flags
	// are the raw discriminating words, kept so the feat/item split can be re-derived without re-reading the RDB.
	public class Feat : BaseEntity
	{
		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// The feat's tooltip text, where the record carries one ("Reduces the casting time of Hellfire Breath, but
		// increases the re-use time."). Only the u16-length record shape has it; the u32 shape carries a name alone,
		// so an empty string means the record had none rather than that it was skipped.
		[MaxLength(1024)]
		public string Description { get; set; } = "";

		// The record's word at offset 44: 0x10000005 on the feat records, 0x200 on the item templates. This is the
		// discriminator the importer selects on.
		public uint Flags { get; set; }

		// The record's word at offset 28 - a category id (7063 on the feats checked, 9081/10090 on items). Corroborates
		// Flags rather than being used to select, since fewer distinct values were verified.
		public uint TemplateCategory { get; set; }

		/// <summary>
		///     The <see cref="TemplateCategory" /> of the feats a character gets automatically, as opposed to
		///     <c>8072</c>, the feat tree it spends points in. It is what separates the two spell-grant mechanisms
		/// and so what <c>DatabaseRepository.GetClassSpellsUnlockedBetween</c> gates the level path on.
		/// </summary>
		/// <remarks>
		///     Measured over the 946 feats that grant a spell (<see cref="FeatSpellGrant" />), where the split is
		///     exact: the 271 in this category resolve to 24 names and no others - <c>Spell Unlocking &lt;lo&gt;-
		///     &lt;hi&gt;</c>, <c>&lt;Class&gt; Class Feat NNN</c> and <c>Pet Mastery</c> - while the 675 in 8072
		///     carry 238 names, every one of them of the form <c>Spell - &lt;the spell's own name&gt;</c>, and not one
		///     of the 24. A feat named for the single spell it teaches is a per-spell choice; a feat named for a level
		///     band is not.
		///     <para>
		///         The captures settle which of the two the level path is. Gating on this category reproduces the
		///         captured spellbooks with <b>zero</b> false positives on every character in the corpus - the level-80
		///         Necromancer 88 offered / <b>88</b> known, the level-80 Demonologist 95 / <b>95</b>, the level-24
		///         Herald 4 / <b>4</b>. Gating on "any feat at all" instead offers the Necromancer 111 for its 109, and
		///         the 20 extra are exactly 8072 rows: <c>Ritual of the Lich</c>, <c>Despoil the Soul</c>,
		///         <c>One with Death</c> - tree spells that player did not spec into. The four the same player did
		///         spec into are 8072 too, so the difference between the two populations is a point spend and not a
		///         property of the spell.
		///     </para>
		///     <para>
		///         The category is used, rather than the 24 names, because it is a client field and because it is
		///         strictly better: <c>Pet Mastery</c> carries it and matches no name pattern, and it is what teaches
		///         the captured Necromancer its <c>Ice Shackle</c>. A name rule would have dropped that.
		///     </para>
		///     <para>
		///         This does not establish what 7063 means in general - 16,051 feats carry it, most of them armour
		///         and equip feats, and <see cref="TemplateCategory" />'s own remark is deliberately cautious. The
		///         claim is scoped to the feats that grant a spell, which is the only population it is read over.
		///     </para>
		/// </remarks>
		public const uint AutomaticUnlockCategory = 7063;

		// The middle segment of a three-part name ("Optional Training", "Core Training"), null when there isn't one.
		[MaxLength(64)]
		public string? Group { get; set; }

		// The trailing rank in the name - "... Thrown Weapons 5" or "... 2HE (3)". 0 when the name carries none, which
		// is a real state (an unranked feat), not a missing value.
		public int Rank { get; set; }

		// The leading segment of a multi-part name: the class or tree it belongs to ("Soldier", "Guardian",
		// "Core Training", "Equip"). Null for a bare name like "Light Blade Training 1".
		[MaxLength(64)]
		public string? Tree { get; set; }

		// The name with the tree/group prefixes and the rank suffix stripped - what the tree UI would show as the
		// feat itself ("Thrown Weapons"). Equal to Name when nothing was strippable.
		[MaxLength(128)]
		public string BaseName { get; set; } = null!;
	}
}
