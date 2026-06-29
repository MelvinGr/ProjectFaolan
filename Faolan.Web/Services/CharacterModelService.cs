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

		// (race, sex) -> body mesh-index id. Only a subset of bodies are local in this client; the rest resolve to the
		// Aquilonian male body so the page still shows a model until CDN fetch + the CHARACTERCREATOR mapping land.
		private const uint AquilonianMaleBody = 1100584;
		private const uint DefaultHead = 3226952; // a local full head (unique_head_saddur) MESHINDEXFILE id - interim default

		private static readonly Dictionary<(Race, Sex), uint> BodyMesh = new()
		{
			[(Race.Aquilonian, Sex.Male)] = 1100584,
			[(Race.Stygian, Sex.Male)] = 1102518,
			[(Race.Cimmerian, Sex.Female)] = 1102536,
			[(Race.Stygian, Sex.Female)] = 1102540
		};

		public byte[]? BuildCharacter(Race race, Sex sex)
		{
			var rdb = Reader();
			var bodyId = BodyMesh.GetValueOrDefault((race, sex), AquilonianMaleBody);
			return RdbAssetExtractor.BuildCharacterGlb(rdb, bodyId, DefaultHead)
			       ?? RdbAssetExtractor.BuildCharacterGlb(rdb, AquilonianMaleBody, DefaultHead);
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
