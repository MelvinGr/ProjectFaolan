using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Faolan.Importer.Rdb
{
	// Store-and-forward fetch for on-demand (file==0xFF) RDB resources: the le.idx 16-byte content hash is the CDN
	// path md5 (<host>/upm/rdb/res/<h0:2>/<h2:1>/<h3:>). If a store dir is given, fetched blobs are ALSO written to it
	// in the Faolan.ContentMirror layout (<store>/gameupdate/upm/rdb/res/...) and read-from-store-first - so every
	// resource we pull is archived locally for preservation and the mirror can later serve it offline. See
	// [[aoc_content_cdn_mirror]].
	public sealed class RdbCdnCache : IDisposable
	{
		private const string Tag = "gameupdate"; // the on-demand RDB host's mirror tag
		private readonly HttpClient _http;
		private readonly string _cdnBase;
		private readonly string _storeDir;
		public int Fetched { get; private set; }

		public int Hits { get; private set; }

		public RdbCdnCache(string cdnBase, string storeDir)
		{
			_cdnBase = cdnBase.TrimEnd('/');
			_storeDir = storeDir;
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
			_http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
		}

		public void Dispose()
		{
			_http.Dispose();
		}

		// The raw (IOz1+zlib) blob for a content hash. Served from the store if archived, else fetched + archived.
		public async Task<byte[]> FetchRawAsync(byte[] hash)
		{
			var h = Convert
				.ToHexString(hash)
				.ToLowerInvariant();
			var rest = $"rdb/res/{h[..2]}/{h.Substring(2, 1)}/{h[3..]}";

			var stored = _storeDir == null
				? null
				: Path.Combine(_storeDir, Tag, "upm", rest.Replace('/', Path.DirectorySeparatorChar));

			if (stored != null && File.Exists(stored))
			{
				Hits++;
				return await File.ReadAllBytesAsync(stored);
			}

			var raw = await _http.GetByteArrayAsync($"{_cdnBase}/upm/{rest}");
			Fetched++;

			if (stored != null)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
				await File.WriteAllBytesAsync(stored, raw);
			}

			return raw;
		}
	}
}
