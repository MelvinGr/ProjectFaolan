using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Services
{
	// Builds a character's 3D model (glTF/glb) on demand from the client RDB, reusing the Faolan.Core asset pipeline
	// (#275). Holds one RdbReader for the process (it memory-maps the index + keeps the data files open). Race/sex ->
	// mesh-index ids is an interim hardcoded table; the proper source is the CHARACTERCREATOR tables (1010700) +
	// per-character head/equipment selection - tracked in #275. Bodies that are CDN-only fall back to a local one so the
	// viewer always renders something.
	public sealed class CharacterModelService
	{
		private readonly string _rdbDir;
		private readonly string? _cdnUrl;
		private readonly IDbContextFactory<DatabaseContext> _dbFactory;
		private readonly Lock _gate = new();
		private RdbReader? _rdb;
		private IReadOnlyList<ArmorSetCatalog.ArmorSet>? _armorSets;

		public CharacterModelService(IConfiguration config, IDbContextFactory<DatabaseContext> dbFactory)
		{
			_dbFactory = dbFactory;
			_rdbDir = config["RdbDir"] ?? throw new InvalidOperationException("RdbDir not configured");
			// Optional ContentMirror proxy base for on-demand (CDN-only) resources, e.g. the CDN race bodies that aren't
			// in the local RDB. Store-and-forward, so requesting one archives it for the future (#275, [[aoc_content_cdn_mirror]]).
			_cdnUrl = config["CdnUrl"];
		}

		public bool RdbAvailable => Directory.Exists(_rdbDir) && File.Exists(Path.Combine(_rdbDir, "le.idx"));

		// (race, sex) -> body MESHINDEXFILE id. Cimmerian/Stygian female bodies are CDN-only in this client, so they
		// fall back to a LOCAL body of the same sex (so the sex is right even when the exact race body needs CDN fetch).
		// Proper per-character resolution (face/hair/skin) is the CHARACTERCREATOR (1010700) + appearance work (#275).
		private const uint LocalMaleBody = 1100584;   // aquilonian_male_body
		private const uint LocalFemaleBody = 1102538; // aquilonian_female_body

		// Head base mesh by sex. The 3 European starting races (Aquilonian/Cimmerian/Stygian) share one head mesh and
		// differ by morphs + face texscript, not by mesh (only Khitai has its own head). These are the shared European
		// heads (female = beautiful_famale_heads.max, male = male_heads_old.max); per-character morph/texture is #275.
		private const uint FemaleHead = 4502887;
		private const uint MaleHead = 4821637;

		private static readonly Dictionary<(Race, Sex), uint> BodyMesh = new()
		{
			[(Race.Aquilonian, Sex.Male)] = 1100584,
			[(Race.Cimmerian, Sex.Male)] = 1102516,
			[(Race.Stygian, Sex.Male)] = 1102518,
			[(Race.Aquilonian, Sex.Female)] = 1102538,
			[(Race.Cimmerian, Sex.Female)] = 1102536, // CDN -> female fallback
			[(Race.Stygian, Sex.Female)] = 1102540    // CDN -> female fallback
		};

		public byte[]? BuildCharacter(Race race, Sex sex, uint? hairRmId = null)
		{
			var rdb = Reader();
			var fallback = sex == Sex.Female ? LocalFemaleBody : LocalMaleBody;
			var bodyId = BodyMesh.GetValueOrDefault((race, sex), fallback);
			var headId = sex == Sex.Female ? FemaleHead : MaleHead;

			return RdbAssetExtractor.BuildCharacterGlb(rdb, bodyId, headId, hairRmId)
			       ?? RdbAssetExtractor.BuildCharacterGlb(rdb, fallback, headId, hairRmId);
		}

		// Build a specific character by id, using their stored appearance (CharacterBodyResources) - currently the
		// chosen hairstyle (*hairmesh_NNN, where NNN is the hair rendermesh id). Head morph/face texscript still pending
		// the CHARACTERCREATOR (1010700) decode (#275); HeadMesh is a creator index, not a resource id.
		public byte[]? BuildCharacterById(uint characterId, IReadOnlyList<uint>? equipMeshIndexIds = null, bool rig = false)
		{
			using var db = _dbFactory.CreateDbContext();
			var character = db.Characters.AsNoTracking().FirstOrDefault(c => c.Id == characterId);
			if (character is null)
				return null;

			var body = db.CharacterBodies.AsNoTracking().FirstOrDefault(b => b.CharacterId == characterId);
			var resources = db.CharacterBodyResources.AsNoTracking()
				.Where(r => r.CharacterId == characterId)
				.Select(r => r.Ref)
				.ToList();
			var hairRmId = ParseResourceId(resources.FirstOrDefault(r => r.StartsWith("*hairmesh_")));

			var rdb = Reader();
			var bodyFallback = character.Sex == Sex.Female ? LocalFemaleBody : LocalMaleBody;
			var bodyId = BodyMesh.GetValueOrDefault((character.Race, character.Sex), bodyFallback);

			// Classify the character's TexScripts (skin/hair tint, eye iris, body tattoos) by their mesh target (#275).
			var (skinTint, hairTint, eyeTex, decals) = ResolveTexScripts(rdb, resources);

			// Body-proportion morphs (cluster-scale deform of the body mesh).
			var morphs = body is null
				? (BodyMorphDeformer.Morphs?)null
				: new BodyMorphDeformer.Morphs(body.Arm, body.Chest, body.Bosom, body.Stomach, body.Arse, body.Thigh, body.Leg);

			// CharacterBody.HeadMesh is stored in editor 2x units; the real CHARACTERCREATOR head monsterdataid is
			// HeadMesh/2 ([[CaptureCharacterImporter]]). Resolve it to head+eyes+lashes meshes via MonsterData.
			var headMeshes = body is { HeadMesh: > 0 }
				? RdbAssetExtractor.ResolveHeadMeshes(rdb, body.HeadMesh / 2)
				: [];

			if (headMeshes.Count > 0)
				// Try the exact race/sex body, then the local same-sex fallback (the exact body is often CDN-only).
				return RdbAssetExtractor.BuildCharacterFromParts(rdb, bodyId, headMeshes, hairRmId, skinTint, hairTint, eyeTex, decals, morphs, equipMeshIndexIds, rig)
				       ?? RdbAssetExtractor.BuildCharacterFromParts(rdb, bodyFallback, headMeshes, hairRmId, skinTint, hairTint, eyeTex, decals, morphs, equipMeshIndexIds, rig)
				       ?? BuildCharacter(character.Race, character.Sex, hairRmId);

			return BuildCharacter(character.Race, character.Sex, hairRmId);
		}

		// The wearable armour-set catalogue (built once from the RDB, then cached). Optionally filtered to a sex.
		public IReadOnlyList<ArmorSetCatalog.ArmorSet> ArmorSets(Sex? sex = null)
		{
			_armorSets ??= ArmorSetCatalog.Build(Reader());
			var want = sex == Sex.Female ? "Female" : sex == Sex.Male ? "Male" : null;
			return want is null
				? _armorSets
				: _armorSets.Where(s => s.Gender == want || s.Gender == "Unisex").ToList();
		}

		// Diagnostic: the decompressed text of an RDB resource (XML resources like TexScript 1010035), or null if absent.
		public string? RawResourceText(uint type, uint id) => RdbAssetExtractor.RawText(Reader(), type, id);

		// Diagnostic: decode an FCTX texture to PNG (for inspecting tattoo decal textures), or null if absent.
		public byte[]? DecodeTexture(uint type, uint id) => RdbAssetExtractor.DecodeTexturePng(Reader(), type, id);

		// Diagnostic: raw decompressed bytes of an RDB resource (binary blob analysis, e.g. rendermesh DPPM morph chunk).
		public byte[]? RawResourceBytes(uint type, uint id) => RdbAssetExtractor.RawBytes(Reader(), type, id);

		// Diagnostic: report whether the body's left/right UVs are mirrored.
		public object DescribeBodyUv(uint bodyMeshIndexId) => RdbAssetExtractor.DescribeBodyUv(Reader(), bodyMeshIndexId);

		// Diagnostic: surface the appearance-resolution chain for a character.
		public object DebugCharacter(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			var character = db.Characters.AsNoTracking().FirstOrDefault(c => c.Id == characterId);
			var body = db.CharacterBodies.AsNoTracking().FirstOrDefault(b => b.CharacterId == characterId);
			var resources = db.CharacterBodyResources.AsNoTracking().Where(r => r.CharacterId == characterId).Select(r => r.Ref).ToList();
			var rdb = Reader();
			var monsterId = body is { HeadMesh: > 0 } ? body.HeadMesh / 2 : 0;
			var headMeshes = monsterId > 0 ? RdbAssetExtractor.ResolveHeadMeshes(rdb, monsterId) : [];

			// Body mesh resolution + whether it's local or a CDN pull (the race body is often CDN-only).
			var bodyFallback = character?.Sex == Sex.Female ? LocalFemaleBody : LocalMaleBody;
			var bodyId = character != null ? BodyMesh.GetValueOrDefault((character.Race, character.Sex), bodyFallback) : 0;
			var bodyStatus = RdbAssetExtractor.DescribeMesh(rdb, bodyId);

			// Resolve each *texscript_NNN to its mesh-target + op so the skin/hair/eye classification is visible.
			var texscripts = resources
				.Where(r => r.StartsWith("*texscript_") && ParseResourceId(r) is not null)
				.Select(r =>
				{
					var id = ParseResourceId(r)!.Value;
					var ts = RdbAssetExtractor.LoadTexScript(rdb, id);
					return ts is null
						? new { id, found = false, meshTarget = 0, kind = "?", r = (int)0, g = (int)0, b = (int)0, textureId = 0u }
						: new { id, found = true, meshTarget = ts.MeshTarget, kind = ts.Op.Kind.ToString(), r = (int)ts.Op.R, g = (int)ts.Op.G, b = (int)ts.Op.B, textureId = ts.Op.TextureId };
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
				bodyId,
				bodyStatus,
				resources,
				texscripts,
				resolved = new
				{
					skin = skinTint is { } s ? $"#{s.R:X2}{s.G:X2}{s.B:X2}" : null,
					hair = hairTint is { } h ? $"#{h.R:X2}{h.G:X2}{h.B:X2}" : null,
					eyeTexture = eyeTex,
					bodyDecals = decals
				}
			};
		}

		// Classify a character's *texscript_NNN resources by their mesh target: -3 skin tint, -4 hair tint, -5 eye iris
		// (a projected texture), -2 body tattoo decals (placed projecttextures composited onto the body atlas).
		// mesh="-1" is the HEAD/face mesh slot, not the body - its projecttextures are face tattoos (a separate
		// ticket) and must NOT be pasted onto the body atlas (else a face decal lands on the chest).
		private static (RdbAssetExtractor.Tint? Skin, RdbAssetExtractor.Tint? Hair, uint? EyeTexture,
			IReadOnlyList<RdbAssetExtractor.BodyDecal> Decals) ResolveTexScripts(RdbReader rdb, IEnumerable<string> resources)
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
						skin = new RdbAssetExtractor.Tint(ts.Op.R, ts.Op.G, ts.Op.B);
						break;
					case -4 when ts.Op.Kind == TexScriptFile.Kind.Tint:
						hair = new RdbAssetExtractor.Tint(ts.Op.R, ts.Op.G, ts.Op.B);
						break;
					case -5 when ts.Op.Kind == TexScriptFile.Kind.Project:
						eyeTex = ts.Op.TextureId;
						break;
					case -2 when ts.Op.Kind == TexScriptFile.Kind.Project:
						decals.Add(new RdbAssetExtractor.BodyDecal(ts.Op.TextureId, ts.Op.U, ts.Op.V, ts.Op.Scale, ts.Op.Rotation, ts.Op.Blend));
						break;
				}
			}

			return (skin, hair, eyeTex, decals);
		}

		// "*hairmesh_203667" -> 203667 (the rendermesh id); null for any other/absent ref.
		private static uint? ParseResourceId(string? reference)
		{
			var us = reference?.LastIndexOf('_') ?? -1;
			return us >= 0 && uint.TryParse(reference!.AsSpan(us + 1), out var id) ? id : null;
		}

		// Build a single named model (any MESHINDEXFILE id) - lets the viewer show equipment/props too.
		public byte[]? BuildModel(uint meshIndexId)
		{
			var rdb = Reader();
			var subs = RdbAssetExtractor.ReadModel(rdb, meshIndexId);
			return subs is { Count: > 0 } ? GlbWriter.Build(subs) : null;
		}

		// Build a SKINNED glb for a skinned model (body / equipment piece): geometry + the character skeleton as a glTF
		// skin, so the result is posable (#275). skeletonId null => auto-select the rig matching the mesh's bone palette.
		public byte[]? BuildRig(uint meshIndexId, uint? skeletonId = null) =>
			RdbAssetExtractor.BuildSkinnedModelGlb(Reader(), meshIndexId, skeletonId);

		// Build a character as a posable rig (skinned glb): the full assembled character - body (morphs + skin + decals) +
		// head + hair - skinned to the auto-selected skeleton, so the viewer's pose slider can articulate it. The body
		// skins to its own bones; the rigid head/hair are bound to the Head bone. (Gear skinning is the separate #275 work.)
		public byte[]? BuildCharacterRig(uint characterId) => BuildCharacterById(characterId, equipMeshIndexIds: null, rig: true);

		// A character's equipped items keyed by the viewer's slot labels (only non-empty slots), to populate the
		// equipment paper-doll. Item id + display name; the icon is resolved separately from the RDB item template.
		public IReadOnlyDictionary<string, (uint Id, string Name)> Equipment(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			var e = db.CharacterEquipment.AsNoTracking()
				.Include(x => x.Head).Include(x => x.Neck).Include(x => x.Shoulder).Include(x => x.Cloak)
				.Include(x => x.Chest).Include(x => x.Wrist).Include(x => x.Hands).Include(x => x.Belt)
				.Include(x => x.Boots).Include(x => x.RingLeft).Include(x => x.RingRight).Include(x => x.Legs)
				.Include(x => x.MainHandLeft).Include(x => x.OffHandLeft).Include(x => x.Tokens).Include(x => x.FirstPosBag)
				.FirstOrDefault(x => x.CharacterId == characterId);
			if (e is null)
				return new Dictionary<string, (uint, string)>();

			var map = new (string Slot, Item? Item)[]
			{
				("Head", e.Head), ("Neck", e.Neck), ("Shoulder", e.Shoulder), ("Cloak", e.Cloak),
				("Chest", e.Chest), ("Wrist", e.Wrist), ("Hands", e.Hands), ("Belt", e.Belt),
				("Boots", e.Boots), ("Ring L", e.RingLeft), ("Ring R", e.RingRight), ("Legs", e.Legs),
				("Main Hand", e.MainHandLeft), ("Off Hand", e.OffHandLeft), ("Tokens", e.Tokens), ("Bag", e.FirstPosBag)
			};

			var result = new Dictionary<string, (uint, string)>();
			foreach (var (slot, item) in map)
				if (item is not null)
					result[slot] = (item.Id, string.IsNullOrWhiteSpace(item.Name) ? $"Item {item.Id}" : item.Name);
			return result;
		}

		// The character's carried (non-worn) items — the backpack/quest/resource contents — as a flat, display-ordered
		// list (container, then slot), each tagged with its tab. Empty slots are dropped; the viewer pages what remains.
		// Containers: 104 = normal backpack, 112 = resources, 113 = quest (Faolan.Core/Enums/ItemType.cs).
		public IReadOnlyList<(uint Id, string Name, uint Count, string Tab)> Inventory(uint characterId)
		{
			using var db = _dbFactory.CreateDbContext();
			return db.CharacterInventory.AsNoTracking()
				.Where(x => x.CharacterId == characterId && x.ItemId != null)
				.Include(x => x.Item)
				.OrderBy(x => x.Container).ThenBy(x => x.Slot)
				.AsEnumerable()
				.Select(x => (
					x.Item!.Id,
					string.IsNullOrWhiteSpace(x.Item.Name) ? $"Item {x.Item.Id}" : x.Item.Name,
					x.Count,
					x.Container switch { 113 => "Quest", 112 => "Resources", _ => "Normal" }))
				.ToList();
		}

		// Build an item's 3D display model (the inventory mesh shown in equipment slots), or null if it has none
		// (armour pieces carry only a worn-look id, not a display mesh - the open gear RE).
		public byte[]? ItemModel(uint itemId)
		{
			using var db = _dbFactory.CreateDbContext();
			var item = db.Items.AsNoTracking().FirstOrDefault(i => i.Id == itemId);
			return item is null
				? null
				: RdbAssetExtractor.BuildItemModelGlb(Reader(), itemId, item.Data1, item.Data2, item.Data3, item.Data4);
		}

		// Build a single RenderMesh (type 1010001) directly by id - for inspecting head/eye/hair meshes (#275).
		public byte[]? BuildRenderMesh(uint renderMeshId)
		{
			var rdb = Reader();
			var subs = RdbAssetExtractor.ReadRenderMeshDirect(rdb, renderMeshId);
			return subs is { Count: > 0 } ? GlbWriter.Build(subs) : null;
		}

		private RdbReader Reader()
		{
			if (_rdb != null)
				return _rdb;

			lock (_gate)
				return _rdb ??= new RdbReader(_rdbDir, _cdnUrl);
		}
	}
}
