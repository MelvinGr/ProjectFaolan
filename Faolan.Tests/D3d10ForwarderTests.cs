using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Faolan.ClientLauncher;
using Faolan.Core.Extensions;
using Xunit;

namespace Faolan.Tests
{
	// Under Wine the DX10 client aborts the first time it preprocesses a shader its cache lacks, because Wine's d3d10.dll
	// exports D3D10PreprocessShader as a bare stub. The launcher rewrites the exe's d3d10.dll import to fao10.dll and
	// writes that DLL next to it: an export table of forwarders that sends D3D10PreprocessShader to
	// d3dcompiler_43.D3DPreprocess and every other name back to d3d10.dll.
	//
	// Both halves are pinned here against synthetic images with the real exe's shape. What carries risk is the rename:
	// it must hit the import descriptor's name and nothing else, because the exe also loads d3d10.dll by name for
	// D3D10CreateDevice, and that load has to keep reaching the real DLL.
	public class D3d10ForwarderTests : IDisposable
	{
		private readonly string _exe = Path.Combine(Path.GetTempPath(), $"faolan-d3d10-{Guid.NewGuid():N}.exe");

		public void Dispose()
		{
			File.Delete(_exe);
		}

		// The rename is in place, and in the real exe the next hint/name entry follows the name's NUL directly.
		[Fact]
		public void TheForwarderName_HasTheLengthOfTheNameItOverwrites()
		{
			Assert.Equal(D3d10ForwarderDll.TargetDll.Length, D3d10ForwarderDll.FileName.Length);
		}

		[Fact]
		public void TheForwarder_SendsTheStubToD3dCompilerAndEverythingElseToD3d10()
		{
			var dll = D3d10ForwarderDll.Build(["D3D10StateBlockMaskEnableAll", "D3D10PreprocessShader", "D3D10CreateBlob"]);

			using var reader = new PEReader(new MemoryStream(dll));
			Assert.Equal(Machine.I386, reader.PEHeaders.CoffHeader.Machine);
			Assert.True(reader.PEHeaders.IsDll);
			Assert.Equal(0, reader.PEHeaders.PEHeader!.AddressOfEntryPoint);

			var (name, exports) = ReadExports(dll);
			Assert.Equal(D3d10ForwarderDll.FileName, name);
			Assert.Equal(
				[
					("D3D10CreateBlob", "d3d10.D3D10CreateBlob"),
					("D3D10PreprocessShader", "d3dcompiler_43.D3DPreprocess"),
					("D3D10StateBlockMaskEnableAll", "d3d10.D3D10StateBlockMaskEnableAll")
				],
				exports);
		}

		// The launcher rewrites the DLL before every launch, so the bytes must not depend on the time or the order the
		// names came in.
		[Fact]
		public void TheForwarder_DependsOnTheExportsAlone()
		{
			Assert.Equal(D3d10ForwarderDll.Build(["D3D10ReflectShader", "D3D10PreprocessShader"]), D3d10ForwarderDll.Build(["D3D10PreprocessShader", "D3D10ReflectShader"]));
		}

		[Fact]
		public void TheForwarder_RefusesExportsWithoutTheStub()
		{
			Assert.Throws<ArgumentException>(() => D3d10ForwarderDll.Build(["D3D10CreateBlob"]));
		}

		// The DX10 exe's shape: a d3d10.dll import beside others, and a second "d3d10.dll" string the code loads by name.
		// Only the descriptor's name changes, and a second run finds the work done.
		[Fact]
		public void TheRedirect_RenamesOnlyTheImportDescriptor()
		{
			string[] d3d10 = ["D3D10CreateStateBlock", "D3D10PreprocessShader"];
			File.WriteAllBytes(_exe, Image(("KERNEL32.dll", ["Sleep"]), ("d3d10.dll", d3d10)));
			var before = File.ReadAllBytes(_exe);

			Assert.Equal(ClientExePatcher.Result.Patched, ClientExePatcher.RedirectD3d10Import(_exe, out var exports));
			Assert.Equal(d3d10, exports);

			var after = File.ReadAllBytes(_exe);
			Assert.Equal(["KERNEL32.dll", D3d10ForwarderDll.FileName], ImportedDlls(after));
			var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
			Assert.InRange(changed.Max() - changed.Min(), 0, D3d10ForwarderDll.FileName.Length - 1); // one name's span
			Assert.Equal(1, Occurrences(after, D3d10ForwarderDll.TargetDll)); // the string the code loads by name

			Assert.Equal(ClientExePatcher.Result.AlreadyDone, ClientExePatcher.RedirectD3d10Import(_exe, out var again));
			Assert.Equal(d3d10, again);
			Assert.Equal(after, File.ReadAllBytes(_exe));
		}

		// The DX9 exe imports d3d9.dll and no d3d10.dll; an exe that imports d3d10.dll without the stub needs no
		// forwarder either. Neither file is touched.
		[Theory]
		[InlineData("d3d9.dll", "Direct3DCreate9")]
		[InlineData("d3d10.dll", "D3D10CreateBlob")]
		public void TheRedirect_LeavesAnExeWithoutTheStubbedImportAlone(string dll, string import)
		{
			File.WriteAllBytes(_exe, Image(("KERNEL32.dll", ["Sleep"]), (dll, [import])));
			var before = File.ReadAllBytes(_exe);

			Assert.Equal(ClientExePatcher.Result.NotFound, ClientExePatcher.RedirectD3d10Import(_exe, out var exports));

			Assert.Empty(exports);
			Assert.Equal(before, File.ReadAllBytes(_exe));
		}

