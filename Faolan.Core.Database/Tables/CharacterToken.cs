using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One of a character's parallel token WALLETS (Gitea #150) - the running totals
	///     <c>DispatchUpdateTokenAmount</c> (s2c 0x204D) carries for a single <see cref="TokenPool" />.
	/// </summary>
	/// <remarks>
	///     ⚠️ <b>The wire carries lifetime totals, not a balance</b>, and so does this row: the client subtracts
	///     <see cref="Spent" /> from <see cref="Gained" /> itself. Storing a single balance would make every push a
	///     lie about one of the two halves.
	///     <para />
	///     A row per pool the character has ever touched, rather than 29 rows per character: retail only ever states
	///     ONE pool per message and only when it moves (ten pushes in the whole corpus, each a single pool), so an
	///     absent row and a 0/0 row mean the same thing to the client.
	///     <para />
	///     The natural composite key is the point (see the model conventions in CLAUDE.md): a wallet is identified by
	///     whose it is and which pool it is, and a surrogate would be renumbered by anything that rebuilt the table.
	/// </remarks>
	[PrimaryKey(nameof(CharacterId), nameof(Pool))]
	public class CharacterToken
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// Which wallet - the client's own token id, the first byte of DispatchUpdateTokenAmount.
		[ConstrainToEnumValues]
		public TokenPool Pool { get; set; }

		// Lifetime totals, in the wire's own width (the message carries two u64s).
		public ulong Gained { get; set; }
		public ulong Spent { get; set; }
	}
}
