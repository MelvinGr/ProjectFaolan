using System.Diagnostics;
using Faolan.Core.Rdb.Assets;

namespace Faolan.TextureUpscaler
{
	// Shells out to realesrgan-ncnn-vulkan (#295 Phase 2, Option A) to super-resolve a colour texture on the GPU
	// (Vulkan). The binary handles alpha / gray / 16-bit / tiling itself, so this is just PNG in → PNG out glue.
	// Fixed integer scale (the model's, e.g. 4 for realesr-general-x4v3); pass the matching `scale` to the replacer.
	//
	// NB: do NOT use this for NORMAL maps (MIXD BC5) - a photo-SR model distorts the vectors. Route those to
	// NormalUpscaler instead. Diffuse/colour + detail channels are fine here.
	public sealed class EsrganUpscaler
	{
		private readonly string _bin;
		private readonly string _model;
		private readonly string? _modelDir;

		public int Scale { get; }

		public EsrganUpscaler(string bin, string model, int scale, string? modelDir = null)
		{
			_bin = bin;
			_model = model;
			Scale = scale;
			_modelDir = modelDir;
		}

		// The Func<rgba, srcW, srcH, dstW, dstH, rgba> the replacer expects. dstW/dstH must equal src*scale (the
		// model's fixed factor); an off-scale request throws.
		public byte[] Upscale(byte[] rgba, int sw, int sh, int dw, int dh)
		{
			if (dw != sw * Scale || dh != sh * Scale)
				throw new ArgumentException($"realesrgan model is x{Scale}; requested {sw}x{sh} -> {dw}x{dh}");

			var tmp = Path.Combine(Path.GetTempPath(), "faolan-esrgan-" + Guid.NewGuid().ToString("N"));
			var inPng = tmp + "-in.png";
			var outPng = tmp + "-out.png";
			try
			{
				File.WriteAllBytes(inPng, SkiaImage.EncodePng(rgba, sw, sh));

				var psi = new ProcessStartInfo(_bin)
				{
					RedirectStandardError = true,
					RedirectStandardOutput = true,
					UseShellExecute = false
				};
				psi.ArgumentList.Add("-i");
				psi.ArgumentList.Add(inPng);
				psi.ArgumentList.Add("-o");
				psi.ArgumentList.Add(outPng);
				psi.ArgumentList.Add("-s");
				psi.ArgumentList.Add(Scale.ToString());
				psi.ArgumentList.Add("-n");
				psi.ArgumentList.Add(_model);
				if (_modelDir != null)
				{
					psi.ArgumentList.Add("-m");
					psi.ArgumentList.Add(_modelDir);
				}

				using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start realesrgan");
				var stderr = p.StandardError.ReadToEnd();
				p.WaitForExit();
				if (p.ExitCode != 0 || !File.Exists(outPng))
					throw new InvalidOperationException($"realesrgan failed (exit {p.ExitCode}): {stderr}");

				var (outRgba, ow, oh) = SkiaImage.DecodePng(File.ReadAllBytes(outPng));
				return (ow, oh) == (dw, dh)
					? outRgba
					: NormalUpscaler.BilinearResample(outRgba, ow, oh, dw, dh); // safety net if the model rounds dims
			}
			finally
			{
				TryDelete(inPng);
				TryDelete(outPng);
			}
		}

		// True if the binary looks runnable (exists on PATH or as a file). Cheap pre-flight so the driver can warn.
		public static bool BinaryAvailable(string bin)
		{
			if (File.Exists(bin))
				return true;

			var path = Environment.GetEnvironmentVariable("PATH") ?? "";
			return path.Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, bin)));
		}

		private static void TryDelete(string f)
		{
			try
			{
				File.Delete(f);
			}
			catch
			{
				// best effort
			}
		}
	}
}
