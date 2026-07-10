using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(Name), IsUnique = true)]
	public class Realm : BaseEntity
	{
		public virtual ICollection<Character> Characters { get; set; }

		[ConstrainToEnumValues]
		public RealmCountry Country { get; set; }

		[ConstrainToEnumValues]
		public RealmLoadStatus LoadStatus { get; set; }

		[ConstrainToEnumValues]
		public RealmOnlineStatus OnlineStatus { get; set; }

		[ConstrainToEnumValues]
		public RealmType Type { get; set; }

		[MaxLength(64)]
		public string IpAddress { get; set; }

		[MaxLength(128)]
		public string Name { get; set; }

		// The realm's GameServer (world) port - advertised to the client in the EnterWorld handoff (SendGameServer).
		public ushort Port { get; set; }

		// Multi-realm (process-per-realm, #324): chat + guild/team run IN the realm process, so their ports are
		// per-realm too. The login/router tier advertises these (with IpAddress) in the AgentServer/CsPlayerAgent
		// handoffs. Seeded to the historical single-process config ports so a one-realm deploy is byte-identical.
		public ushort AgentServerPort { get; set; }

		public ushort CsPlayerAgentPort { get; set; }
	}
}
