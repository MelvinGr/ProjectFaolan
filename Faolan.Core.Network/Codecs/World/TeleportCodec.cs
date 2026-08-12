using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The change-map / teleport prep records the retail server sends before the 0x2002 handoff (#219), decoded
	// byte-for-byte from the changemap captures (frames 4200/4212 map 2010, 4417/4420 map 2000) against the IIR
	// readers recovered in #233 (full reader walk in docs/subsystems/teleport-changemap.md):
	//   - IntendedTeleportIIR_c (0x10D27BC0, "begin teleport to map N")  -> reader FUN_005bfce0
	//   - n3TeleportIIR_t       (0x5A32F0D7, "destination map + position") -> reader FUN_005d1ae0
	// Both embed a PlayfieldProxy stream (FUN_00a6a600): `[u8 version='b'(0x62)][u32 x8][u8 bool]`, where u32[0] is
	// a playfield-class word and u32[1] the destination map. The "begin" proxy carries only those two (the
	// destination instance isn't pinned yet); the dest-announce proxy is fully specified - u32[5]=1 (a valid/ready
	// flag), u32[6]=0x9C50, u32[7]=the destination's playfield-INSTANCE token.
	//
	// A FAMILY (#530 rule 5a): both IIRs are [carrier][env][...][PlayfieldProxy][...], so the base TeleportIIR really
	// does hold what they share and Parse discriminates on the subop.
	//
	// ⚠️ n3Teleport is ONE grammar, not two (#809). It used to be read as a "cross-map" and a "same-map" form told
	// apart by a leading-block HEURISTIC (six zero words + 1.0f), and the same-map branch then required two trailing
	// bytes where retail sends one - so every one of the 36 captured same-map records was REFUSED and our own builder
	// emitted 89 bytes where retail emits 88. Read straight through, all 128 captured records are one shape:
	//
	//   [u32 sub][u32 0xC350][u32 clientInstance][u8 env]
	//   [Vector3 position][Quaternion rotation]        <- the ARRIVAL transform
	//   [PlayfieldProxy]
	//   [u32 0][u32 0]                                 <- obj+0x6c / obj+0x70
	//   [u32 blobLen][ blob: u32 tag 0x00018704, Vector3 position, u32 descriptor ]
	//   [u8 trailing]
	//
	// The "two forms" are just whether the length-prefixed spawn blob is EMPTY, which the length word states
	// outright - no heuristic. The census is exact: 92 records carry the 20-byte blob, and every one of those has a
	// ZERO leading position, an IDENTITY rotation, trailing 0 and a 0xC79C/0xCABC proxy (the cross-map announce,
	// which states its arrival in the blob); the other 36 carry no blob, a REAL leading position, trailing 1 and a
	// 0xC7A0 proxy (the same-map relocate). `env` is the direction - 0 on all 113 s2c records, 1 on all 15 c2s
	// echoes.
	//
	// ⭐ The four words after the position are a QUATERNION, not "facingX, 0, facingZ" with two zero words: x²+y²+z²+w²
	// is 1.0 to within 1e-4 on all 128 records, including the 34 whose x and z are non-zero (a pitched arrival the
	// old model could not express and refused outright). The cross-map form's "six zero words and a 1.0f" is that
	// same pair at zero/identity, which is why it looked like a discriminating constant.
	public static class TeleportCodec
	{
		// The IIR types this codec constructs or parses.
		public static readonly IirTypes[] HandledTypes = [IirTypes.IntendedTeleportIIR, IirTypes.N3TeleportIIR];

		private const uint PositionBlobTag = 0x00018704; // first word of the length-prefixed spawn-position blob

		// The revive body's proxy uses this class word instead of PlayfieldProxy.PlayfieldClass; which of
		// 0xC79C/0xC79E/0xC7A0 a packet carries is per-PACKET and undecoded (see ResurrectionSpotsBody).
		private const uint RevivePlayfieldClass = 0x0000C7A0;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(TeleportIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// 0x10D27BC0 "begin teleport to map N": carrier + env + a PlayfieldProxy carrying only the class word and the
		// destination map (instance slots zero) + a trailing bool.
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=01][PlayfieldProxy][u8 bool=0]
		public static byte[] Build(uint clientInstance, uint destMapId)
		{
			return Build(new IntendedTeleportIIR(clientInstance, 0x01, PlayfieldProxy.ForMap(destMapId), 0x00));
		}

		// The SAME-MAP teleport begin (#214): the single IntendedTeleport (0x10D27BC0) that pins the target playfield
		// for a same-map relocation WITHOUT a loading screen - retail sends it for the death revive AND same-playfield
		// zone-line doors (the inn; via WorldSimulation.RelocateSameMap -> SendReviveTeleport). ONE record, NO
		// position (the client positions off the RespawnMe spawnPointId / its RDB / the following n3Teleport). The
		// PlayfieldProxy is pinned to the CURRENT playfield: map + the player's own handle in slot3 + the ready flag +
		// the LIVE playfield token in slot7. CRITICAL: playfieldToken MUST be the token the client currently holds
		// (Zone.PlayfieldToken, captured from the burst-less world-entry snapshot - the same one SpawnTransformPatch
		// stamps into rendered spawns), NOT the MapWorldBurst token (ResolveMapPlayfieldToken): a mismatch makes the
		// client treat it as a real changemap and show a LOADING SCREEN. Decoded byte-for-byte from the beach resurrect
		// (Stihetfv #1387, map 0xFAA): trailing IIR bool = 1.
		public static byte[] Build(uint clientInstance, uint mapId, uint playerHandle, uint playfieldToken)
		{
			return Build(new IntendedTeleportIIR(clientInstance, 0x01, PlayfieldProxy.ForInstance(mapId, playfieldToken) with { OwnerId = playerHandle }, 0x01));
		}

		// 0x5A32F0D7 CROSS-MAP destination announce: carrier + a zero arrival transform + the fully-specified
		// destination PlayfieldProxy + 2 words + the spawn-position blob + a trailing bool.
		// destPlayfieldToken is the destination playfield-INSTANCE token (NOT a per-map constant - the captured
		// 0x00503570 was that session's instance); the live change-map path sources it from the destination map's
		// burst via WorldBroadcaster.ResolveMapPlayfieldToken so it matches the reconnect's world-entry stamp (#211).
		// destDescriptor (0x14 map 2010 / 0xBE map 2000) is the position blob's trailing word - a per-zone descriptor
		// not yet derivable from the data. Both default to the map-2010 capture values for the golden / a burst-less map.
		public static byte[] Build(uint clientInstance, uint destMapId, Vector3 position, uint destPlayfieldToken = 0x00503570u, uint destDescriptor = 0x00000014u)
		{
			// The arrival transform stays zero/identity: a cross-map announce states its arrival in the blob.
			return Build(new N3TeleportIIR(clientInstance, 0x00, Vector3.Zero, Quaternion.Identity,
				PlayfieldProxy.ForInstance(destMapId, destPlayfieldToken), new N3TeleportSpawn(position, destDescriptor)));
		}

		// The SAME-MAP destination announce: the n3Teleport (0x5A32F0D7) that actually relocates the player on their
		// CURRENT playfield with no loading screen. Retail sends it for BOTH the death revive (#214, after the revive
		// IntendedTeleport) AND a same-playfield zone-line door - e.g. the Thirsty Dog Inn, which repositions within
		// City of Tortage rather than change-mapping (WorldSimulation.RelocateSameMap reaches it via
		// SendReviveDestination; WorldTeleportExtractor decodes this same variant). It differs from the cross-map form
		// only in the fields, not the grammar: the arrival rides the leading transform so NO spawn blob is sent, the
		// proxy carries 0xC7A0 (not 0xC79C) + map + player handle with the instance slots ZERO, and the trailing bool
		// is 1. Decoded byte-for-byte from the beach resurrect (Stihetfv #1277, map 0xFAA).
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

			// The spawn blob's LENGTH says whether this announce carries one - 20 bytes on a cross-map arrival, 0 on a
			// same-map relocate, which states its arrival in the leading transform instead.
			var blobBytes = r.ReadBytesU32Length();
			N3TeleportSpawn? spawn = null;
			if (!blobBytes.IsEmpty)
			{
				var blob = new SpanReader(blobBytes, nameof(TeleportCodec) + ".positionBlob");
				blob.ExpectU32Be(PositionBlobTag, "spawn-position blob tag");
				var position = blob.ReadVector3Be();
				spawn = new N3TeleportSpawn(position, blob.ReadU32Be());
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
					if (dest.Spawn is { } spawn)
						blob.WriteUInt32(PositionBlobTag).WriteVector3(spawn.Position).WriteUInt32(spawn.Descriptor);

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

	// IntendedTeleportIIR_c (0x10D27BC0) - "begin teleport". Trailing is the IIR's own bool at obj+0x40: 0 for a
	// cross-map begin, 1 for the same-map one.
	public sealed record IntendedTeleportIIR(uint ClientInstance, byte Env, PlayfieldProxy Proxy, byte Trailing) : TeleportIIR(ClientInstance, Env, Proxy);

	// n3TeleportIIR_t (0x5A32F0D7) - the destination announce. `Position`/`Rotation` are the arrival transform a
	// SAME-MAP relocate carries; a CROSS-MAP announce leaves them zero/identity and states its arrival in `Spawn`
	// instead. `Trailing` is the IIR's own bool - 0 cross-map, 1 same-map on all 128 captured records.
	public sealed record N3TeleportIIR(uint ClientInstance, byte Env, Vector3 Position, Quaternion Rotation, PlayfieldProxy Proxy,
		N3TeleportSpawn? Spawn = null, byte Trailing = 0) : TeleportIIR(ClientInstance, Env, Proxy)
	{
		/// <summary>Where the player actually arrives, from whichever of the two slots this announce used.</summary>
		public Vector3 ArrivalPosition => Spawn?.Position ?? Position;
	}

	// The length-prefixed spawn-position blob (tag 0x00018704) a CROSS-MAP announce carries. `Descriptor` is a
	// per-zone word (0x14 on map 2010, 0xBE on map 2000) not yet derivable from the data - #665.
	public sealed record N3TeleportSpawn(Vector3 Position, uint Descriptor);
}
