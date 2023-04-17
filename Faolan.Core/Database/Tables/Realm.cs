using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(Name), IsUnique = true)]
	public class Realm : BaseEntity
	{
		public virtual ICollection<Character> Characters { get; set; }

		public RealmCountry Country { get; set; }
		public RealmLoadStatus LoadStatus { get; set; }
		public RealmOnlineStatus OnlineStatus { get; set; }
		public RealmType Type { get; set; }

		[MaxLength(64)]
		public string IpAddress { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }

		public ushort Port { get; set; }
	}
}
