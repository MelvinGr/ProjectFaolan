using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     The create-editor initial burst as typed data: the five archetype pedestals and the live avatar preview.
	/// </summary>
	/// <remarks>
	///     Generated from the captured create-editor burst. Regenerate with:
	///     <code>
	///     FAOLAN_UPDATE_SCENE=1 dotnet test --project Faolan.Tests -- --filter-class '*CreateEditorSceneGeneratorTests'
	///     </code>
	///     <para>
	///         Some pre-IIR words are the retail server's own uninitialised memory rather than content: the slots they
	///         sit in resolve to <c>PvPAnnihilationMatchesWonSeasonal</c>, <c>PvPCTFMatchesLostSeasonal</c>,
	///         <c>Onslaught2SpecialsKilled</c> and the like, and a create-editor pedestal has no seasonal PvP record.
	///         They are reproduced because the gate is byte-exactness against retail, not because they mean anything.
	///         Do not read them as data, and do not carry them to a new record.
	///     </para>
	/// </remarks>
	public static class CreateEditorScene
	{
		// "Stygian Female" - 557 captured bytes.
		public static SimpleCharFullUpdateIIR StygianFemale()
		{
			return new SimpleCharFullUpdateIIR
			{
				Ack = 0xC350u,
				Handle = 0x9FF92u,
				FieldA = 0u,
				Position = new Vector3(262.931f, 6.0141416f, 280.97f),
				Rotation = new Quaternion(0f, 0.98768836f, 0f, 0.15643449f),
				Playfield = 0x502276ul,
				PreIir = new SpawnPreIir
				{
					Version = 0x05,
					Flags = [0x00, 0x03, 0x00, 0x00, 0x00, 0x00],
					Words = [0u, 0u, 0u, 0x7151E7E0u, 0u, 0x7151E81Cu, 0u, 0x15B6090u, 0x5862B7E0u, 0x40u, 0x829400D0u, 0u, 1u, 0u, 0u, 0u, 0x310u, 3u, 0u, 0u, 0x5E49D8B0u, 0u, 0u, 0u, 0u, 0u, 0x7151E81Cu, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x390u, 0u, 0x60u, 0u, 0x7151E7D0u, 0u, 0x36u, 5u, 0u, 0x775396C0u, 0u]
				},
				CoreCharVersion = 0x41,
				CoreCharIirHeader = 0x8203u,
				Character = new ProtoCoreCharacterIIR
				{
					Name = "Stygian Female",
					Stats = new ProtoCoreCharacterIIR.CharacterStats
					{
						Sex = 3u,
						Side = 0u,
						Race = 3u,
						MonsterScale = 0x5Au,
						MonsterData = 0x21923u,
						Life = 0x189Cu,
						Health = 0x189Cu,
						MaxStamina = 0x189Cu,
						Stamina = 0x189Cu,
						MaxMana = 0x189Cu,
						Mana = 0x189Cu,
						LooksPackage = 0u,
						MountScale = 0x64u,
						MountFlags = 0u,
						Flags = 0x10000001u,
						HeadMesh = 0x32773u,
						Level = 1u,
						ApprenticeLevel = 1u,
						Radius = 50,
						Bcc = 0x30C8u,
						BccState = 1u,
						CarsGroup = 0u,
						OrientationMode = 1u,
						VoiceSet = 3u
					},
					npc_stats = new ProtoCoreCharacterIIR.NpcStats
					{
						SightSkill = 0x14u,
						HearingSkill = 5u,
						SmellingSkill = 1u,
						TemplateId = 0xA74u,
						SuperFamily = 1u,
						NpcFlags = 0x1C0000u,
						NpcType = 4u
					},
					Bcc = new ProtoCoreCharacterIIR.CharacterBcc
					{
						AnimIdleId = 0xA428u,
						AnimIdleTimer = 0f,
						AnimIdleBlend = true,
						AnimFlags = 0u
					},
					CanFall = true,
					MovementMode = 2u,
					InventoryEntries = {
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 5u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0x3357A3D9u,
									Key2 = 0x4D972FBCu,
									Key3 = 0x5F9549B7u,
									Key4 = 0xB7272D41u
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0x3357A3D9u,
									Key2 = 0x4D972FBCu,
									Key3 = 0x5F9549B7u,
									Key4 = 0xB7272D41u
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0x3357A3D9u,
									Key2 = 0x4D972FBCu,
									Key3 = 0x5F9549B7u,
									Key4 = 0xB7272D41u
								},
								Template1 = 0x19D74u,
								Template2 = 0x19D74u,
								Level = 1u,
								Hash = 0x38523849u
							}
						}
					}
				},
				Post = new SpawnPostIir
				{
					FirstRecordLen = 5u,
					MovementMode = 2u,
					HasMoveTaskSlot = true,
					Slot = new SpawnEntrySlot()
				}
			};
		}

		// "Saddur" - 549 captured bytes.
		public static SimpleCharFullUpdateIIR Saddur()
		{
			return new SimpleCharFullUpdateIIR
			{
				Ack = 0xC350u,
				Handle = 0x9FF93u,
				FieldA = 0u,
				Position = new Vector3(263.515f, 6.545f, 284.583f),
				Rotation = new Quaternion(0f, 0f, 0f, 1f),
				Playfield = 0x502276ul,
				PreIir = new SpawnPreIir
				{
					Version = 0x05,
					Words = [0u, 0u, 0u, 0x76724F60u, 0u, 0x5745EF10u, 0u, 0x5745EF10u, 0x76724F60u, 0u, 0x210u, 0x40u, 0x462E93B0u, 0x775AED00u, 0x66F3F7C0u, 0x5E49D9B0u, 0x5745EF10u, 0xC9u, 0u, 0u, 0x250u, 0x40u, 0u, 0x5E549970u, 0u, 0x775AED00u, 0x76724F60u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x5E49D9B0u, 0x66F3F7C0u, 0u, 0x742B9000u, 0x7F25u, 0x1000u, 0u, 0x290u, 0u, 0u, 0x74BFE610u, 0u, 0x74BFE620u, 0u]
				},
				CoreCharVersion = 0x41,
				CoreCharIirHeader = 0x8203u,
				Character = new ProtoCoreCharacterIIR
				{
					Name = "Saddur",
					Stats = new ProtoCoreCharacterIIR.CharacterStats
					{
						Sex = 2u,
						Side = 0u,
						Race = 3u,
						MonsterScale = 0x69u,
						MonsterData = 0x31F5A3u,
						Life = 0x189Cu,
						Health = 0x189Cu,
						MaxStamina = 0x189Cu,
						Stamina = 0x189Cu,
						MaxMana = 0x189Cu,
						Mana = 0x189Cu,
						LooksPackage = 0u,
						MountScale = 0x64u,
						MountFlags = 0u,
						Flags = 0x10000001u,
						HeadMesh = 0x31F5B0u,
						Level = 1u,
						ApprenticeLevel = 1u,
						Radius = 50,
						Bcc = 0x30C8u,
						BccState = 1u,
						CarsGroup = 3u,
						OrientationMode = 1u,
						VoiceSet = 2u
					},
					npc_stats = new ProtoCoreCharacterIIR.NpcStats
					{
						SightSkill = 0xFu,
						HearingSkill = 0xAu,
						SmellingSkill = 5u,
						TemplateId = 0xBB1u,
						SuperFamily = 1u,
						NpcFlags = 0u,
						NpcType = 4u
					},
					Bcc = new ProtoCoreCharacterIIR.CharacterBcc
					{
						AnimIdleId = 0xA428u,
						AnimIdleTimer = 0f,
						AnimIdleBlend = true,
						AnimFlags = 0u
					},
					CanFall = true,
					MovementMode = 2u,
					InventoryEntries = {
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 6u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0xF2F9A557u,
									Key2 = 0xEA013404u,
									Key3 = 0x40322F6Au,
									Key4 = 0x3D622844u
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0xF2F9A557u,
									Key2 = 0xEA013404u,
									Key3 = 0x40322F6Au,
									Key4 = 0x3D622844u
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0xF2F9A557u,
									Key2 = 0xEA013404u,
									Key3 = 0x40322F6Au,
									Key4 = 0x3D622844u
								},
								Template1 = 0x3FC36u,
								Template2 = 0x3FC36u,
								Level = 5u,
								Hash = 0x52465150u
							}
						}
					}
				},
				Post = new SpawnPostIir
				{
					FirstRecordLen = 5u,
					MovementMode = 2u,
					HasMoveTaskSlot = true,
					Slot = new SpawnEntrySlot()
				}
			};
		}

		// "Male Template" - 650 captured bytes.
		public static SimpleCharFullUpdateIIR MaleTemplate()
		{
			return new SimpleCharFullUpdateIIR
			{
				Ack = 0xC350u,
				Handle = 0x9FF94u,
				FieldA = 0u,
				Position = new Vector3(266.7f, 5.9240003f, 293.6f),
				Rotation = new Quaternion(0f, 0f, 0f, 1f),
				Playfield = 0x502276ul,
				PreIir = new SpawnPreIir
				{
					Version = 0x05,
					Flags = [0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
					Words = [0u, 0x874D2CA0u, 0u, 0x874D2CA0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x775AEE60u, 0xFFFFFFFFu, 0u, 0u, 0u, 0u, 4u, 0x12Cu, 0u, 0u, 0u, 0x5A993030u, 0u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0x6C39B400u, 0u, 0u, 0u, 0u, 0u, 0u, 0xFFFFFFFFu, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x390u, 0u, 0x60u, 0u, 0x55C1DBE0u, 0u, 0u, 0u, 0u, 0x6C39B610u, 0u]
				},
				CoreCharVersion = 0x41,
				CoreCharIirHeader = 0x8008203u,
				Character = new ProtoCoreCharacterIIR
				{
					Name = "Male Template",
					Stats = new ProtoCoreCharacterIIR.CharacterStats
					{
						Sex = 2u,
						Side = 0u,
						Race = 1u,
						MonsterScale = 0x64u,
						MonsterData = 0x22427u,
						Life = 0x189Cu,
						Health = 0x189Cu,
						MaxStamina = 0x189Cu,
						Stamina = 0x189Cu,
						MaxMana = 0x189Cu,
						Mana = 0x189Cu,
						LooksPackage = 0u,
						MountScale = 0x64u,
						MountFlags = 0u,
						Flags = 0x10000001u,
						HeadMesh = 0x2EC3Bu,
						Level = 1u,
						ApprenticeLevel = 1u,
						Radius = 50,
						Bcc = 0x30C8u,
						BccState = 1u,
						CarsGroup = 0u,
						OrientationMode = 1u,
						VoiceSet = 3u,
						ChatId = 0x332u
					},
					npc_stats = new ProtoCoreCharacterIIR.NpcStats
					{
						SightSkill = 0x14u,
						HearingSkill = 5u,
						SmellingSkill = 1u,
						TemplateId = 0xA77u,
						SuperFamily = 1u,
						NpcFlags = 0x1C0000u,
						NpcType = 4u
					},
					Bcc = new ProtoCoreCharacterIIR.CharacterBcc
					{
						AnimIdleId = 0xA428u,
						AnimIdleTimer = 0f,
						AnimIdleBlend = true,
						AnimFlags = 0u
					},
					CanFall = true,
					MovementMode = 2u,
					InventoryEntries = {
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 0x10u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0x6C7C4F92u,
									Key2 = 0x5E9FF0FEu,
									Key3 = 0xC4F8D7C2u,
									Key4 = 0x434894ABu
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0x6C7C4F92u,
									Key2 = 0x5E9FF0FEu,
									Key3 = 0xC4F8D7C2u,
									Key4 = 0x434894ABu
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0x6C7C4F92u,
									Key2 = 0x5E9FF0FEu,
									Key3 = 0xC4F8D7C2u,
									Key4 = 0x434894ABu
								},
								Template1 = 0x36812u,
								Template2 = 0x36812u,
								Level = 1u,
								Hash = 0x304F5331u
							}
						},
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 0x12u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0xDD60BF99u,
									Key2 = 0x760973F6u,
									Key3 = 0x7949EDCAu,
									Key4 = 0x8DEDC7D2u
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0xDD60BF99u,
									Key2 = 0x760973F6u,
									Key3 = 0x7949EDCAu,
									Key4 = 0x8DEDC7D2u
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0xDD60BF99u,
									Key2 = 0x760973F6u,
									Key3 = 0x7949EDCAu,
									Key4 = 0x8DEDC7D2u
								},
								Template1 = 0x334B15u,
								Template2 = 0x334B15u,
								Level = 1u,
								Hash = 0x43555035u
							}
						}
					}
				},
				Post = new SpawnPostIir
				{
					FirstRecordLen = 5u,
					MovementMode = 2u,
					HasMoveTaskSlot = true,
					Slot = new SpawnEntrySlot()
				}
			};
		}

		// "Female Template" - 738 captured bytes.
		public static SimpleCharFullUpdateIIR FemaleTemplate()
		{
			return new SimpleCharFullUpdateIIR
			{
				Ack = 0xC350u,
				Handle = 0x9FF95u,
				FieldA = 0u,
				Position = new Vector3(264.325f, 6.0125904f, 277.114f),
				Rotation = new Quaternion(0f, 0f, 0f, 1f),
				Playfield = 0x502276ul,
				PreIir = new SpawnPreIir
				{
					Version = 0x05,
					Flags = [0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
					Words = [0u, 0u, 0u, 0x55C1DBF0u, 0u, 0x55C1DC2Cu, 0u, 0x15B6090u, 0x5F4F1600u, 0x40u, 0x55C1DBA0u, 0u, 1u, 0u, 0u, 0u, 0x310u, 0x6Fu, 0u, 0u, 0x6BAED020u, 0u, 0u, 0u, 0u, 0u, 0x55C1DC2Cu, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x390u, 0u, 0x60u, 0u, 0x57C4A010u, 0u, 0u, 0u, 0u, 0u, 0u]
				},
				CoreCharVersion = 0x41,
				CoreCharIirHeader = 0x8203u,
				Character = new ProtoCoreCharacterIIR
				{
					Name = "Female Template",
					Stats = new ProtoCoreCharacterIIR.CharacterStats
					{
						Sex = 3u,
						Side = 0u,
						Race = 1u,
						MonsterScale = 0x64u,
						MonsterData = 0x4785u,
						Life = 0x189Cu,
						Health = 0x189Cu,
						MaxStamina = 0x189Cu,
						Stamina = 0x189Cu,
						MaxMana = 0x189Cu,
						Mana = 0x189Cu,
						LooksPackage = 0u,
						MountScale = 0x64u,
						MountFlags = 0u,
						Flags = 0x10000001u,
						HeadMesh = 0x3228Au,
						Level = 1u,
						ApprenticeLevel = 1u,
						Radius = 50,
						Bcc = 0x30C8u,
						BccState = 1u,
						CarsGroup = 0u,
						OrientationMode = 1u,
						VoiceSet = 3u
					},
					npc_stats = new ProtoCoreCharacterIIR.NpcStats
					{
						SightSkill = 0x14u,
						HearingSkill = 5u,
						SmellingSkill = 1u,
						TemplateId = 0xA78u,
						SuperFamily = 1u,
						NpcFlags = 0x1C0000u,
						NpcType = 4u
					},
					Bcc = new ProtoCoreCharacterIIR.CharacterBcc
					{
						AnimIdleId = 0xA428u,
						AnimIdleTimer = 0f,
						AnimIdleBlend = true,
						AnimFlags = 0u
					},
					CanFall = true,
					MovementMode = 2u,
					InventoryEntries = {
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 5u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0x82530EE7u,
									Key2 = 0x11C4463Fu,
									Key3 = 0x25AB1CE4u,
									Key4 = 0xD1ABA06Fu
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0x82530EE7u,
									Key2 = 0x11C4463Fu,
									Key3 = 0x25AB1CE4u,
									Key4 = 0xD1ABA06Fu
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0x82530EE7u,
									Key2 = 0x11C4463Fu,
									Key3 = 0x25AB1CE4u,
									Key4 = 0xD1ABA06Fu
								},
								Template1 = 0x4F1E2u,
								Template2 = 0x4F1E2u,
								Level = 1u,
								Hash = 0x4D395739u
							}
						},
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 0x10u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0x37AC2FEu,
									Key2 = 0x60E046BFu,
									Key3 = 0x84923D28u,
									Key4 = 0xBAD0F252u
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0x37AC2FEu,
									Key2 = 0x60E046BFu,
									Key3 = 0x84923D28u,
									Key4 = 0xBAD0F252u
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0x37AC2FEu,
									Key2 = 0x60E046BFu,
									Key3 = 0x84923D28u,
									Key4 = 0xBAD0F252u
								},
								Template1 = 0x4F250u,
								Template2 = 0x4F250u,
								Level = 1u,
								Hash = 0x4F504331u
							}
						},
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 0x12u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0xDD60BF99u,
									Key2 = 0x760973F6u,
									Key3 = 0x7949EDCAu,
									Key4 = 0x8DEDC7D2u
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0xDD60BF99u,
									Key2 = 0x760973F6u,
									Key3 = 0x7949EDCAu,
									Key4 = 0x8DEDC7D2u
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0xDD60BF99u,
									Key2 = 0x760973F6u,
									Key3 = 0x7949EDCAu,
									Key4 = 0x8DEDC7D2u
								},
								Template1 = 0x334B15u,
								Template2 = 0x334B15u,
								Level = 1u,
								Hash = 0x43555035u
							}
						}
					}
				},
				Post = new SpawnPostIir
				{
					FirstRecordLen = 5u,
					MovementMode = 2u,
					HasMoveTaskSlot = true,
					Slot = new SpawnEntrySlot()
				}
			};
		}

		// "Stygian Slaver" - 859 captured bytes.
		public static SimpleCharFullUpdateIIR StygianSlaver()
		{
			return new SimpleCharFullUpdateIIR
			{
				Ack = 0xC350u,
				Handle = 0x9FF96u,
				FieldA = 0u,
				Position = new Vector3(266.37567f, 9.913f, 317.2214f),
				Rotation = new Quaternion(0f, 0.106486f, 0f, 0.994314f),
				Playfield = 0x502276ul,
				PreIir = new SpawnPreIir
				{
					Version = 0x05,
					Flags = [0x00, 0x04, 0x00, 0x00, 0x00, 0x00],
					Words = [0u, 0u, 0u, 0x5F75F7E0u, 0u, 0x5F75F81Cu, 0u, 0x15B6090u, 0x56E512F0u, 0x40u, 0x5F75F790u, 0u, 1u, 0u, 0u, 0u, 0x310u, 0xC9u, 0u, 0u, 0x45B18FF0u, 0u, 0u, 0u, 0u, 0u, 0x5F75F81Cu, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x390u, 0u, 0x60u, 0u, 0x5766DC90u, 0u, 0u, 0u, 0u, 0u, 0u]
				},
				CoreCharVersion = 0x41,
				CoreCharIirHeader = 0x8008203u,
				Character = new ProtoCoreCharacterIIR
				{
					Name = "Stygian Slaver",
					Stats = new ProtoCoreCharacterIIR.CharacterStats
					{
						Sex = 2u,
						Side = 0u,
						Race = 3u,
						MonsterScale = 0x64u,
						MonsterData = 0x22426u,
						Life = 0x189Cu,
						Health = 0x189Cu,
						MaxStamina = 0x189Cu,
						Stamina = 0x189Cu,
						MaxMana = 0x189Cu,
						Mana = 0x189Cu,
						LooksPackage = 0u,
						MountScale = 0x64u,
						MountFlags = 0u,
						Flags = 0x10000001u,
						HeadMesh = 0x2EC3Eu,
						Level = 1u,
						ApprenticeLevel = 1u,
						Radius = 50,
						Bcc = 0x30C8u,
						BccState = 1u,
						CarsGroup = 3u,
						OrientationMode = 1u,
						VoiceSet = 1u,
						ChatId = 0x332u
					},
					npc_stats = new ProtoCoreCharacterIIR.NpcStats
					{
						SightSkill = 0xFu,
						HearingSkill = 0xAu,
						SmellingSkill = 5u,
						TemplateId = 0x3CBu,
						SuperFamily = 1u,
						NpcFlags = 0x1C0000u,
						NpcType = 4u
					},
					Bcc = new ProtoCoreCharacterIIR.CharacterBcc
					{
						AnimIdleId = 0xA428u,
						AnimIdleTimer = 0f,
						AnimIdleBlend = true,
						AnimFlags = 0u
					},
					CanFall = true,
					MovementMode = 2u,
					InventoryEntries = {
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 0xBu,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0xCC1E2C06u,
									Key2 = 0xECF6161Bu,
									Key3 = 0xE5ECB5CBu,
									Key4 = 0xAC912C59u
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0xCC1E2C06u,
									Key2 = 0xECF6161Bu,
									Key3 = 0xE5ECB5CBu,
									Key4 = 0xAC912C59u
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0xCC1E2C06u,
									Key2 = 0xECF6161Bu,
									Key3 = 0xE5ECB5CBu,
									Key4 = 0xAC912C59u
								},
								Template1 = 0x46343u,
								Template2 = 0x46343u,
								Level = 1u,
								Hash = 0x5A39494Du
							}
						},
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 0xEu,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0x4C29C216u,
									Key2 = 0x5CDF262Du,
									Key3 = 0x63C43249u,
									Key4 = 0x2D768088u
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0x4C29C216u,
									Key2 = 0x5CDF262Du,
									Key3 = 0x63C43249u,
									Key4 = 0x2D768088u
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0x4C29C216u,
									Key2 = 0x5CDF262Du,
									Key3 = 0x63C43249u,
									Key4 = 0x2D768088u
								},
								Template1 = 0x2E835u,
								Template2 = 0x2E835u,
								Level = 1u,
								Hash = 0x4D524354u
							}
						},
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 0x10u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0x40C16840u,
									Key2 = 0xB8BF432Cu,
									Key3 = 0x3212BF2Eu,
									Key4 = 0xE397CE5Bu
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0x40C16840u,
									Key2 = 0xB8BF432Cu,
									Key3 = 0x3212BF2Eu,
									Key4 = 0xE397CE5Bu
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0x40C16840u,
									Key2 = 0xB8BF432Cu,
									Key3 = 0x3212BF2Eu,
									Key4 = 0xE397CE5Bu
								},
								Template1 = 0x46340u,
								Template2 = 0x46340u,
								Level = 1u,
								Hash = 0x5053594Eu
							}
						},
						new ProtoCoreCharacterIIR.InventoryEntry
						{
							Pos = 0x12u,
							Item = new ProtoAcgItem
							{
								Key1 = new ProtoDecryptionKey
								{
									Key1 = 0xA8D805FAu,
									Key2 = 0x3222563Cu,
									Key3 = 0x9D7819F4u,
									Key4 = 0x58C48C48u
								},
								Key2 = new ProtoDecryptionKey
								{
									Key1 = 0xA8D805FAu,
									Key2 = 0x3222563Cu,
									Key3 = 0x9D7819F4u,
									Key4 = 0x58C48C48u
								},
								Key3 = new ProtoDecryptionKey
								{
									Key1 = 0xA8D805FAu,
									Key2 = 0x3222563Cu,
									Key3 = 0x9D7819F4u,
									Key4 = 0x58C48C48u
								},
								Template1 = 0x2187Bu,
								Template2 = 0x2187Bu,
								Level = 1u,
								Hash = 0x48463459u
							}
						}
					}
				},
				Post = new SpawnPostIir
				{
					FirstRecordLen = 5u,
					MovementMode = 2u,
					HasMoveTaskSlot = true,
					Slot = new SpawnEntrySlot
					{
						Appearance = {
							new AppearanceEntry
							{
								MorphId = 0x324u,
								Value = 0.7686341f,
								Word2 = 0u,
								Text = ""
							},
							new AppearanceEntry
							{
								MorphId = 0x343u,
								Value = 0.12378088f,
								Word2 = 0u,
								Text = ""
							}
						}
					}
				}
			};
		}

		// "Character35021512" - 447 captured bytes.
		public static SimpleCharFullUpdateIIR LiveAvatarPreview()
		{
			return new SimpleCharFullUpdateIIR
			{
				Ack = 0xC350u,
				Handle = 0x2000001u,
				FieldA = 0u,
				Position = new Vector3(260f, 0f, 290f),
				Rotation = new Quaternion(0f, 0f, 0f, 1f),
				Playfield = 0x502276ul,
				PreIir = new SpawnPreIir
				{
					Version = 0x05,
					Words = [0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x6Fu, 0u, 0x41700000u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u]
				},
				CoreCharVersion = 0x41,
				CoreCharIirHeader = 0x8208u,
				Character = new ProtoCoreCharacterIIR
				{
					Name = "Character35021512",
					Stats = new ProtoCoreCharacterIIR.CharacterStats
					{
						Sex = 0u,
						Side = 0u,
						Race = 1u,
						MonsterScale = 0x64u,
						MonsterData = 0u,
						Life = 0x1F40u,
						Health = 0x1F40u,
						MaxStamina = 0x1388u,
						Stamina = 0x64u,
						MaxMana = 0u,
						Mana = 0u,
						LooksPackage = 0u,
						MountScale = 0x64u,
						MountFlags = 0u,
						Flags = 1u,
						HeadMesh = 0u,
						Level = 1u,
						ApprenticeLevel = 1u,
						Radius = 50,
						Bcc = 0x30C8u,
						BccState = 8u,
						CarsGroup = 0u,
						OrientationMode = 1u,
						VoiceSet = 5u
					},
					player_stats = new ProtoCoreCharacterIIR.PlayerStats
					{
						Strength = 0xAu,
						SubscriptionFlags = 0x301u,
						HardcoreLevel = 0
					},
					Bcc = new ProtoCoreCharacterIIR.CharacterBcc
					{
						AnimIdleId = 0x12E78u,
						AnimIdleTimer = 0f,
						AnimIdleBlend = true,
						AnimFlags = 0u
					},
					CanFall = false,
					MovementMode = 2u
				},
				Post = new SpawnPostIir
				{
					FirstRecordLen = 4u,
					MovementMode = 2u,
					HasMoveTaskSlot = false,
					Slot = new SpawnEntrySlot()
				}
			};
		}

		// The five fixed pedestals, in the order retail streams them. `LiveAvatarPreview` is not here on
		// purpose - it takes the session's client instance as its handle, so it is not a constant of the scene.
		public static IReadOnlyList<System.Func<SimpleCharFullUpdateIIR>> Pedestals =>
		[
			StygianFemale,
			Saddur,
			MaleTemplate,
			FemaleTemplate,
			StygianSlaver,
		];

		// Every record of the scene, pedestals first - the order the burst streams them.
		public static IReadOnlyList<System.Func<SimpleCharFullUpdateIIR>> All =>
		[
			StygianFemale,
			Saddur,
			MaleTemplate,
			FemaleTemplate,
			StygianSlaver,
			LiveAvatarPreview,
		];
	}
}
