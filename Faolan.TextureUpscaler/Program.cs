using System.Text;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.TextureUpscaler;

// Faolan.TextureUpscaler (#295 Phase 2): batch-upscales AoC textures and INLINES the results into the RDB, so a
// modded install renders higher-res art with no CDN dependency. Colour textures (DXT1/DXT5) go through
// realesrgan-ncnn-vulkan on the GPU; MIXD normal/detail maps are bilinear-upscaled (never ML - it distorts normals).
//
// Usage:
//   dotnet run --project Faolan.TextureUpscaler -- --rdb <RDB dir> --bin /usr/bin/realesrgan-ncnn-vulkan \
//       --model realesrgan-x4plus --model-dir /usr/share/realesrgan-ncnn-vulkan/models \
//       [--scale 4] [--datafile 210] [--type 1010004] [--id 21,66,86] [--limit N] [--dry-run]
// --id  = only these specific resource ids (comma-separated); --type = which RDB type(s); --limit = first N found.
//
// SAFETY: writes le.idx + a new NN.rdbdata IN PLACE. It backs le.idx up to le.idx.faolan-bak first. Stop the game
// before running. --dry-run does everything except the write.

internal class Program
{
	public static int Main(string[] args)
	{
		var opt = ParseArgs(args);
		if (opt.Rdb == null)
		{
			Console.Error.WriteLine("--rdb <dir> is required. See the header of Program.cs for usage.");
			return 2;
		}

		var binOk = EsrganUpscaler.BinaryAvailable(opt.Bin);
		if (!binOk && !opt.DryRun)
			Console.WriteLine($"WARNING: realesrgan binary '{opt.Bin}' not found - colour textures will be skipped (install it, or --dry-run).");

		var rdb = new RdbReader(opt.Rdb, opt.Cdn);
		var esrgan = new EsrganUpscaler(opt.Bin, opt.Model, opt.Scale, opt.ModelDir);

		// Collect the target texture entries (local, decodable FCTX of the requested types).
		var targets = new List<(uint Type, uint Id, string FourCc)>();
		foreach (var e in rdb.Entries)
		{
			if (!opt.Types.Contains(e.Type) || e.Size == 0 || !rdb.CanRead(e))
				continue;

			if (opt.Ids.Count > 0 && !opt.Ids.Contains(e.Id))
				continue; // --id filter: only these specific resource ids

			byte[] head;
			try
			{
				head = rdb.ReadDecompressed(e);
			}
			catch
			{
				continue;
			}

			if (!FctxConverter.IsFctx(head) || head.Length < 24)
				continue;

			var fourCc = Encoding.ASCII.GetString(head, 20, 4);
			if (fourCc is "DXT1" or "DXT5" or "MIXD")
				targets.Add((e.Type, e.Id, fourCc));

			if (opt.Limit > 0 && targets.Count >= opt.Limit)
				break;
		}

		Console.WriteLine($"targets: {targets.Count} textures (types {string.Join(",", opt.Types)}, scale x{opt.Scale})");

		if (!opt.DryRun)
			File.Copy(Path.Combine(opt.Rdb, "le.idx"), Path.Combine(opt.Rdb, "le.idx.faolan-bak"), true);

		var blobs = new List<(uint, uint, byte[])>();
		int color = 0, mixd = 0, skipped = 0, failed = 0;
		foreach (var (type, id, fourCc) in targets)
		{
			try
			{
				byte[]? blob;
				if (fourCc == "MIXD")
				{
					blob = RdbTextureReplacer.BuildUpscaled(rdb, type, id, opt.Scale, NormalUpscaler.Renormalizing);
					if (blob != null)
						mixd++;
				}
				else if (binOk)
				{
					blob = RdbTextureReplacer.BuildUpscaled(rdb, type, id, opt.Scale, esrgan.Upscale);
					if (blob != null)
						color++;
				}
				else
				{
					skipped++;
					continue;
				}

				if (blob == null)
				{
					skipped++;
					continue;
				}

				blobs.Add((type, id, blob));
			}
			catch (Exception ex)
			{
				failed++;
				Console.Error.WriteLine($"  {type}:{id} ({fourCc}) failed: {ex.Message}");
			}
		}

		if (opt.DryRun)
			Console.WriteLine($"DRY-RUN: would inline {blobs.Count} textures ({color} colour, {mixd} MIXD), {skipped} skipped, {failed} failed.");
		else
		{
			var n = RdbWriter.InlineEntries(opt.Rdb, opt.DataFile, blobs);
			Console.WriteLine($"inlined {n} textures ({color} colour, {mixd} MIXD) into {opt.DataFile:D2}.rdbdata; {skipped} skipped, {failed} failed. le.idx backup: le.idx.faolan-bak");
		}

		return 0;

		static Options ParseArgs(string[] a)
		{
			var o = new Options();
			for (var i = 0; i < a.Length; i++)
			{
				switch (a[i])
				{
					case "--rdb":
						o.Rdb = a[++i];
						break;
					case "--bin":
						o.Bin = a[++i];
						break;
					case "--model":
						o.Model = a[++i];
						break;
					case "--model-dir":
						o.ModelDir = a[++i];
						break;
					case "--cdn":
						o.Cdn = a[++i];
						break;
					case "--scale":
						o.Scale = int.Parse(a[++i]);
						break;
					case "--datafile":
						o.DataFile = byte.Parse(a[++i]);
						break;
					case "--type":
						o.Types.Add(uint.Parse(a[++i]));
						break;
					case "--id":
						foreach (var s in a[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
							o.Ids.Add(uint.Parse(s));
						break;
					case "--limit":
						o.Limit = int.Parse(a[++i]);
						break;
					case "--dry-run":
						o.DryRun = true;
						break;
				}
			}

			if (o.Types.Count == 0)
				o.Types.Add(1010004); // FCTX1 textures by default
			return o;
		}
	}

	private sealed class Options
	{
		public readonly HashSet<uint> Ids = [];
		public readonly HashSet<uint> Types = [];
		public bool DryRun;
		public byte DataFile = 210;
		public int Limit;
		public int Scale = 4;
		public string Bin = "realesrgan-ncnn-vulkan";
		public string Model = "realesrgan-x4plus";
		public string? Cdn;
		public string? ModelDir;
		public string? Rdb;
	}
}
