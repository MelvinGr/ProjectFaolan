using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One entry of the client's character-customization registry (RDB type 1000076 id 0): every selectable
	// appearance target - the face/body morph sliders and the choosable resources (*hairmesh_N / *beardmesh_N /
	// *piercingmesh_N / *texscript_N for hair/eye/skin color, tattoos, scars, face paint). Imported by
	// Faolan.Importer --rdb. The spawn scaffold's weighted (1.0) entry-table records reference these by Id
	// verbatim - the id leads each RDB record and is the wire tag (verified: faolanx's 7 weighted entries
	// resolve 7/7 against his create-editor resource refs, and strie's captured spawn resolves to exactly one
	// entry per category). It is not `id - 1`: that was a compensation for a parser that read each record's id
	// off the end, i.e. the next record's, which cancels only while ids ascend by one. See
	// docs/re/player-spawn-generation.md "Weighted attributes".
	//
	// Name is unique, and that is load-bearing rather than cosmetic: the registry is consumed as a by-name
	// dictionary (GetMorphTargetRegistry -> ToDictionaryAsync(t => t.Name)), which throws on a repeat, and
	// CharacterBodyResource.Ref addresses rows by that name. The index makes a duplicate impossible to import
	// instead of a crash on the next spawn. There is deliberately no FK from CharacterBodyResource.Ref to it:
	// the importer delete+reinserts this whole table (RdbMorphTargetImporter), which a restricting FK would
	// block and a cascading one would answer by wiping every character's appearance.
	[Index(nameof(Name), IsUnique = true)]
	public class MorphTarget : BaseEntity
	{
		// The registry's category: 0 face sliders, 1 body sliders, 2 face paint (never emitted in spawns),
		// 3 hairmesh, 4 beardmesh, 5 piercingmesh, 6 hair color, 7 eye color, 8 skin color, 9/10 tattoos+scars.
		public byte Category { get; set; }

		// The registry weight (1.0 for resources; sliders carry 0.1/0.3/0.5).
		public float Weight { get; set; }
		// Id (from BaseEntity) = the wire tag = the id that leads the RDB record, verbatim.

		// The registry name: a slider name ("arm", "02_BrowLow") or a resource ref ("*texscript_1108153").
		[MaxLength(128)]
		public string Name { get; set; } = null!;
	}
}