		// A forwarder can only export names, so an import by ordinal is an error rather than a DLL missing that entry.
		[Fact]
		public void TheRedirect_RefusesAnImportByOrdinal()
		{
			File.WriteAllBytes(_exe, Image(("d3d10.dll", ["D3D10PreprocessShader", "#7"])));

			Assert.Throws<InvalidDataException>(() => ClientExePatcher.RedirectD3d10Import(_exe, out _));
		}

		private static byte[] Image(params (string Dll, string[] Names)[] imports)
		{
			var image = new BlobBuilder();
			new ImportingImage(imports).Serialize(image);
			return image.ToArray();
		}

		private static List<string> ImportedDlls(byte[] image)
		{
			using var reader = new PEReader(new MemoryStream(image));
			var headers = reader.PEHeaders;
			var dlls = new List<string>();
			for (var at = Offset(headers, headers.PEHeader!.ImportTableDirectory.RelativeVirtualAddress); image.ReadU32Le(at + 12) != 0; at += 20)
				dlls.Add(CString(image, Offset(headers, (int)image.ReadU32Le(at + 12))));

			return dlls;
		}

		private static int Occurrences(byte[] haystack, string text)
		{
			var needle = Encoding.ASCII.GetBytes(text + "\0");
			return Enumerable.Range(0, haystack.Length - needle.Length + 1).Count(i => haystack.AsSpan(i, needle.Length).SequenceEqual(needle));
		}

		// The DLL's own name and (export, forwarder string) pairs in name-table order.
		private static (string Name, List<(string, string)> Exports) ReadExports(byte[] dll)
		{
			using var reader = new PEReader(new MemoryStream(dll));
			var headers = reader.PEHeaders;
			var directory = headers.PEHeader!.ExportTableDirectory;
			var at = Offset(headers, directory.RelativeVirtualAddress);
			var count = (int)dll.ReadU32Le(at + 24);
			var functions = Offset(headers, (int)dll.ReadU32Le(at + 28));
			var names = Offset(headers, (int)dll.ReadU32Le(at + 32));
			var ordinals = Offset(headers, (int)dll.ReadU32Le(at + 36));

			var exports = new List<(string, string)>();
			for (var i = 0; i < count; i++)
			{
				var target = (int)dll.ReadU32Le(functions + 4 * dll.ReadU16Le(ordinals + 2 * i));
				// An address inside the export directory is what makes the entry a forwarder rather than code.
				Assert.InRange(target, directory.RelativeVirtualAddress, directory.RelativeVirtualAddress + directory.Size - 1);
				exports.Add((CString(dll, Offset(headers, (int)dll.ReadU32Le(names + 4 * i))), CString(dll, Offset(headers, target))));
			}

			return (CString(dll, Offset(headers, (int)dll.ReadU32Le(at + 12))), exports);
		}

		private static string CString(byte[] bytes, int at)
		{
			return Encoding.ASCII.GetString(bytes, at, Array.IndexOf(bytes, (byte)0, at) - at);
		}

		private static int Offset(PEHeaders headers, int rva)
		{
			var section = headers.SectionHeaders[headers.GetContainingSectionIndex(rva)];
			return rva - section.VirtualAddress + section.PointerToRawData;
		}

		// A 32-bit image whose one section holds a loose "d3d10.dll" string, the import descriptors, and per DLL a lookup
		// table, its hint/name entries and the DLL name. A name written "#n" is imported by ordinal n.
		private sealed class ImportingImage : PEBuilder
		{
			private const int DescriptorsAt = 0x10;

			private readonly (string Dll, string[] Names)[] _imports;
			private DirectoryEntry _importTable;

			public ImportingImage((string Dll, string[] Names)[] imports)
				: base(new PEHeaderBuilder(Machine.I386, 0x1000, imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Bit32Machine), BlobContentId.GetTimeBasedProvider())
			{
				_imports = imports;
			}

			protected override ImmutableArray<Section> CreateSections()
			{
				return [new Section(".idata", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead)];
			}

			protected override PEDirectoriesBuilder GetDirectories()
			{
				return new PEDirectoriesBuilder { ImportTable = _importTable };
			}

			protected override BlobBuilder SerializeSection(string name, SectionLocation location)
			{
				var rva = (uint)location.RelativeVirtualAddress;
				var section = new byte[0x400];
				Encoding.ASCII.GetBytes(D3d10ForwarderDll.TargetDll).CopyTo(section, 0);

				var cursor = DescriptorsAt + 20 * (_imports.Length + 1);
				for (var d = 0; d < _imports.Length; d++)
				{
					var (dll, names) = _imports[d];
					var lookup = cursor;
					cursor += 4 * (names.Length + 1);
					for (var n = 0; n < names.Length; n++)
					{
						if (names[n].StartsWith('#'))
						{
							section.AsSpan().WriteU32Le(lookup + 4 * n, 0x80000000 | uint.Parse(names[n][1..]));
							continue;
						}

						section.AsSpan().WriteU32Le(lookup + 4 * n, rva + (uint)cursor);
						cursor += 2; // the hint
						cursor = (cursor + WriteString(section, cursor, names[n]) + 1) & ~1;
					}

					var dllName = cursor;
					cursor += WriteString(section, cursor, dll);

					var descriptor = DescriptorsAt + 20 * d;
					section.AsSpan().WriteU32Le(descriptor, rva + (uint)lookup);
					section.AsSpan().WriteU32Le(descriptor + 12, rva + (uint)dllName);
					section.AsSpan().WriteU32Le(descriptor + 16, rva + (uint)lookup);
				}

				_importTable = new DirectoryEntry((int)rva + DescriptorsAt, 20 * (_imports.Length + 1));
				var blob = new BlobBuilder();
				blob.WriteBytes(section);
				return blob;
			}

			private static int WriteString(byte[] section, int at, string value)
			{
				Encoding.ASCII.GetBytes(value).CopyTo(section, at);
				return value.Length + 1;
			}
		}
	}
}
