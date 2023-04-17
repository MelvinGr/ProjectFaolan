using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(UserName), IsUnique = true)]
	public class Account : BaseEntity
	{
		public virtual ICollection<Character> Characters { get; set; }

		public AccountState State { get; set; }
		public AccountType Type { get; set; }

		// In-memory only (set per session from ClientInstance); never persisted, so no FK to Character.
		[NotMapped]
		public Character? Character { get; set; }

		public DateTime CreationDate { get; set; }
		public DateTime? LastConnection { get; set; }

		public int CreateCounter { get; set; }
		public int CreateState { get; set; }

		[MaxLength(32)]
		public string AuthChallenge { get; set; }

		[MaxLength(45)] // INET6_ADDRSTRLEN 46
		public string LastIpAddress { get; set; }

		[MaxLength(128)]
		public string Password { get; set; }

		[MaxLength(128)]
		public string UserName { get; set; }

		public uint AuthStatus { get; set; }

		// The id the client is currently using for this session: a real Character.Id during normal play, or
		// the temporary create-editor instance (0x021662C8) during character creation. Deliberately NOT a
		// foreign key - it must hold the temp instance without a constraint violation (so db.SaveChanges
		// works), and deleting a character must never cascade into deleting the account.
		public uint ClientInstance { get; set; }

		[NotMapped]
		public ulong LongId => 0x0000271200000000u + Id; // As used by the client

		/*public Account()
		{
		    CreationDate = DateTime.UtcNow;
		    AuthChallenge = Guid.NewGuid().ToString("N");
		}*/

		public override string ToString()
		{
			return $"{base.ToString()}: {UserName}";
		}
	}
}
