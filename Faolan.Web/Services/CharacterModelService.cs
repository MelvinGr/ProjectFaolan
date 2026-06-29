using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;

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
		private readonly object _gate = new();
		private RdbReader? _rdb;

		public CharacterModelService(IConfiguration config)
		{
			_rdbDir = config["Rdb:Path"]
			          ?? "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB";
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

		public byte[]? BuildCharacter(Race race, Sex sex)
		{
			var rdb = Reader();
			var fallback = sex == Sex.Female ? LocalFemaleBody : LocalMaleBody;
			var bodyId = BodyMesh.GetValueOrDefault((race, sex), fallback);
			var headId = sex == Sex.Female ? FemaleHead : MaleHead;

			return RdbAssetExtractor.BuildCharacterGlb(rdb, bodyId, headId)
			       ?? RdbAssetExtractor.BuildCharacterGlb(rdb, fallback, headId);
		}

		// Build a single named model (any MESHINDEXFILE id) - lets the viewer show equipment/props too.
		public byte[]? BuildModel(uint meshIndexId)
		{
			var rdb = Reader();
			var subs = RdbAssetExtractor.ReadModel(rdb, meshIndexId);
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
