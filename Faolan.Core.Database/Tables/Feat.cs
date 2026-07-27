using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// One entry of the client's FEAT catalog - the talent trees (Gitea #251). Keyed by its RDB template id.
	//
	// ⚠️ This is NOT the id space the WIRE uses. The player spawn's unlocked list (CharacterFeat) and the FeatIIR
	// train handshake carry a different space: 0 of the 56 captured CharacterFeat ids appear here, and the
	// featBase*100+rank decomposition does not bridge them either. It IS the space the rest of the client data uses
	// (45 of the 482 Combo.FeatId values resolve here), so what is missing is the wire->catalog mapping. Until that
	// exists, this table is a catalog to read, not something to join a character against.
	//
	// Source: RDB type 1000020, the PLAINTEXT records (magic "TB", 0x000F4254). That type holds items and feats
	// together and the items are the TEA-encrypted ~79%, so the plaintext share is essentially the feat catalog -
	// 15,834 of 16,784 named plaintext records carry the feat Flags value. No key crack and no capture needed for
	// these; the encrypted remainder stays blocked (see the issue).
	//
	// Tree/Group/BaseName/Rank are PARSED FROM THE NAME, which is how the client's own data encodes them
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
