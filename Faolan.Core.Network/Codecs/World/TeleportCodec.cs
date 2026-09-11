using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The change-map / teleport prep records the retail server sends before the 0x2002 handoff, decoded byte-for-byte
	// from the changemap captures (frames 4200/4212 map 2010, 4417/4420 map 2000) against the IIR readers (full reader
	// walk in docs/subsystems/teleport-changemap.md):
	//   - IntendedTeleportIIR_c ("begin teleport to map N")    -> reader FUN_005bfce0
	//   - n3TeleportIIR_t       ("destination map + position") -> reader FUN_005d1ae0
	// Both embed a PlayfieldProxy stream (FUN_00a6a600): `[u8 version='b'(0x62)][u32 x8][u8 bool]`, where u32[0] is
	// a playfield-class word and u32[1] the destination map. The "begin" proxy carries only those two (the
	// destination instance is not pinned yet); the dest-announce proxy is fully specified - u32[5]=1 (a valid/ready
	// flag), u32[6]=0x9C50, u32[7]=the destination's playfield-instance token.
	//
	// A family: both IIRs are [carrier][env][...][PlayfieldProxy][...], so the base TeleportIIR holds what they share
	// and Parse discriminates on the subop.
	//
	// n3Teleport is one grammar, not two. Read straight through, all 128 captured records are one shape:
	//
	//   [u32 sub][u32 0xC350][u32 clientInstance][u8 env]
	//   [Vector3 position][Quaternion rotation]        <- the arrival transform
	//   [PlayfieldProxy]
	//   [u32 0][u32 0]                                 <- obj+0x6c / obj+0x70
	//   [u32 blobLen][ blob: u32 tag 0x00018704, Vector3 position, u32 descriptor ]
	//   [u8 trailing]
	//
	// The two apparent forms are just whether the length-prefixed spawn blob is empty, which the length word states
	// outright - there is no heuristic to apply. The census is exact: 92 records carry the 20-byte blob, and every one
	// of those has a zero leading position, an identity rotation, trailing 0 and a 0xC79C/0xCABC proxy (the cross-map
	// announce, which states its arrival in the blob); the other 36 carry no blob, a real leading position, trailing 1
	// and a 0xC7A0 proxy (the same-map relocate). `env` is the direction - 0 on all 113 s2c records, 1 on all 15 c2s
	// echoes.
	//
	// The four words after the position are a quaternion, not "facingX, 0, facingZ" with two zero words: x²+y²+z²+w²
	// is 1.0 to within 1e-4 on all 128 records, including the 34 whose x and z are non-zero. The cross-map form leaves
	// that pair at zero/identity, which is why it can look like a discriminating constant.
	public static class TeleportCodec
	{
		// The IIR types this codec constructs or parses.
		public static readonly IirTypes[] HandledTypes = [IirTypes.IntendedTeleportIIR, IirTypes.N3TeleportIIR];

		// The destination-announce half of the family, named so a caller can select it without spelling an IirTypes
		// member of its own: the importer has to pick the n3Teleport out of a packet stream to harvest a changemap-shaped
		// dialogue trip.
		public const uint N3Opcode = (uint)IirTypes.N3TeleportIIR;

		// The length-prefixed spawn blob is itself tagged, and its first word says which arm follows. 0x…04 states the
		// arrival as literal coordinates; 0x…06 states it as a spawn-point name and ships no coordinates at all, leaving
		// the destination playfield to resolve it. Corpus census over 144 records: 94 carry 0x…04, 49 carry no blob,
		// 1 carries 0x…06. That one is the ACG/instanced travel arrival (the Tortage harbour rowboat into White Sands
		// Night, xxx_mage-necromancer_login_play3 frame 4735), and that capture is the only one containing the ACG
		// teleport flow at all, so the named arm is 1-of-1 within its own population.
		private const uint SpawnPositionTag = 0x00018704; // arm A: [Vector3 position][u32 descriptor]
		private const uint SpawnPointNameTag = 0x00018706; // arm B: [u16 len][latin1 spawn-point name]

		// The revive body's proxy uses this class word instead of PlayfieldProxy.PlayfieldClass; which of
		// 0xC79C/0xC79E/0xC7A0 a packet carries is per-packet and undecoded (see ResurrectionSpotsBody).
		private const uint RevivePlayfieldClass = 0x0000C7A0;

		// Canonical - the only method here that writes bytes.
		public static byte[] Build(TeleportIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// IntendedTeleport "begin teleport to map N": carrier + env + a PlayfieldProxy carrying only the class word and
		// the destination map (instance slots zero) + a trailing bool.
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=01][PlayfieldProxy][u8 bool=0]
		// `epic` picks the destination's difficulty class word: the captured epic Spider Caverns begin is byte-identical
		// to the normal one except 0xCABC for 0xC79C (chat-annotated-3 frames 23499/23843).
		public static byte[] Build(uint clientInstance, uint destMapId, bool epic = false)
		{
			return Build(new IntendedTeleportIIR(clientInstance, 0x01, PlayfieldProxy.ForMap(destMapId, epic), 0x00));
		}

		// The same-map teleport begin: the single IntendedTeleport that pins the target playfield for a same-map
		// relocation without a loading screen - retail sends it for the death revive and for same-playfield zone-line
		// doors such as the inn's. One record, no position: the client positions off the RespawnMe spawnPointId, its RDB
		// entry, or the following n3Teleport. The PlayfieldProxy is pinned to the current playfield - map, the player's
		// own handle in slot3, the ready flag, and the live playfield token in slot7.
		//
		// `playfieldToken` must be the token the client currently holds (Zone.PlayfieldToken, taken from the burst-less
		// world-entry snapshot - the same one SpawnTransformPatch stamps into rendered spawns), never the MapWorldBurst
		// token (ResolveMapPlayfieldToken): a mismatch makes the client treat this as a real changemap and show a
		// loading screen. Decoded byte-for-byte from the beach resurrect (Stihetfv packet 1387, map 0xFAA): trailing IIR
		// bool = 1.
		public static byte[] Build(uint clientInstance, uint mapId, uint playerHandle, uint playfieldToken)
		{
			return Build(new IntendedTeleportIIR(clientInstance, 0x01, PlayfieldProxy.ForInstance(mapId, playfieldToken) with { OwnerId = playerHandle }, 0x01));
		}

		// The cross-map destination announce: carrier + a zero arrival transform + the fully specified destination
		// PlayfieldProxy + 2 words + the spawn-position blob + a trailing bool.
		// `destPlayfieldToken` is the destination playfield-instance token, not a per-map constant - the captured
		// 0x00503570 was that session's instance; the live change-map path sources it from the destination map's burst
		// via WorldBroadcaster.ResolveMapPlayfieldToken so it matches the reconnect's world-entry stamp.
		// `destDescriptor` (0x14 map 2010 / 0xBE map 2000) is the position blob's trailing word - a per-zone descriptor
		// not derivable from the data. Both default to the map-2010 capture values, for the golden and a burst-less map.
		public static byte[] Build(uint clientInstance, uint destMapId, Vector3 position, uint destPlayfieldToken = 0x00503570u, uint destDescriptor = 0x00000014u,
			bool epic = false)
		{
			// The arrival transform stays zero/identity: a cross-map announce states its arrival in the blob.
			return Build(new N3TeleportIIR(clientInstance, 0x00, Vector3.Zero, Quaternion.Identity,
				PlayfieldProxy.ForInstance(destMapId, destPlayfieldToken, epic), new N3TeleportSpawnPosition(position, destDescriptor)));
		}

		// The same-map destination announce: the n3Teleport that actually relocates the player on their current
		// playfield with no loading screen. Retail sends it both for the death revive (after the revive IntendedTeleport)
		// and for a same-playfield zone-line door - the Thirsty Dog Inn, say, which repositions within City of Tortage
		// rather than change-mapping (WorldSimulation.RelocateSameMap reaches it via SendReviveDestination;
		// WorldTeleportExtractor decodes the same variant). It differs from the cross-map form only in the fields, not
		// the grammar: the arrival rides the leading transform so no spawn blob is sent, the proxy carries 0xC7A0 rather
		// than 0xC79C plus map and player handle with the instance slots zero, and the trailing bool is 1. Decoded
		// byte-for-byte from the beach resurrect (Stihetfv packet 1277, map 0xFAA).
		public static byte[] Build(uint clientInstance, uint mapId, uint playerHandle, Vector3 position, Quaternion rotation)
		{
			// Instance slots zero: a same-map move does not re-pin the playfield instance.
			return Build(new N3TeleportIIR(clientInstance, 0x00, position, rotation, new PlayfieldProxy(mapId, RevivePlayfieldClass, playerHandle), Spawn: null, Trailing: 0x01));
		}

		public static TeleportIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeleportCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static TeleportIIR Read(ref SpanReader r)
		{
			var subopOffset = r.Position;
			var subop = (IirTypes)r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var clientInstance = r.ReadU32Be();
			var env = r.ReadU8();

			if (subop == IirTypes.IntendedTeleportIIR)
			{
				var proxy = PlayfieldProxy.Read(ref r);
				return new IntendedTeleportIIR(clientInstance, env, proxy, r.ReadU8());
			}

			if (subop != IirTypes.N3TeleportIIR)
				throw CodecParseException.Unexpected(nameof(TeleportCodec), subopOffset, "IntendedTeleportIIR or n3TeleportIIR", $"0x{(uint)subop:X8}");

			var arrival = r.ReadVector3Be();
			var rotation = r.ReadQuaternionBe();
			var n3Proxy = PlayfieldProxy.Read(ref r);
			r.ExpectU32Be(0, "obj+0x6c");
			r.ExpectU32Be(0, "obj+0x70");

			// The spawn blob's length says whether this announce carries one - 0 on a same-map relocate, which states its
			// arrival in the leading transform instead. A present blob then names its own arm in its first word.
			var blobBytes = r.ReadBytesU32Length();
			N3TeleportSpawn? spawn = null;
			if (!blobBytes.IsEmpty)
			{
				var blob = new SpanReader(blobBytes, nameof(TeleportCodec) + ".spawnBlob");
				var tagOffset = blob.Position;
				var tag = blob.ReadU32Be();
				spawn = tag switch
				{
					SpawnPositionTag => new N3TeleportSpawnPosition(blob.ReadVector3Be(), blob.ReadU32Be()),
					SpawnPointNameTag => new N3TeleportSpawnPoint(blob.ReadStringU16Length()),
					_ => throw CodecParseException.Unexpected(nameof(TeleportCodec), tagOffset, "a spawn-blob tag (0x00018704 position or 0x00018706 named)", $"0x{tag:X8}")
				};
				blob.EnsureAtEnd();
			}

			return new N3TeleportIIR(clientInstance, env, arrival, rotation, n3Proxy, spawn, r.ReadU8());
		}

		internal static void Write(FaolanStream s, TeleportIIR v)
		{
			switch (v)
			{
				case IntendedTeleportIIR begin:
				{
					s.WriteUInt32(IirTypes.IntendedTeleportIIR).WriteUInt32(EntityClass.Character).WriteUInt32(begin.ClientInstance).WriteByte(begin.Env);
					begin.Proxy.Write(s);
					s.WriteByte(begin.Trailing); // IntendedTeleportIIR trailing bool (-> obj+0x40)
					break;
				}

				case N3TeleportIIR dest:
				{
					using var blob = new FaolanStream();
					switch (dest.Spawn)
					{
						case N3TeleportSpawnPosition at:
							blob.WriteUInt32(SpawnPositionTag).WriteVector3(at.Position).WriteUInt32(at.Descriptor);
							break;

						case N3TeleportSpawnPoint named:
							blob.WriteUInt32(SpawnPointNameTag).WriteString(named.Name);
							break;
					}

					s.WriteUInt32(IirTypes.N3TeleportIIR)
						.WriteUInt32(EntityClass.Character)
						.WriteUInt32(dest.ClientInstance)
						.WriteByte(dest.Env)
						.WriteVector3(dest.Position)
						.WriteQuaternion(dest.Rotation);
					dest.Proxy.Write(s);
					s.WriteUInt32(0).WriteUInt32(0) // obj+0x6c / +0x70
						.WriteArrayUInt32Length(blob.ToArray())
						.WriteByte(dest.Trailing); // n3TeleportIIR trailing bool
					break;
				}

				default:
				{
					throw new CodecParseException($"{nameof(TeleportCodec)}: unsupported teleport record {v.GetType().Name}");
				}
			}
		}
	}

	// What the two teleport records share: the addressed client instance, the carrier's env byte, and the
	// PlayfieldProxy naming the destination playfield.
	public abstract record TeleportIIR(uint ClientInstance, byte Env, PlayfieldProxy Proxy);

	// IntendedTeleportIIR_c - "begin teleport". Trailing is the IIR's own bool at obj+0x40: 0 for a cross-map begin,
	// 1 for the same-map one.
	public sealed record IntendedTeleportIIR(uint ClientInstance, byte Env, PlayfieldProxy Proxy, byte Trailing) : TeleportIIR(ClientInstance, Env, Proxy);

	// n3TeleportIIR_t - the destination announce. `Position`/`Rotation` are the arrival transform a same-map relocate
	// carries; a cross-map announce leaves them zero/identity and states its arrival in `Spawn` instead. `Trailing` is
	// the IIR's own bool - 0 cross-map, 1 same-map on all 128 captured records.
	public sealed record N3TeleportIIR(uint ClientInstance, byte Env, Vector3 Position, Quaternion Rotation, PlayfieldProxy Proxy,
		N3TeleportSpawn? Spawn = null, byte Trailing = 0) : TeleportIIR(ClientInstance, Env, Proxy)
	{
		/// <summary>Where the player actually arrives, from whichever slot this announce used.</summary>
		/// <returns>
		///     Null when the record states no coordinates at all, i.e. a named spawn point the destination playfield has
		///     to resolve. Callers must handle that rather than substituting the zero vector: the leading transform
		///     really is (0,0,0) on a cross-map announce, so a fallback would be indistinguishable from a genuine origin
		///     arrival.
		/// </returns>
		public Vector3? ArrivalPosition => Spawn switch
		{
			N3TeleportSpawnPosition at => at.Position,
			N3TeleportSpawnPoint => null,
			_ => Position
		};
	}

	// The length-prefixed spawn blob a cross-map announce carries, in whichever of its two tagged arms.
	public abstract record N3TeleportSpawn;

	// Arm 0x00018704 - the arrival stated as literal coordinates. `Descriptor` is a per-zone word (0x14 on map 2010,
	// 0xBE on map 2000, 0xFFFFFFFF on the Tortage inn zone-line) that is not derivable from the data.
	// Open work tracked in Gitea #665.
	public sealed record N3TeleportSpawnPosition(Vector3 Position, uint Descriptor) : N3TeleportSpawn;

	// Arm 0x00018706 - the arrival stated as a spawn-point name ("PlayerSpawnPoint"), with no coordinates on the wire.
	public sealed record N3TeleportSpawnPoint(string Name) : N3TeleportSpawn;
}
