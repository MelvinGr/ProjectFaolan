using System.ComponentModel.DataAnnotations;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The character-creator options that are per SEX and not per race - the body morph ranges (`BodyNodes`), the face
	// morph ranges (`FaceNodes`) and the five voice samples. In CHARACTERCREATOR (RDB 1010700) these hang off the SEX
	// element, above the per-race sections, so there is exactly one set of each per sex.
	//
	// They used to live in AppearanceChoice, written once per race so that a single (sex, race) query returned
	// everything. That meant storing each definition FOUR times - 264 rows carrying 66 distinct ones - and it made the
	// table lie about its own shape: a `morph` row keyed by Race implies morphs vary by race, and they do not. The
	// per-race categories (hair, head, beard, piercings, the colours, the tattoos, and height, which IS read per race
	// even though its 92..100 happens to be uniform) stay in AppearanceChoice.
	//
	// Keyed by (Sex, Category, Sequence), the same "a choice IS its slot in one list" reasoning AppearanceChoice uses.
	[PrimaryKey(nameof(Sex), nameof(Category), nameof(Sequence))]
	public class AppearanceSexOption
	{
		[ConstrainToEnumValues]
		public Sex Sex { get; set; }

		public int Max { get; set; }

		// Slider bounds in CharacterBody signed-slider units, for the morph categories; 0 for a voice row.
		public int Min { get; set; }

		// Editor send order within the category.
		public int Sequence { get; set; }

		// morph | facemorph | voice
		[MaxLength(64)]
		public string Category { get; set; } = null!;

		// The morph NAME for a morph/facemorph row, or the voice `enum` for a voice row.
		[MaxLength(128)]
		public string Value { get; set; } = null!;

		// The voice sample's wav path (else null).
		[MaxLength(128)]
		public string? Name { get; set; }
	}
}
