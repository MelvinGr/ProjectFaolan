using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A map's captured login world-build (MapChange + world objects + entity spawns + inventory), stored as the
	// ordered raw download packets to replay on world entry - re-addressed from CapturedInstance to the live
	// character. Generalizes the embedded beach world-burst to any map. Imported by Faolan.Importer.
	[Obsolete]
	public class MapWorldBurst : BaseEntity
	{
		public virtual ICollection<MapWorldBurstPacket> Packets { get; set; }
		public uint CapturedInstance { get; set; }
		public uint MapId { get; set; }
	}

	[Obsolete]
	public class MapWorldBurstPacket : BaseEntity
	{
		[ForeignKey(nameof(MapWorldBurstId))]
		public virtual MapWorldBurst Burst { get; set; }

		public byte[] Data { get; set; }

		public int Sequence { get; set; }
		public uint MapWorldBurstId { get; set; }
	}
}
