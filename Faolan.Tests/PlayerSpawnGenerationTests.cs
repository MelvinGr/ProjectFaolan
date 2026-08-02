using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the CharacterIIR half of player-spawn generation: the char message inside the spawn body is a
	// ProtoCoreCharacterIIR (name / firstname / lastname / stats / npc_stats / player_stats / bcc / targets /
	// generic_stats / inventory) that decodes and RE-ENCODES byte-identically through the client schema, so the
	// spawn's identity/stats/equipment is built from a typed object populated from the character. The scaffold
	// frame around it is covered by PlayerSpawnScaffoldCodecTests/RowsTests. The Strie bytes used here are a
	// golden test oracle only (SpawnStrieTemplate.bin) - the server embeds no captured spawn. See
	// docs/re/player-spawn-generation.md.
	public class PlayerSpawnGenerationTests
	{
		// A DEAD character's player FA015807 self-spawn (the SimpleCharFullUpdateIIR that CONSTRUCTS the local
		// SimpleChar whose dead-flag gates the release/resurrect window) carries CURRENT health = 0 while life/max
		// stays full - matching the retail login-while-dead capture (Stihetfv #50: health(f7)=0, life(f6) full).
		// An alive character keeps cur==max. This is the FA015807 analogue of the snapshot HealthCurrent=0
		// (#214); the SimpleChar is built from THIS packet, not the 0x737A6DF9 snapshot.
		[Theory, InlineData(false, 13000u), InlineData(true, 0u)]
		// alive: current health == max (Guardian L1 = 130, x100 wire scale)
		// dead:  current health == 0,   max/life stays full
		public void Build_DeadCharacter_ZeroesCurrentHealthKeepsLifeFull(bool isDead, uint expectedHealth)
		{
			var character = new Character
			{
				Name = "Testie",
				Race = Race.Cimmerian,
				Sex = Sex.Male,
				Class = ConanClass.Guardian,
				Level = 1,
				IsDead = isDead
			};

			var message = PlayerCharacterIirs.ForCharacter(character, new CharacterEquipment());

			Assert.Equal(expectedHealth, message.Stats.Health);
			// `life` carries the CLASS BASE, which the client tops up - not our sheet ceiling. Retail proves the split
			// in one packet: Stihetfv (Conqueror L2) has life 19600 = 196 = the class base against current health 229
			// (#580). It stays full regardless of dead state; only `health` is zeroed.
			Assert.Equal(CharacterMaxStats.ClassBaseHundredths(character.Class, character.Level).Health, message.Stats.Life);
			Assert.True(message.Stats.Life < character.MaxVitals().Health * 100, "the base must sit below the sheet ceiling");
			if (isDead)
			{
				Assert.Equal(29u, message.Stats.BccState); // bcc_state=29 = the Dead behavior-controller state (alive=1)
				Assert.False(message.CanFall); // a dead corpse reports can_fall=0 (alive keeps the template value)
				Assert.Equal(41877u, message.Bcc.AnimForcedId); // forced death pose animation
				Assert.True(message.Bcc.AnimForcedBlend);
			}
			else
			{
				Assert.Equal(1u, message.Stats.BccState); // alive: template's bcc_state (1)
				Assert.Equal(0u, message.Bcc.AnimForcedId); // alive: no forced animation (template carries none)
			}
		}

		[Fact]
		public void PlayerCharacterMessage_DecodesExpectedIdentity()
		{
			var decoded = CharacterMessage(Golden.Bytes("SpawnStrieTemplate")).FromProtobuf<ProtoCoreCharacterIIR>();

			Assert.Equal("Strie", decoded.Name);
			Assert.NotNull(decoded.Stats);
			Assert.NotNull(decoded.player_stats);
			Assert.NotNull(decoded.Bcc);
		}

		// Locks the editor->wire looks convention used by the spawn rebuild: the editor reports head mesh /
		// voice / size as 2x the wire value (like race/class), so CharacterStats carries ChangeHeadMesh/2,
		// ChangeVoice/2 and ChangeSize/2. Dikketest's create capture chose head 2285694, voice 10, size 194.
		[Fact]
		public void PlayerCharacterMessage_LooksFields_AreHalfTheEditorValues()
		{
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(Golden.Bytes("SpawnDikketestLogin"), out var read));

			Assert.Equal(2285694u / 2, read.Message.Stats.HeadMesh);
			Assert.Equal(10u / 2, read.Message.Stats.VoiceSet);
			Assert.Equal(194u / 2, read.Message.Stats.MonsterScale);
		}

		[Fact]
		public void PlayerCharacterMessage_RoundTripsByteIdentically()
		{
			var message = CharacterMessage(Golden.Bytes("SpawnStrieTemplate"));

			var decoded = message.FromProtobuf<ProtoCoreCharacterIIR>();
			var reencoded = decoded.ToProtobuf();

			Assert.Equal(message.Length, reencoded.Length);
			Assert.True(message.SequenceEqual(reencoded), "ProtoCoreCharacterIIR did not re-encode byte-identically");
		}

		[Fact]
		public void PlayerSpawnEquipment_BuildsInventoryEntriesFromWornItems()
		{
			var equipment = new CharacterEquipment
			{
				Head = new Item
				{
					Id = 0x00308E19,
					Key = new ProtoDecryptionKey
					{
						Key1 = 0x4C95E6E3,
						Key2 = 0xDF665726,
						Key3 = 0x09000E2A,
						Key4 = 0x892A0B17
					},
					Hash = 0x1234
				},
				Chest = new Item
				{
					Id = 0x000035F9,
					Key = new ProtoDecryptionKey
					{
						Key1 = 0x6B18BD73,
						Key2 = 0xCCEBE6CF,
						Key3 = 0xED8B315D,
						Key4 = 0x1E1E835F
					},
					Hash = 0x5678
				},
			};

			var entries = PlayerSpawnEquipment.BuildInventoryEntries(equipment);

			Assert.Equal(2, entries.Count);
			Assert.Equal((uint)CharacterEquipmentSlot.Head, entries[0].Pos); // 2, sorted before Chest (5)
			Assert.Equal((uint)CharacterEquipmentSlot.Chest, entries[1].Pos);

			var head = entries[0].Item;
			Assert.Equal(0x00308E19u, head.Template1);
			Assert.Equal(0x00308E19u, head.Template2);
			Assert.Equal(0x1234u, head.Hash);
			Assert.Equal(0x4C95E6E3u, head.Key1.Key1);
			Assert.Equal(0x892A0B17u, head.Key1.Key4);
			Assert.Same(head.Key1, head.Key2); // keys reuse the same quad (matches SnapshotItems)
		}

		// A worn item with no hash cannot be described on the wire. It used to be skipped, which showed the piece to its
		// WEARER (their client rendered it from their own inventory) and to nobody else - a divergence with no error
		// anywhere. The NPC builder already refuses the same shape.
		[Fact]
		public void PlayerSpawnEquipment_WornItemWithoutAHash_Throws()
		{
			var equipment = new CharacterEquipment
			{
				Belt = new Item
				{
					Id = 0x99,
					Name = "Hashless Belt",
					Hash = 0
				}
			};

			var ex = Assert.Throws<InvalidOperationException>(() => PlayerSpawnEquipment.BuildInventoryEntries(equipment));
			Assert.Contains("has no hash", ex.Message);
		}

		// The melee weapons render from the LEFT-hand navs (the wire melee pair is 6/8); the ranged pair
		// (MainHandRight/OffHandRight) is 20/21 - NecroL80's crossbow/quiver slots. A swap here makes weapons
		// silently not render (the bug this fixed: MainWeapon read MainHandRight).
		[Fact]
		public void PlayerSpawnEquipment_MapsWeaponPairsToTheCorrectHands()
		{
			var equipment = new CharacterEquipment
			{
				MainHandLeft = new Item
				{
					Id = 1,
					Key = new ProtoDecryptionKey
					{
						Key1 = 1
					},
					Hash = 1
				},
				OffHandLeft = new Item
				{
					Id = 2,
					Key = new ProtoDecryptionKey
					{
						Key1 = 2
					},
					Hash = 2
				},
				MainHandRight = new Item
				{
					Id = 3,
					Key = new ProtoDecryptionKey
					{
						Key1 = 3
					},
					Hash = 3
				},
				OffHandRight = new Item
				{
					Id = 4,
					Key = new ProtoDecryptionKey
					{
						Key1 = 4
					},
					Hash = 4
				},
				Bag = new Item
				{
					Id = 5,
					Key = new ProtoDecryptionKey
					{
						Key1 = 5
					},
					Hash = 5
				}
			};

			var entries = PlayerSpawnEquipment.BuildInventoryEntries(equipment);

			Assert.Equal(
				new[]
				{
					((uint)CharacterEquipmentSlot.Bag, 5u), ((uint)CharacterEquipmentSlot.MainWeapon, 1u), ((uint)CharacterEquipmentSlot.OffHandWeapon, 2u),
					((uint)CharacterEquipmentSlot.RangedWeapon, 3u), ((uint)CharacterEquipmentSlot.Ammo, 4u)
				}, entries.Select(e => (e.Pos, e.Item.Template1)).ToArray());
		}

		// ToggleVanityArmorFilter (0x200B) does NOT withhold anything from the spawn: every worn + vanity item always
		// ships, and hiding a slot is carried purely by the VanityShownMask bitmask in the spawn header (the client
		// renders the show/hide off it). So the snapshot equipment is invariant under the toggle - only the mask moves.
		[Fact]
		public void PlayerSpawnEquipment_ShipsAllItems_HideIsMaskOnly()
		{
			var equipment = new CharacterEquipment
			{
				Head = new Item
				{
					Id = 0x10,
					Key = new ProtoDecryptionKey
					{
						Key1 = 0x10
					},
					Hash = 0x10
				},
				VanityHead = new Item
				{
					Id = 0x20,
					Key = new ProtoDecryptionKey
					{
						Key1 = 0x20
					},
					Hash = 0x20
				},
				VanityCape = new Item
				{
					Id = 0x30,
					Key = new ProtoDecryptionKey
					{
						Key1 = 0x30
					},
					Hash = 0x30
				}
			};

			var before = PlayerSpawnEquipment.BuildInventoryEntries(equipment).Select(e => e.Pos).ToHashSet();
			Assert.Contains((uint)CharacterEquipmentSlot.VanityHead, before);
			Assert.Contains((uint)CharacterEquipmentSlot.VanityCape, before);
			Assert.Contains((uint)CharacterEquipmentSlot.Head, before);

			// Toggle head + the global master: the mask picks up bits 2 and 31, but the shipped entries are unchanged.
			Assert.True(equipment.SetVanityFilter((uint)CharacterEquipmentSlot.Head, true));
			Assert.True(equipment.SetVanityFilter(CharacterEquipment.VanityFilterMainSlot, true));
			Assert.Equal((1u << 2) | (1u << 31), equipment.VanityShownMask);

			var after = PlayerSpawnEquipment.BuildInventoryEntries(equipment).Select(e => e.Pos).ToHashSet();
			Assert.Equal(before, after); // nothing withheld

			// An out-of-range slot is rejected, changing nothing.
			Assert.False(equipment.SetVanityFilter(32, true));
		}

		// The dynamic reader locates the CharacterIIR in any spawn; on the Strie template it must agree with the fixed
		// SpawnPacketBuilder offsets (NameTagOffset / NameInnerLengthOffset).
		[Fact]
		public void PlayerSpawnReader_LocatesCharacterMessage_OnTemplate()
		{
			var body = Golden.Bytes("SpawnStrieTemplate");
			var innerLen = (int)body.ReadU32Be(SpawnPacketBuilder.NameInnerLengthOffset);

			Assert.True(SpawnBodyReader.TryReadCharacterMessage(body, out var result));
			Assert.Equal(SpawnPacketBuilder.NameTagOffset, result.Offset);
			Assert.Equal(innerLen, result.Length);
			Assert.Equal("Strie", result.Message.Name);
		}

		// Changing the message (here the name, a different length) resizes the body, keeps it decodable, and fixes up
		// the enclosing length prefixes (the body-length field @0 == total - 4).
		[Fact]
		public void SetCharacterMessage_ChangingName_ResizesAndStaysConsistent()
		{
			var body = Golden.Bytes("SpawnStrieTemplate");
			var message = CharacterMessage(body).FromProtobuf<ProtoCoreCharacterIIR>();
			message.Name = "Tester"; // 6 chars vs "Strie" (5)

			var rebuilt = SpawnPacketBuilder.SetCharacterMessage(body, message);

			Assert.Equal(body.Length + 1, rebuilt.Length);
			Assert.Equal((uint)(rebuilt.Length - 4), rebuilt.ReadU32Be(0));
			var back = CharacterMessage(rebuilt).FromProtobuf<ProtoCoreCharacterIIR>();
			Assert.Equal("Tester", back.Name);
			Assert.NotNull(back.Bcc); // the tail (after the name) survived the splice
		}

		// Build-side oracle: replacing the CharacterIIR component with its own decoded form is a byte-identical no-op.
		[Fact]
		public void SetCharacterMessage_WithUnchangedDecodedMessage_IsByteIdentical()
		{
			var body = Golden.Bytes("SpawnStrieTemplate");
			var message = CharacterMessage(body).FromProtobuf<ProtoCoreCharacterIIR>();

			var rebuilt = SpawnPacketBuilder.SetCharacterMessage(body, message);

			Assert.Equal(body, rebuilt);
		}

		// The ProtoCoreCharacterIIR sub-message sits at NameTagOffset (its first field, name=0x0A) and is enclosed by
		// the 4-byte length prefix at NameInnerLengthOffset.
		private static byte[] CharacterMessage(byte[] template)
		{
			var length = (int)template.ReadU32Be(SpawnPacketBuilder.NameInnerLengthOffset);
			Assert.Equal(0x0A, template[SpawnPacketBuilder.NameTagOffset]); // field 1 (name)
			return template.AsSpan(SpawnPacketBuilder.NameTagOffset, length).ToArray();
		}
	}
}
