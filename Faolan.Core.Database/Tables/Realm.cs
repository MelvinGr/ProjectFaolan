using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(Name), IsUnique = true)]
	public class Realm : BaseEntity
	{
		public virtual ICollection<Character> Characters { get; set; } = [];

		[ConstrainToEnumValues]
		public RealmCountry Country { get; set; }

		[ConstrainToEnumValues]
		public RealmLoadStatus LoadStatus { get; set; }

		[ConstrainToEnumValues]
		public RealmOnlineStatus OnlineStatus { get; set; }

		[ConstrainToEnumValues]
		public RealmType Type { get; set; }

		[MaxLength(64)]
		public string IpAddress { get; set; } = null!;

		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// The u32 form the ChatServer / CsPlayerAgent handoffs carry (a fixed 4-byte field + u16 port, byte-exact
		// against retail's ChatServerConnectReady / CSServerConnectReady). The client is IPv4-only (#845: both of
		// its connect helpers are hard-wired AF_INET + gethostbyname), and that field has no room for anything
		// else - so a realm row holding a v6 literal is a configuration error, and this THROWS rather than
		// silently sending the first four bytes of the v6 address as if they were an IPv4 one (which is what
		// GetAddressBytes() would hand back).
		public uint IpAddressLong
		{
			get
			{
				var address = IPAddress.Parse(IpAddress);
				if (address.AddressFamily != AddressFamily.InterNetwork)
					throw new InvalidOperationException(
						$"Realm '{Name}' IpAddress '{IpAddress}' is {address.AddressFamily}; the chat/CS handoff carries a " +
						"u32 IPv4 address and the retail client is IPv4-only (#845)");
				return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
			}
		}

		// Multi-realm (process-per-realm, #324): chat + guild/team run IN the realm process, so their ports are
		// per-realm too. The login/router tier advertises these (with IpAddress) in the ChatServer/CsPlayerAgent
		// handoffs. Seeded to the historical single-process config ports so a one-realm deploy is byte-identical.
		public ushort ChatServerPort { get; set; }

		public ushort CsPlayerAgentPort { get; set; }

		// The realm's GameServer (world) port - advertised to the client in the EnterWorld handoff (SendGameServer).
		public ushort Port { get; set; }
	}
}
