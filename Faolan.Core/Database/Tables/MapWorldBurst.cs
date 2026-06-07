using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A map's captured login world-build (MapChange + world objects + entity spawns + inventory), stored as the
	// ordered raw download packets to replay on world entry - re-addressed from CapturedInstance to the live
	// character. Generalizes the embedded beach world-burst to any map. Imported by Faolan.Importer.
	public class MapWorldBurst : BaseEntity
	{
		public uint MapId { get; set; }
		public uint CapturedInstance { get; set; }
		public virtual ICollection<MapWorldBurstPacket> Packets { get; set; }
	}

	public class MapWorldBurstPacket : BaseEntity
	{
		public uint MapWorldBurstId { get; set; }

		[ForeignKey(nameof(MapWorldBurstId))]
		public virtual MapWorldBurst Burst { get; set; }

		public int Sequence { get; set; }
		public byte[] Data { get; set; }
	}
}
