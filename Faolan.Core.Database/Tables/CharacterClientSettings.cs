using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The two blobs the client authors for a character and uploads on its mid-session PlayerAgent socket
	// (UpdateCharacterSettings / UpdateCharacterPreview). The server never reads inside them - its whole job
	// is to hand each back verbatim: the settings at LoginCharacter (SendCharacterSettings) and the preview before the
	// character list (SendCharacterPreview), so a player's UI layout and select-screen pose survive a relog.
	//
	// Stored compressed, exactly as received. The checksum the client compares against
	// (SendCharacterSettingsCheckSumMap) is an MD5 over the compressed payload, and a deflate stream is not a function
	// of its contents - so re-encoding these bytes would change the checksum and make every cached client copy look
	// stale. Both columns are nullable because either half can exist without the other (the preview is uploaded on its
	// own after a look change).
	[PrimaryKey(nameof(CharacterId))]
	public class CharacterClientSettings
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// The per-character UI archive: Chat\Windows.xml, DockAreas\SideBar*.xml, Prefs_2.xml.
		public byte[]? Settings { get; set; }

		// The character-select preview archive: a single `preview.bin` appearance record.
		public byte[]? Preview { get; set; }
	}
}
