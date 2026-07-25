using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Faolan.Web.Services
{
	// Display-facing item details for the viewer's slots + tooltips. Sell = vendor sell price (round(Value/10), 0 when
	// the value is unknown or the item is no-sell). Count is the stack size (1 for worn gear).
	public readonly record struct ItemInfo(uint Id, string Name, string Type, byte Level, uint Value, uint Sell, uint Count)
	{
		public static ItemInfo From(Item item, uint count = 1)
		{
			var value = item.Value;
			return new ItemInfo(item.Id, string.IsNullOrWhiteSpace(item.Name)
				? $"Item {item.Id}"
				: item.Name, item.Type.ToString(), item.Level, value, value > 0
				? (uint)Math.Round(value / 10.0)
				: 0, count);
		}
	}

	// Builds a character's 3D model (glTF/glb) on demand from the client RDB, reusing the Faolan.Core asset pipeline
	// (#275). Holds one RdbReader for the process (it memory-maps the index + keeps the data files open). Race/sex ->
	// body monsterdata id is a hardcoded table (BodyMonster) resolved via the CHARACTERCREATOR data (1010700, replacing
	// the old mesh-index table); per-character head/equipment is resolved from the stored appearance (#275). Bodies that
	// are CDN-only fall back to a local one so the viewer always renders something.
	public sealed class CharacterModelService
	{
		// Head base mesh by sex. The 3 European starting races (Aquilonian/Cimmerian/Stygian) share one head mesh and
		// differ by morphs + face texscript, not by mesh (only Khitai has its own head). These are the shared European
		// heads (female = beautiful_famale_heads.max, male = male_heads_old.max); per-character morph/texture is #275.
		private const uint FemaleHead = 4502887;
		private const uint MaleHead = 4821637;

		private static readonly AppearanceOptions EmptyOptions = new([], [], [], [], [], [], [], 0, 0, new Dictionary<string, int[]>());

		// (race, sex) -> the body's CHARACTERCREATOR monsterdata id (RDB type 1010700 <body monsterdataid>, the
		// authoritative per-race/sex body). The MonsterData in turn names the body's skinned RenderMesh, resolved via
		// RdbAssetExtractor.ResolveBodyRenderMesh (a <mesh> is either a 1010030 mesh-index - the male bodies - or a 1010001
		// RenderMesh directly - the female bodies). This replaces the old hardcoded MESHINDEXFILE table, which pointed the
		// FEMALE bodies at a censored/wrong atlas (no nipples + a torso/limb tone seam) and at CDN-only meshes; the
		// monsterdata bodies are the correct, locally-present ones for all three races (#275).
		private static readonly Dictionary<(Race, Sex), uint> BodyMonster = new()
		{
			[(Race.Aquilonian, Sex.Male)] = 1091335,
			[(Race.Cimmerian, Sex.Male)] = 1091337,
			[(Race.Stygian, Sex.Male)] = 1091339,
			[(Race.Aquilonian, Sex.Female)] = 1091334,
			[(Race.Cimmerian, Sex.Female)] = 1091336,
			[(Race.Stygian, Sex.Female)] = 1091338
		};

		// Deterministic renders are cached for a while after last use; the empty array is the "no model" sentinel so a
		// 404 result is cached too (a slot with no display mesh isn't rebuilt on every draw).
		private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
		private static readonly byte[] None = [];

		// A map's top-down image (RDB type 1010209 keyed by playfield id) as a ready-to-serve PNG, or null when the
		// RDB is unavailable, the map has no local image (CDN-gated), or no PNG is embedded. The record is a small
		// [type][id][count] preamble followed by the raw PNG, so we carve from the PNG signature.
		private static readonly byte[] PngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

		// A map's world extent (tilemap Width/Height in world units) from the RDB playfield description (1070003),
		// used to scale the live radar. Null when the RDB is unavailable or the playfield is CDN-gated. Cached.
		private readonly ConcurrentDictionary<uint, (int W, int H)?> _mapBounds = new();
		private readonly IDbContextFactory<DatabaseContext> _dbFactory;
		private readonly IMemoryCache _cache;
		private readonly Lock _gate = new();
		private readonly string _rdbDir;
		private readonly string? _cdnUrl;

		// Linked to every cached render so ClearRenderCache can evict them all at once.
		private CancellationTokenSource _cacheReset = new();
		private IReadOnlyList<ArmorSetCatalog.ArmorSet>? _armorSets;
		private RdbReader? _rdb;

		public bool RdbAvailable => Directory.Exists(_rdbDir) && File.Exists(Path.Combine(_rdbDir, "le.idx"));

		public CharacterModelService(IConfiguration config, IDbContextFactory<DatabaseContext> dbFactory, IMemoryCache cache)
		{
			_dbFactory = dbFactory;
			_cache = cache;
			_rdbDir = config["RdbDir"] is { Length: > 0 } dir
				? dir
				: throw new InvalidOperationException("RdbDir not configured - set it to the client's RDB folder in appsettings.Development.json or the RdbDir env var");
			// Optional ContentMirror proxy base for on-demand (CDN-only) resources, e.g. the CDN race bodies that aren't
			// in the local RDB. Store-and-forward, so requesting one archives it for the future (#275, [[aoc_content_cdn_mirror]]).
			_cdnUrl = config["CdnUrl"];
		}

		// The wearable armour-set catalogue (built once from the RDB, then cached). Optionally filtered to a sex.
		public IReadOnlyList<ArmorSetCatalog.ArmorSet> ArmorSets(Sex? sex = null)
		{
			_armorSets ??= ArmorSetCatalog.Build(Reader());
			var want = sex == Sex.Female
				? "Female"
				: sex == Sex.Male
					? "Male"
					: null;
			return want == null
				? _armorSets
				: _armorSets.Where(s => s.Gender == want || s.Gender == "Unisex").ToList();
		}

		public byte[]? BuildCharacter(Race race, Sex sex, uint? hairRmId = null)
		{
			var rdb = Reader();
			var bodyRm = BodyRenderMesh(rdb, race, sex);
			if (bodyRm == 0)
				return null;

			var headId = sex == Sex.Female
				? FemaleHead
				: MaleHead;
			return RdbAssetExtractor.BuildCharacterGlb(rdb, bodyRm, headId, hairRmId);
		}

		// Build a specific character by id, using their stored appearance (CharacterBodyResources) - currently the
		// chosen hairstyle (*hairmesh_NNN, where NNN is the hair rendermesh id). Head morph/face texscript still pending
		// the CHARACTERCREATOR (1010700) decode (#275); HeadMesh is a creator index, not a resource id.
		public byte[]? BuildCharacterById(uint characterId, IReadOnlyList<uint>? equipMeshIndexIds = null, bool rig = false, IReadOnlySet<string>? hiddenSlots = null)
		{
			using var db = _dbFactory.CreateDbContext();
			var character = db.Characters.AsNoTracking().FirstOrDefault(c => c.Id == characterId);
			if (character == null)
				return null;

			var body = db.CharacterBodies.AsNoTracking().FirstOrDefault(b => b.CharacterId == characterId);
			var resources = db.CharacterBodyResources.AsNoTracking().Where(r => r.CharacterId == characterId).Select(r => r.Ref).ToList();
			var hairRmId = ParseResourceId(resources.FirstOrDefault(r => r.StartsWith("*hairmesh_")));

			// Body-proportion morphs (cluster-scale deform of the body mesh).
			var morphs = body == null
				? (BodyMorphDeformer.Morphs?)null
				: new BodyMorphDeformer.Morphs(body.Arm, body.Chest, body.Bosom, body.Stomach, body.Arse, body.Thigh, body.Leg);

			// CharacterBody.HeadMesh is stored in editor 2x units; the real CHARACTERCREATOR head monsterdataid is
			// HeadMesh/2 ([[CaptureCharacterImporter]]).
			var headMonster = body is { HeadMesh: > 0 }
				? body.HeadMesh / 2
				: (uint?)null;

			// No explicit overlay => dress the character in their ACTUAL equipped gear, resolved from the item templates
			// (#275, ItemLookResolver). Worn armour meshes are authored in body bind-pose space, so they overlay in the
			// static view and skin to the body skeleton in the rig path alike. hiddenSlots drops individual pieces (the
			// viewer's per-slot show/hide toggle).
			equipMeshIndexIds ??= ResolveEquippedMeshes(characterId, character.Sex, hiddenSlots);

			return BuildFromSpec(Reader(), character.Sex, character.Race, hairRmId, headMonster, morphs, resources, equipMeshIndexIds, rig);
		}

		// Build a character glb from an EXPLICIT appearance spec instead of the stored one - the live-preview path for the
		// web appearance editor (nothing is persisted; the editor calls this on every control change). texRefs are the
		// chosen *hairmesh_/*texscript_ resource refs; morphs are the 7 body-proportion sliders; headMonster is the
		// CHARACTERCREATOR head monsterdataid (HeadMesh/2). Gear is intentionally omitted so skin/tattoos are visible.
		//
		// STRICT (no fallbacks): if a required asset is missing, returns the reason in Error instead of silently
		// substituting a default/local body - so the editor surfaces exactly what is unavailable (e.g. a CDN-only body).
		public (byte[]? Glb, string? Error) BuildCharacterCustom(Sex sex, Race race, uint? hairRmId, uint? headMonster, BodyMorphDeformer.Morphs? morphs, IReadOnlyList<string>? texRefs)
		{
			var rdb = Reader();

			if (!BodyMonster.TryGetValue((race, sex), out var bodyMonster))
				return (null, $"No body monsterdata is mapped for {race} {sex}.");

			if (RdbAssetExtractor.ResolveBodyRenderMesh(rdb, bodyMonster) is not { } bodyRm)
				return (null, $"Body monsterdata {bodyMonster} for {race} {sex} could not be resolved (CDN-only or missing in the local RDB).");

			if (headMonster is not > 0)
				return (null, "No head selected (head monsterdata id required).");

			var headMeshes = RdbAssetExtractor.ResolveHeadMeshes(rdb, headMonster.Value);
			if (headMeshes.Count == 0)
				return (null, $"Head monsterdata {headMonster} was not found in the RDB.");

			var (skinTint, hairTint, eyeTex, decals) = ResolveTexScripts(rdb, texRefs ?? []);

			var glb = RdbAssetExtractor.BuildCharacterFromParts(rdb, bodyRm, headMeshes, hairRmId, skinTint, hairTint, eyeTex, decals, morphs, null, true);
			return glb == null
				? (null, $"Body RenderMesh {bodyRm} for {race} {sex} could not be built.")
				: (glb, null);
		}

		// Build a character as a posable rig (skinned glb): the full assembled character - body (morphs + skin + decals) +
		// head + hair - skinned to the auto-selected skeleton, so the viewer's pose slider can articulate it. The body
		// skins to its own bones; the rigid head/hair are bound to the Head bone. (Gear skinning is the separate #275 work.)
		public byte[]? BuildCharacterRig(uint characterId, IReadOnlySet<string>? hiddenSlots = null)
		{
			return BuildCharacterById(characterId, null, true, hiddenSlots);
		}

		// Build a single named model (any MESHINDEXFILE id) - lets the viewer show equipment/props too.
		public byte[]? BuildModel(uint meshIndexId)
		{
			var rdb = Reader();
			var subs = RdbAssetExtractor.ReadModel(rdb, meshIndexId);
			return subs is { Count: > 0 }
				? GlbWriter.Build(subs)
				: null;
		}

		// Build a single RenderMesh (type 1010001) directly by id - for inspecting head/eye/hair meshes (#275).
		public byte[]? BuildRenderMesh(uint renderMeshId)
		{
			var rdb = Reader();
			var subs = RdbAssetExtractor.ReadRenderMeshDirect(rdb, renderMeshId);
			return subs is { Count: > 0 }
				? GlbWriter.Build(subs)
				: null;
		}

		// Build a SKINNED glb for a skinned model (body / equipment piece): geometry + the character skeleton as a glTF
		// skin, so the result is posable (#275). skeletonId null => auto-select the rig matching the mesh's bone palette.
		public byte[]? BuildRig(uint meshIndexId, uint? skeletonId = null)
		{
			return RdbAssetExtractor.BuildSkinnedModelGlb(Reader(), meshIndexId, skeletonId);
		}

		// Drop every cached item model / map image (e.g. after a faolan.db re-import). Cancels the shared token so all
		// linked entries are evicted, then swaps in a fresh one for new entries.
		public void ClearRenderCache()
		{
			var old = _cacheReset;
			_cacheReset = new CancellationTokenSource();
			old.Cancel();
			old.Dispose();
		}

		// Diagnostic: surface the appearance-resolution chain for a character.
		public object DebugCharacter(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			var character = db.Characters.AsNoTracking().FirstOrDefault(c => c.Id == characterId);
			var body = db.CharacterBodies.AsNoTracking().FirstOrDefault(b => b.CharacterId == characterId);
			var resources = db.CharacterBodyResources.AsNoTracking().Where(r => r.CharacterId == characterId).Select(r => r.Ref).ToList();
			var rdb = Reader();
			var monsterId = body is { HeadMesh: > 0 }
				? body.HeadMesh / 2
				: 0;
			var headMeshes = monsterId > 0
				? RdbAssetExtractor.ResolveHeadMeshes(rdb, monsterId)
				: [];

			// Body resolution: the CHARACTERCREATOR monsterdata id + the skinned RenderMesh it resolves to.
			var bodyMonster = character != null
				? BodyMonster.GetValueOrDefault((character.Race, character.Sex))
				: 0;
			var bodyRenderMesh = character != null
				? BodyRenderMesh(rdb, character.Race, character.Sex)
				: 0;

			// Resolve each *texscript_NNN to its mesh-target + op so the skin/hair/eye classification is visible.
			var texscripts = resources
				.Where(r => r.StartsWith("*texscript_") && ParseResourceId(r) != null)
				.Select(r =>
				{
					var id = ParseResourceId(r)!.Value;
					var ts = RdbAssetExtractor.LoadTexScript(rdb, id);
					return ts == null
						? new
						{
							id,
							found = false,
							meshTarget = 0,
							kind = "?",
							r = 0,
							g = 0,
							b = 0,
							textureId = 0u
						}
						: new
						{
							id,
							found = true,
							meshTarget = ts.MeshTarget,
							kind = ts.Op.Kind.ToString(),
							r = (int)ts.Op.R,
							g = (int)ts.Op.G,
							b = (int)ts.Op.B,
							textureId = ts.Op.TextureId
						};
				})
				.ToList();
			var (skinTint, hairTint, eyeTex, decals) = ResolveTexScripts(rdb, resources);

			return new
			{
				found = character != null,
				race = character?.Race.ToString(),
				sex = character?.Sex.ToString(),
				hasBody = body != null,
				headMeshRaw = body?.HeadMesh,
				monsterId,
				headMeshes,
				bodyMonster,
				bodyRenderMesh,
				resources,
				texscripts,
				resolved = new
				{
					skin = skinTint is { } s
						? $"#{s.R:X2}{s.G:X2}{s.B:X2}"
						: null,
					hair = hairTint is { } h
						? $"#{h.R:X2}{h.G:X2}{h.B:X2}"
						: null,
					eyeTexture = eyeTex,
					bodyDecals = decals
				}
			};
		}

		// Diagnostic: decode an FCTX texture to PNG (for inspecting tattoo decal textures), or null if absent.
		public byte[]? DecodeTexture(uint type, uint id)
		{
			return RdbAssetExtractor.DecodeTexturePng(Reader(), type, id);
		}

		// Diagnostic: report whether the body's left/right UVs are mirrored.
		public object DescribeBodyUv(uint bodyMeshIndexId)
		{
			return RdbAssetExtractor.DescribeBodyUv(Reader(), bodyMeshIndexId);
		}

		// A character's equipped items keyed by the viewer's slot labels (only non-empty slots), to populate the
		// equipment paper-doll. Item id + display name; the icon is resolved separately from the RDB item template.
		public IReadOnlyDictionary<string, ItemInfo> Equipment(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			var e = db
				.CharacterEquipment.AsNoTracking()
				.Include(x => x.Head)
				.Include(x => x.Neck)
				.Include(x => x.Shoulder)
				.Include(x => x.Cloak)
				.Include(x => x.Chest)
				.Include(x => x.Wrist)
				.Include(x => x.Hands)
				.Include(x => x.Belt)
				.Include(x => x.Boots)
				.Include(x => x.RingLeft)
				.Include(x => x.RingRight)
				.Include(x => x.Legs)
				.Include(x => x.MainHandLeft)
				.Include(x => x.OffHandLeft)
				.Include(x => x.Bag)
				.Include(x => x.FirstPosBag)
				.FirstOrDefault(x => x.CharacterId == characterId);
			if (e == null)
				return new Dictionary<string, ItemInfo>();

			var map = new (string Slot, Item? Item)[]
			{
				("Head", e.Head), ("Neck", e.Neck), ("Shoulder", e.Shoulder), ("Cloak", e.Cloak), ("Chest", e.Chest), ("Wrist", e.Wrist), ("Hands", e.Hands), ("Belt", e.Belt), ("Boots", e.Boots),
				("Ring L", e.RingLeft), ("Ring R", e.RingRight), ("Legs", e.Legs), ("Main Hand", e.MainHandLeft), ("Off Hand", e.OffHandLeft), ("Tokens", e.Bag), ("Bag", e.FirstPosBag)
			};

			var result = new Dictionary<string, ItemInfo>();
			foreach (var (slot, item) in map)
			{
				if (item != null)
					result[slot] = ItemInfo.From(item);
			}

			return result;
		}

		// One clip's decoded keyframes (type 1010003, by rdbname), shaped for the viewer to build THREE.KeyframeTracks:
		// per-bone position (vec3) + rotation (quat xyzw) with their key times (seconds). Null if unavailable/variant.
		public object? GetAnimation(uint rdbname)
		{
			if (!RdbAvailable || AnimationCatalog.DecodeClip(Reader(), rdbname) is not { } clip)
				return null;

			return new
			{
				rdbname = clip.Rdbname,
				duration = clip.Duration,
				bones = clip.Bones.Select(b => new
				{
					bone = b.Bone,
					posTimes = b.PosTimes,
					pos = b.Pos,
					rotTimes = b.RotTimes,
					rot = b.Rot
				})
			};
		}

		// The appearance choices legal for a given SEX+RACE, read from the imported AppearanceChoice rows (built by
		// the rdb-appearance importer from the client CHARACTERCREATOR table + resource names). Colours carry their hex
		// (swatches); tattoos/heads carry a friendly Name, hair falls back to an ordinal label.
		public AppearanceOptions GetAppearanceOptions(Sex sex, Race race)
		{
			using var db = _dbFactory.CreateDbContext();
			var rows = db.AppearanceChoices.AsNoTracking().Where(c => c.Sex == sex && c.Race == race).OrderBy(c => c.Sequence).ToList();
			if (rows.Count == 0)
				return EmptyOptions;

			var morphLimits = rows.Where(r => r.Category == "morph").GroupBy(r => r.Value).ToDictionary(g => g.Key, g => new[] { g.First().Min, g.First().Max });
			var height = rows.FirstOrDefault(r => r.Category == "height");

			return new AppearanceOptions(Cat("hair"), Cat("haircolor"), Cat("eyecolor"), Cat("skincolor"), Cat("bodymarkings"), Cat("facemarkings"), Cat("head"), height?.Min ?? 0, height?.Max ?? 0,
				morphLimits);

			List<AppearanceOption> Cat(string category)
			{
				return rows
					.Where(r => r.Category == category)
					.Select(r => new AppearanceOption(r.Value, string.IsNullOrEmpty(r.Name)
						? $"#{r.Sequence + 1}"
						: r.Name, r.Color))
					.ToList();
			}
		}

		// The character's CURRENT appearance selections, to seed the editor when a character is opened: the 7 body
		// morphs, the head (editor 2x units), and the chosen resource ref in each render-role category (or "" = none).
		public AppearanceCurrent GetCurrentAppearance(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			var body = db.CharacterBodies.AsNoTracking().FirstOrDefault(b => b.CharacterId == characterId);
			var byCat = db
				.CharacterBodyResources.AsNoTracking()
				.Where(r => r.CharacterId == characterId)
				.Join(db.MorphTargets.AsNoTracking(), r => r.Ref, m => m.Name, (r, m) => new
				{
					m.Category,
					r.Ref
				})
				.ToList()
				.GroupBy(x => x.Category)
				.ToDictionary(g => g.Key, g => g.First().Ref);

			return new AppearanceCurrent(body?.Arm ?? 0, body?.Chest ?? 0, body?.Bosom ?? 0, body?.Stomach ?? 0, body?.Arse ?? 0, body?.Thigh ?? 0, body?.Leg ?? 0, body?.HeadMesh ?? 0, Cat(3), Cat(6),
				Cat(7), Cat(8), Cat(9), Cat(10));

			string Cat(byte c)
			{
				return byCat.GetValueOrDefault(c, "");
			}
		}

		// Cheap check (no decompress / no HTTP) for whether a top-down image is reachable for the map — local OR
		// fetchable from the configured CDN mirror.
		public bool HasMapImage(uint mapId)
		{
			var rdb = RdbAvailable
				? Reader()
				: null;
			return rdb != null && rdb.TryGet(1010209, mapId, out var e) && e.Size > 0 && rdb.CanRead(e);
		}

		// The character's carried (non-worn) items — the backpack/quest/resource contents — as a flat, display-ordered
		// list (container, then slot), each tagged with its tab. Empty slots are dropped; the viewer pages what remains.
		// Containers: 104 = normal backpack, 112 = resources, 113 = quest (Faolan.Core/Enums/ItemType.cs).
		public IReadOnlyList<(ItemInfo Info, string Tab)> Inventory(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			return db
				.CharacterInventory.AsNoTracking()
				.Where(x => x.CharacterId == characterId && x.ItemId != null)
				.Include(x => x.Item)
				.OrderBy(x => x.Container)
				.ThenBy(x => x.Slot)
				.AsEnumerable()
				.Select(x => (ItemInfo.From(x.Item!, x.Count), x.Container switch
				{
					ContainerKind.Quest    => "Quest",
					ContainerKind.Resource => "Resources",
					_                      => "Normal"
				}))
				.ToList();
		}

		// Build an item's 3D display model (the inventory mesh shown in equipment slots), or null if it has none
		// (armour pieces carry only a worn-look id, not a display mesh - the open gear RE).
		public byte[]? ItemModel(uint itemId)
		{
			return Cached($"item:{itemId}", () =>
			{
				using var db = _dbFactory.CreateDbContext();
				var item = db.Items.AsNoTracking().FirstOrDefault(i => i.Id == itemId);
				return item == null
					? null
					: RdbAssetExtractor.BuildItemModelGlb(Reader(), itemId, item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4);
			});
		}

		// The authentic ANIMSYS clips available for a skeleton (#292), for the viewer's animation dropdown. Defaults to
		// the humanoid rig the viewer skins to; CDN-only clips resolve through the ContentMirror via Reader(). Returns
		// (name, rdbname, duration) sorted by name.
		public IReadOnlyList<AnimationCatalog.ClipInfo> ListAnimations(uint? skeletonId = null)
		{
			return RdbAvailable
				? AnimationCatalog.ListClips(Reader(), skeletonId ?? RdbAssetExtractor.HumanoidSkeletonId)
				: [];
		}

		public (int Width, int Height)? MapBounds(uint mapId)
		{
			if (!RdbAvailable)
				return null;

			return _mapBounds.GetOrAdd(mapId, id =>
			{
				try
				{
					var rdb = Reader();
					if (!rdb.TryGet(1070003, id, out var e) || e.Size == 0 || e.File == 0xff)
						return null;

					var xml = Encoding.Latin1.GetString(rdb.ReadDecompressed(e));
					var m = Regex.Match(xml, "<tilemap[^>]*Width=\"(\\d+)\"[^>]*Height=\"(\\d+)\"");
					return m.Success
						? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))
						: null;
				}
				catch
				{
					return null;
				}
			});
		}

		public byte[]? MapImage(uint mapId)
		{
			if (!RdbAvailable)
				return null;

			return Cached($"map:{mapId}", () =>
			{
				try
				{
					var rdb = Reader();
					if (!rdb.TryGet(1010209, mapId, out var e) || e.Size == 0 || !rdb.CanRead(e))
						return null;

					// ReadDecompressed transparently fetches CDN-gated (file == 0xff) records via the ContentMirror.
					var body = rdb.ReadDecompressed(e);
					var idx = body.AsSpan().IndexOf(PngSig);
					return idx < 0
						? null
						: body[idx..];
				}
				catch
				{
					return null;
				}
			});
		}

		// Diagnostic: raw decompressed bytes of an RDB resource (binary blob analysis, e.g. rendermesh DPPM morph chunk).
		public byte[]? RawResourceBytes(uint type, uint id)
		{
			return RdbAssetExtractor.RawBytes(Reader(), type, id);
		}

		// Diagnostic: the decompressed text of an RDB resource (XML resources like TexScript 1010035), or null if absent.
		public string? RawResourceText(uint type, uint id)
		{
			return RdbAssetExtractor.RawText(Reader(), type, id);
		}

		// Resolve a character's worn armour MESHINDEXFILE ids from their equipped items (#275). Each equipped item's
		// decrypted template carries its worn mesh(es) in field 9 (ItemLookResolver); weapons/jewellery carry none.
		// Male/female variants of one garment collapse to the character's sex. Login-independent - RDB + faolan.db only.
		public IReadOnlyList<uint> ResolveEquippedMeshes(uint characterId, Sex sex, IReadOnlySet<string>? hiddenSlots = null)
		{
			using var db = _dbFactory.CreateDbContext();
			var eq = db
				.CharacterEquipment.AsNoTracking()
				.Include(e => e.Head)
				.Include(e => e.Chest)
				.Include(e => e.Legs)
				.Include(e => e.Hands)
				.Include(e => e.Boots)
				.Include(e => e.Shoulder)
				.Include(e => e.Wrist)
				.Include(e => e.Belt)
				.Include(e => e.Cloak)
				.Include(e => e.Bag)
				.FirstOrDefault(e => e.CharacterId == characterId);
			if (eq == null)
				return [];

			// Slot names match the viewer's equipment paper-doll labels so a hidden slot maps 1:1 to the hide toggle.
			var slots = new (string Slot, Item? Item)[]
			{
				("Head", eq.Head), ("Chest", eq.Chest), ("Legs", eq.Legs), ("Hands", eq.Hands), ("Boots", eq.Boots), ("Shoulder", eq.Shoulder), ("Wrist", eq.Wrist), ("Belt", eq.Belt),
				("Cloak", eq.Cloak), ("Tokens", eq.Bag)
			};

			var rdb = Reader();
			var want = sex == Sex.Female
				? "Female"
				: "Male";
			var seen = new HashSet<uint>();
			var result = new List<uint>();
			foreach (var (slot, item) in slots)
			{
				if (hiddenSlots != null && hiddenSlots.Contains(slot))
					continue;

				var template = DecryptTemplate(rdb, item);
				if (template == null)
					continue;

				foreach (var meshId in ItemLookResolver.WornMeshIds(rdb, template, want))
				{
					if (seen.Add(meshId))
						result.Add(meshId);
				}
			}

			return result;
		}

		// Persist an edited appearance to the character's CharacterBody (morphs + head) and CharacterBodyResources
		// (the chosen hair/colour/tattoo refs, re-sequenced). This is what the editor's Save writes; the web viewer
		// reads these back so the change sticks. Returns false when the character does not exist.
		public bool SaveAppearance(uint characterId, AppearanceSpec spec)
		{
			using var db = _dbFactory.CreateDbContext();
			if (!db.Characters.Any(c => c.Id == characterId))
				return false;

			var body = db.CharacterBodies.FirstOrDefault(b => b.CharacterId == characterId);
			if (body == null)
			{
				body = new CharacterBody
				{
					CharacterId = characterId
				};
				db.CharacterBodies.Add(body);
			}

			body.Arm = spec.Arm;
			body.Chest = spec.Chest;
			body.Bosom = spec.Bosom;
			body.Stomach = spec.Stomach;
			body.Arse = spec.Arse;
			body.Thigh = spec.Thigh;
			body.Leg = spec.Leg;
			if (spec.HeadMesh > 0)
				body.HeadMesh = spec.HeadMesh;

			db.CharacterBodyResources.RemoveRange(db.CharacterBodyResources.Where(r => r.CharacterId == characterId));
			var seq = 0;
			foreach (var reference in spec.Refs.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct())
			{
				db.CharacterBodyResources.Add(new CharacterBodyResource
				{
					CharacterId = characterId,
					Sequence = seq++,
					Ref = reference
				});
			}

			db.SaveChanges();

			PropagateToScaffold(characterId);
			return true;
		}

		// Armory search: named item templates matching an optional name fragment + type filter, paged. Returns the
		// page of items (as display ItemInfo) and the total match count for the pager.
		public (IReadOnlyList<ItemInfo> Items, int Total) SearchItems(string? query, int? type, int page, int pageSize)
		{
			using var db = _dbFactory.CreateDbContext();
			var q = db.Items.AsNoTracking().Where(i => i.Name != null && i.Name != "");
			if (!string.IsNullOrWhiteSpace(query))
				q = q.Where(i => EF.Functions.Like(i.Name, $"%{query.Trim()}%"));
			if (type is { } t)
				q = q.Where(i => (int)i.Type == t);

			var total = q.Count();
			var items = q.OrderBy(i => i.Name).Skip(page * pageSize).Take(pageSize).AsEnumerable().Select(i => ItemInfo.From(i)).ToList();
			return (items, total);
		}

		// The character's learned spells, resolved to readable names via the Spells catalog (RDB-imported). Ordered by
		// level then name. Range is metres, Damage is the spell's damage-rating percent (0 when not a damage spell).
		public IReadOnlyList<(string Name, byte Level, float Range, float Damage)> Spells(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			return db
				.CharacterSpells.AsNoTracking()
				.Where(x => x.CharacterId == characterId)
				.Join(db.Spells, cs => cs.SpellId, s => s.Id, (cs, s) => s)
				.OrderBy(s => s.Level)
				.ThenBy(s => s.Name)
				.AsEnumerable()
				.Select(s => (string.IsNullOrWhiteSpace(s.Name)
					? $"Spell {s.Id}"
					: s.Name, s.Level, s.Range, s.DamageRating))
				.ToList();
		}

		// Shared assembler for the stored-character path (BuildCharacterById): resolve the TexScripts (skin/hair tint, eye
		// iris, body tattoo - by mesh target, #275), the head meshes and the body RenderMesh (BodyRenderMesh, which itself
		// falls back to the same-sex Aquilonian body), then build the glb; falls back to a bare body when parts are missing.
		private byte[]? BuildFromSpec(RdbReader rdb, Sex sex, Race race, uint? hairRmId, uint? headMonster, BodyMorphDeformer.Morphs? morphs, IReadOnlyList<string>? texRefs,
			IReadOnlyList<uint>? equipMeshIndexIds, bool rig)
		{
			var bodyRm = BodyRenderMesh(rdb, race, sex);

			var (skinTint, hairTint, eyeTex, decals) = ResolveTexScripts(rdb, texRefs ?? []);

			var headMeshes = headMonster is > 0
				? RdbAssetExtractor.ResolveHeadMeshes(rdb, headMonster.Value)
				: [];

			if (bodyRm != 0 && headMeshes.Count > 0)
			{
				return RdbAssetExtractor.BuildCharacterFromParts(rdb, bodyRm, headMeshes, hairRmId, skinTint, hairTint, eyeTex, decals, morphs, equipMeshIndexIds, rig) ??
				       BuildCharacter(race, sex, hairRmId);
			}

			return BuildCharacter(race, sex, hairRmId);
		}

		// Cache a deterministic render (keyed string -> bytes). Caches the "no model" result too. Size-tracked so the
		// MemoryCache size cap can evict; linked to the reset token so the admin "clear cache" button drops them all.
		private byte[]? Cached(string key, Func<byte[]?> build)
		{
			var bytes = _cache.GetOrCreate(key, entry =>
			{
				var result = build() ?? None;
				entry.SlidingExpiration = CacheTtl;
				entry.Size = Math.Max(result.Length, 1);
				entry.AddExpirationToken(new CancellationChangeToken(_cacheReset.Token));
				return result;
			})!;
			return bytes.Length == 0
				? null
				: bytes;
		}

		// Rebuild the character's spawn-scaffold appearance entries from the just-saved resources so the edit shows
		// in-game on the next login - now possible because PlayerSpawnScaffoldRows lives in Faolan.Core.Spawn (#290),
		// which the web can reference. No-op when the character has no scaffold yet (first login seeds it from the new
		// resources via TrySeedFreshRows). The game server's login-time RefreshAppearance stays as a safety net.
		private void PropagateToScaffold(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			var character = db.Characters.AsNoTracking().Include(c => c.Attributes).Include(c => c.Body).Include(c => c.BodyResources).FirstOrDefault(c => c.Id == characterId);
			if (character?.Attributes is not { Count: > 0 })
				return;

			var refs = character.BodyResources.Select(r => r.Ref).ToList();
			var registry = db.MorphTargets.AsNoTracking().Where(m => refs.Contains(m.Name)).ToDictionary(m => m.Name);
			var weighted = PlayerSpawnScaffoldRows.BuildWeightedEntries(character.BodyResources, registry);

			if (!PlayerSpawnScaffoldRows.RefreshAppearance(character, weighted))
				return;

			// Persist only the entry table (fresh, navigation-free rows - so EF doesn't cascade-insert the parent).
			db.CharacterAttributes.RemoveRange(db.CharacterAttributes.Where(a => a.CharacterId == characterId));
			db.CharacterAttributes.AddRange(character.Attributes);
			db.SaveChanges();
		}

		private RdbReader Reader()
		{
			if (_rdb != null)
				return _rdb;

			lock (_gate)
			{
				return _rdb ??= new RdbReader(_rdbDir, _cdnUrl);
			}
		}

		// The body's skinned RenderMesh id for a race/sex, via its CHARACTERCREATOR monsterdata (BodyMonster ->
		// ResolveBodyRenderMesh). Falls back to the same-sex Aquilonian body if a race body can't be resolved (all three
		// race bodies are locally present today, so the fallback is belt-and-braces, and it keeps the correct sex). Returns
		// 0 only when even the fallback is unavailable. Lenient by design - the STRICT editor path resolves without this.
		private static uint BodyRenderMesh(RdbReader rdb, Race race, Sex sex)
		{
			if (BodyMonster.TryGetValue((race, sex), out var monster) && RdbAssetExtractor.ResolveBodyRenderMesh(rdb, monster) is { } rm)
				return rm;

			return BodyMonster.TryGetValue((Race.Aquilonian, sex), out var fallback) && RdbAssetExtractor.ResolveBodyRenderMesh(rdb, fallback) is { } fallbackRm
				? fallbackRm
				: 0;
		}

		// Decrypt an item's RDB template (type 1000020/1000620) with its stored per-template TEA key (Data1..4), or null
		// when the item has no key / no local template entry.
		private static byte[]? DecryptTemplate(RdbReader rdb, Item? item)
		{
			if (item == null || item is { Key.Key1: 0, Key.Key2: 0, Key.Key3: 0, Key.Key4: 0 })
				return null;

			var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };
			foreach (var type in new uint[] { 1000020, 1000620 })
			{
				if (rdb.TryGet(type, item.Id, out var e) && e.File != 0xff && e.Size > 0)
				{
					try
					{
						return rdb.ReadDecrypted(e, key);
					}
					catch
					{
						return null;
					}
				}
			}

			return null;
		}

		// "*hairmesh_203667" -> 203667 (the rendermesh id); null for any other/absent ref.
		private static uint? ParseResourceId(string? reference)
		{
			var us = reference?.LastIndexOf('_') ?? -1;
			return us >= 0 && uint.TryParse(reference!.AsSpan(us + 1), out var id)
				? id
				: null;
		}

		// Classify a character's *texscript_NNN resources by their mesh target: -3 skin tint, -4 hair tint, -5 eye iris
		// (a projected texture), -2 body tattoo decals (placed projecttextures composited onto the body atlas).
		// mesh="-1" is the HEAD/face mesh slot, not the body - its projecttextures are face tattoos (a separate
		// ticket) and must NOT be pasted onto the body atlas (else a face decal lands on the chest).
		private static (RdbAssetExtractor.Tint? Skin, RdbAssetExtractor.Tint? Hair, uint? EyeTexture, IReadOnlyList<RdbAssetExtractor.BodyDecal> Decals) ResolveTexScripts(RdbReader rdb,
			IEnumerable<string> resources)
		{
			RdbAssetExtractor.Tint? skin = null, hair = null;
			uint? eyeTex = null;
			var decals = new List<RdbAssetExtractor.BodyDecal>();

			foreach (var reference in resources)
			{
				if (!reference.StartsWith("*texscript_") || ParseResourceId(reference) is not { } id)
					continue;
				if (RdbAssetExtractor.LoadTexScript(rdb, id) is not { } ts)
					continue;

				switch (ts.MeshTarget)
				{
					case -3 when ts.Op.Kind == TexScriptFile.Kind.Tint:
					{
						skin = new RdbAssetExtractor.Tint(ts.Op.R, ts.Op.G, ts.Op.B);
						break;
					}

					case -4 when ts.Op.Kind == TexScriptFile.Kind.Tint:
					{
						hair = new RdbAssetExtractor.Tint(ts.Op.R, ts.Op.G, ts.Op.B);
						break;
					}

					case -5 when ts.Op.Kind == TexScriptFile.Kind.Project:
					{
						eyeTex = ts.Op.TextureId;
						break;
					}

					case -2 when ts.Op.Kind == TexScriptFile.Kind.Project:
					{
						decals.Add(new RdbAssetExtractor.BodyDecal(ts.Op.TextureId, ts.Op.U, ts.Op.V, ts.Op.Scale, ts.Op.Rotation, ts.Op.Blend));
						break;
					}
				}
			}

			return (skin, hair, eyeTex, decals);
		}

		public sealed record AppearanceCurrent(
			float Arm,
			float Chest,
			float Bosom,
			float Stomach,
			float Arse,
			float Thigh,
			float Leg,
			uint HeadMesh,
			string HairStyle,
			string HairColor,
			string Eyes,
			string Skin,
			string BodyTattoo,
			string FaceTattoo);

		// One pickable option. Value = what the editor stores/sends (a "*hairmesh_"/"*texscript_" ref, or a head
		// monsterdataid); Color = the swatch hex for colour lists (null otherwise).
		public sealed record AppearanceOption(string Value, string Label, string? Color);

		public sealed record AppearanceOptions(
			IReadOnlyList<AppearanceOption> HairStyle,
			IReadOnlyList<AppearanceOption> HairColor,
			IReadOnlyList<AppearanceOption> Eyes,
			IReadOnlyList<AppearanceOption> Skin,
			IReadOnlyList<AppearanceOption> BodyTattoo,
			IReadOnlyList<AppearanceOption> FaceTattoo,
			IReadOnlyList<AppearanceOption> Heads,
			int HeightMin,
			int HeightMax,
			IReadOnlyDictionary<string, int[]> MorphLimits);

		public sealed record AppearanceSpec(float Arm, float Chest, float Bosom, float Stomach, float Arse, float Thigh, float Leg, uint HeadMesh, IReadOnlyList<string> Refs);
	}
}
