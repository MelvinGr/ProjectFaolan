using Faolan.Core.Database;
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
		private readonly IDbContextFactory<DatabaseContext> _dbFactory;
		private readonly Lock _gate = new();
		private RdbReader? _rdb;

		public CharacterModelService(IConfiguration config, IDbContextFactory<DatabaseContext> dbFactory)
		{
			_dbFactory = dbFactory;
			_rdbDir = config["RdbDir"] ?? throw new InvalidOperationException("RdbDir not configured");
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
		public byte[]? BuildCharacterById(uint characterId)
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

			// Classify the character's TexScripts (skin/hair tint, eye iris) by their mesh target (#275).
			var (skinTint, hairTint, eyeTex) = ResolveTexScripts(rdb, resources);

			// CharacterBody.HeadMesh is stored in editor 2x units; the real CHARACTERCREATOR head monsterdataid is
			// HeadMesh/2 ([[CaptureCharacterImporter]]). Resolve it to head+eyes+lashes meshes via MonsterData.
			var headMeshes = body is { HeadMesh: > 0 }
				? RdbAssetExtractor.ResolveHeadMeshes(rdb, body.HeadMesh / 2)
				: [];

			if (headMeshes.Count > 0)
				// Try the exact race/sex body, then the local same-sex fallback (the exact body is often CDN-only).
				return RdbAssetExtractor.BuildCharacterFromParts(rdb, bodyId, headMeshes, hairRmId, skinTint, hairTint, eyeTex)
				       ?? RdbAssetExtractor.BuildCharacterFromParts(rdb, bodyFallback, headMeshes, hairRmId, skinTint, hairTint, eyeTex)
				       ?? BuildCharacter(character.Race, character.Sex, hairRmId);

			return BuildCharacter(character.Race, character.Sex, hairRmId);
		}

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
			return new
			{
				found = character != null,
				race = character?.Race.ToString(),
				sex = character?.Sex.ToString(),
				hasBody = body != null,
				headMeshRaw = body?.HeadMesh,
				monsterId,
				headMeshes,
				resources
			};
		}

		// Classify a character's *texscript_NNN resources by their mesh target: -3 skin tint, -4 hair tint, -5 eye iris
		// (a projected texture). Body markings (-1/-2 tattoo decals) are not composited yet (#275).
		private static (RdbAssetExtractor.Tint? Skin, RdbAssetExtractor.Tint? Hair, uint? EyeTexture) ResolveTexScripts(
			RdbReader rdb, IEnumerable<string> resources)
		{
			RdbAssetExtractor.Tint? skin = null, hair = null;
			uint? eyeTex = null;

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
				}
			}

			return (skin, hair, eyeTex);
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
				return _rdb ??= new RdbReader(_rdbDir);
		}
	}
}
