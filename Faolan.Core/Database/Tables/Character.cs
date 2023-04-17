using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(Name), IsUnique = true)]
	public class Character : Base3DEntity
	{
		[ForeignKey(nameof(AccountId))]
		public virtual Account Account { get; set; }

		public virtual CharacterBody Body { get; set; }
		public virtual CharacterEquipment Equipment { get; set; }

		//public virtual CharacterMaxStats Stats { get; set; }
		[NotMapped]
		public virtual CharacterMaxStats MaxStats { get; set; }

		[ForeignKey(nameof(MapId))]
		public virtual Map Map { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; }

		public Class Class { get; set; }
		public DateTimeOffset CreationDate { get; set; }

		public DateTimeOffset? LastConnection { get; set; }
		public Race Race { get; set; }
		public Sex Sex { get; set; }

		[MaxLength(32)]
		public string AuthChallenge { get; set; }

		[MaxLength(4)]
		public string Language { get; set; }

		[MaxLength(45)] // INET6_ADDRSTRLEN 46
		public string LastIpAddress { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }

		public uint AccountId { get; set; }
		public uint Experience { get; set; }

		public uint? MapId { get; set; }

		public uint? RealmId { get; set; }
		public ushort Level { get; set; }

		//[NotMapped]
		//public string LastConnectionRealmString => DateTimeOffset.FromUnixTimeSeconds(LastConnection).ToString("MM/dd/yyyy HH:mm:ss");

		public override string ToString()
		{
			return $"{base.ToString()}: {Name}";
		}
	}

	public class CharacterMaxStats : BaseEntity
	{
		public virtual Character Character { get; set; }
		public uint CharacterId { get; set; }

		public uint Health { get; set; }
		public uint Mana { get; set; }
		public uint Stamina { get; set; }
	}
}
