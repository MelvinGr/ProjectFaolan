using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     SkiaSharp-backed 2D image operations for the texture pipeline: PNG encoding, colour tinting (an RGB
	///     multiply), alpha-over compositing such as an eye iris over its sclera, and UV-placed decals such as body
	///     tattoos and markings.
	/// </summary>
	/// <remarks>
	///     Raw BCn block decoding belongs to <see cref="BcDecoder" /> and <see cref="FctxConverter" />: Skia decodes
	///     standard image formats, not GPU-compressed blocks.
	/// </remarks>
	public static class SkiaImage
	{
		/// <summary>
		///     Composites a set of decals, such as body tattoos, onto a base atlas in one pass, then encodes the result
		///     opaque and optionally tinted.
		/// </summary>
		public static byte[] CompositeDecals(byte[] baseRgba, int bw, int bh, IReadOnlyList<Decal> decals, RdbAssetExtractor.Tint? tint = null)
		{
			using var baseBmp = FromRgba(baseRgba, bw, bh);
			using var result = new SKBitmap(Unpremul(bw, bh));
			using (var canvas = new SKCanvas(result))
			{
				Blit(canvas, baseBmp, 0, 0);
				foreach (var d in decals)
				{
					using var decalBmp = FromRgba(d.Rgba, d.W, d.H);
					using var paint = new SKPaint
					{
						Color = SKColors.White.WithAlpha((byte)Math.Clamp(d.Blend * 255f, 0, 255)),
						IsAntialias = true
					};
					var drawW = d.ScaleFraction * bw;
					var drawH = drawW * d.H / d.W;
					canvas.Save();
					canvas.Translate(d.U * bw, d.V * bh);
					canvas.RotateDegrees(d.RotationDeg);
					Blit(canvas, decalBmp, new SKRect(0, 0, d.W, d.H), new SKRect(-drawW / 2, -drawH / 2, drawW / 2, drawH / 2), paint);
					canvas.Restore();
				}
			}

			ForceOpaque(result);
			return Encode(result, tint);
		}

		/// <summary>
		///     An alpha-over composite: draws the overlay, scaled to the base size, on top of the base, then encodes
		///     the result opaque and optionally tinted. This is how an eye iris is laid onto the eyeball's sclera
		///     while the white of the eye is kept.
		/// </summary>
		public static byte[] CompositeOver(byte[] baseRgba, int bw, int bh, byte[] overRgba, int ow, int oh, RdbAssetExtractor.Tint? tint = null)
		{
			using var baseBmp = FromRgba(baseRgba, bw, bh);
			using var overBmp = FromRgba(overRgba, ow, oh);
			using var result = new SKBitmap(Unpremul(bw, bh));
			using (var canvas = new SKCanvas(result))
			{
				Blit(canvas, baseBmp, 0, 0);
				Blit(canvas, overBmp, new SKRect(0, 0, ow, oh), new SKRect(0, 0, bw, bh));
			}

			ForceOpaque(result);
			return Encode(result, tint);
		}

		/// <summary>
		///     Decodes a PNG, or any Skia-supported image, to an RGBA8888 buffer and its dimensions, which is how an
		///     external upscaler's output is read back into the texture pipeline.
		/// </summary>
		public static (byte[] Rgba, int W, int H) DecodePng(byte[] png)
		{
			// Decode through the codec straight to RGBA8888 unpremultiplied. SKBitmap.Decode defaults to
			// premultiplied alpha, which folds alpha into RGB and darkens semi-transparent textures; DXT encoding
			// needs the straight, non-premultiplied colour.
			using var data = SKData.CreateCopy(png);
			using var codec = SKCodec.Create(data) ?? throw new ArgumentException("not a decodable image");
			var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
			var rgba = new byte[info.Width * info.Height * 4];
			var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
			try
			{
				var res = codec.GetPixels(info, handle.AddrOfPinnedObject());
				if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput)
					throw new ArgumentException($"image decode failed: {res}");
			}
			finally
			{
				handle.Free();
			}

			return (rgba, info.Width, info.Height);
		}

		/// <summary>
		///     Draws a decal, such as a tattoo, onto a copy of the base atlas at UV (u, v in 0..1), scaled, rotated and
		///     alpha-blended.
		/// </summary>
		/// <param name="scaleFraction">
		///     The decal's drawn size as a fraction of the atlas width, which is the TexScript "scale" over 255.
		/// </param>
		public static byte[] DrawDecal(byte[] baseRgba, int bw, int bh, byte[] decalRgba, int dw, int dh, float u, float v, float scaleFraction, float rotationDeg, float blend,
			RdbAssetExtractor.Tint? tint = null)
		{
			using var baseBmp = FromRgba(baseRgba, bw, bh);
			using var decalBmp = FromRgba(decalRgba, dw, dh);
			using var result = new SKBitmap(Unpremul(bw, bh));
			using (var canvas = new SKCanvas(result))
			using (var paint = new SKPaint
			{
				Color = SKColors.White.WithAlpha((byte)Math.Clamp(blend * 255f, 0, 255)),
				IsAntialias = true
			})
			{
				Blit(canvas, baseBmp, 0, 0);

				var drawW = scaleFraction * bw;
				var drawH = drawW * dh / dw;
				canvas.Save();
				canvas.Translate(u * bw, v * bh);
				canvas.RotateDegrees(rotationDeg);
				Blit(canvas, decalBmp, new SKRect(0, 0, dw, dh), new SKRect(-drawW / 2, -drawH / 2, drawW / 2, drawH / 2), paint);
				canvas.Restore();
			}

			ForceOpaque(result);
			return Encode(result, tint);
		}

		/// <summary>
		///     Encodes an RGBA buffer to PNG, optionally multiplied by a colour tint.
		/// </summary>
		public static byte[] EncodePng(byte[] rgba, int w, int h, RdbAssetExtractor.Tint? tint = null)
		{
			using var bmp = FromRgba(rgba, w, h);
			return Encode(bmp, tint);
		}

		/// <summary>
		///     Wraps an RGBA8888 byte buffer in an SKBitmap, copying it into Skia-owned pixels.
		/// </summary>
		public static SKBitmap FromRgba(byte[] rgba, int w, int h)
		{
			var bmp = new SKBitmap(Unpremul(w, h));
			Marshal.Copy(rgba, 0, bmp.GetPixels(), Math.Min(rgba.Length, w * h * 4));
			return bmp;
		}

		/// <summary>
		///     Draws a bitmap through <see cref="SKImage.FromBitmap" /> and DrawImage, which is the supported path
		///     since SKCanvas.DrawBitmap became obsolete in SkiaSharp 4.x. The default sampling is nearest.
		/// </summary>
		private static void Blit(SKCanvas canvas, SKBitmap bmp, float x, float y, SKPaint? paint = null)
		{
			using var img = SKImage.FromBitmap(bmp);
			canvas.DrawImage(img, x, y, default, paint);
		}

		private static void Blit(SKCanvas canvas, SKBitmap bmp, SKRect source, SKRect dest, SKPaint? paint = null)
		{
			using var img = SKImage.FromBitmap(bmp);
			canvas.DrawImage(img, source, dest, default, paint);
		}

		private static byte[] Encode(SKBitmap bmp, RdbAssetExtractor.Tint? tint)
		{
			if (tint is { } t)
			{
				using var tinted = new SKBitmap(Unpremul(bmp.Width, bmp.Height));
				using (var canvas = new SKCanvas(tinted))
				using (var paint = new SKPaint
				{
					ColorFilter = SKColorFilter.CreateColorMatrix([t.R / 255f, 0, 0, 0, 0, 0, t.G / 255f, 0, 0, 0, 0, 0, t.B / 255f, 0, 0, 0, 0, 0, 1, 0])
				})
					Blit(canvas, bmp, 0, 0, paint);

				return EncodeRaw(tinted);
			}

			return EncodeRaw(bmp);
		}

		private static byte[] EncodeRaw(SKBitmap bmp)
		{
			using var img = SKImage.FromBitmap(bmp);
			using var data = img.Encode(SKEncodedImageFormat.Png, 100);
			return data.ToArray();
		}

		/// <summary>
		///     Forces every texel opaque: a composited skin or eye texture must not carry the overlay's transparency
		///     through.
		/// </summary>
		private static unsafe void ForceOpaque(SKBitmap bmp)
		{
			var p = (byte*)bmp.GetPixels();
			for (var i = 3; i < bmp.ByteCount; i += 4)
				p[i] = 255;
		}

		private static SKImageInfo Unpremul(int w, int h)
		{
			return new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		}

		/// <summary>
		///     One decal to lay onto a base atlas: RGBA pixels at (U, V) in 0..1, drawn <c>ScaleFraction</c> of the
		///     atlas width wide, rotated <c>RotationDeg</c> degrees and alpha-blended by <c>Blend</c>.
		/// </summary>
		public readonly record struct Decal(byte[] Rgba, int W, int H, float U, float V, float ScaleFraction, float RotationDeg, float Blend);
	}
}
