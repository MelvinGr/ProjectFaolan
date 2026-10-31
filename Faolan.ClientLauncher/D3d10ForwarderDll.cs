using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Faolan.ClientLauncher
{
	/// <summary>
	///     Builds fao10.dll, the export-only DLL that AgeOfConanDX10.exe imports in place of d3d10.dll under Wine, so
	///     the client's shader loader no longer reaches a function Wine leaves unimplemented.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Wine's d3d10.dll declares D3D10PreprocessShader as a bare stub in its spec file (so in Wine master and in
	///         Proton Experimental 11.0), and the first call aborts the process with "unimplemented function
	///         d3d10.dll.D3D10PreprocessShader". The DX10 client calls it (0x012565cb, ShaderManager.dx10.cpp, reached
	///         from 0x012599bc) for every shader permutation that shader.cache.dx10 does not hold, which applying a
	///         changed video setting is enough to produce. It is the only import of the DX10 exe that Wine stubs.
	///     </para>
	///     <para>
	///         The DLL holds nothing but an export table of forwarders. D3D10PreprocessShader forwards to
	///         d3dcompiler_43.D3DPreprocess, which takes the same seven stdcall arguments; Wine forwards
	///         D3D10CreateBlob and D3D10GetInputSignatureBlob to that DLL the same way. Every other name forwards to
	///         d3d10.dll unchanged. With no code there is no entry point and nothing to relocate.
	///     </para>
	/// </remarks>
	internal static class D3d10ForwarderDll
	{
		/// <summary>The import the forwarder replaces.</summary>
		public const string TargetDll = "d3d10.dll";

		/// <summary>
		///     The forwarder's file name. It overwrites <see cref="TargetDll" /> inside the exe's import descriptor,
		///     where the next entry follows the terminating NUL directly, so the two names are the same length.
		/// </summary>
		public const string FileName = "fao10.dll";

		/// <summary>The d3d10.dll export that Wine leaves unimplemented.</summary>
		public const string StubbedExport = "D3D10PreprocessShader";

		private const string StubReplacement = "d3dcompiler_43.D3DPreprocess";

		/// <summary>
		///     The forwarder DLL for <paramref name="exports" />, the names the exe imports from d3d10.dll. The bytes
		///     depend on the names alone, so rewriting the file before every launch leaves it unchanged.
		/// </summary>
		public static byte[] Build(IReadOnlyCollection<string> exports)
		{
			ArgumentNullException.ThrowIfNull(exports);
			if (!exports.Contains(StubbedExport))
				throw new ArgumentException($"The forwarder exists to replace {StubbedExport}, which is not among the exports.", nameof(exports));

			var header = new PEHeaderBuilder(
				Machine.I386,
				sectionAlignment: 0x1000,
				imageBase: 0x10000000,
				subsystem: Subsystem.WindowsGui,
				dllCharacteristics: DllCharacteristics.DynamicBase | DllCharacteristics.NxCompatible | DllCharacteristics.NoSeh,
				imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage | Characteristics.Bit32Machine);

			var image = new BlobBuilder();
			new ForwarderImage(header, [.. exports.Distinct().Order(StringComparer.Ordinal)]).Serialize(image);
			return image.ToArray();
		}

		/// <summary>Where <paramref name="export" /> forwards to, as a "module.export" string.</summary>
		public static string ForwarderFor(string export)
		{
			return export == StubbedExport
				? StubReplacement
				: $"{TargetDll[..^4]}.{export}";
		}

		/// <summary>A PE with one read-only section holding the export directory and everything it points at.</summary>
		private sealed class ForwarderImage : PEBuilder
		{
			private const int ExportDirectorySize = 40;

			private readonly string[] _exports;
			private DirectoryEntry _exportTable;

			/// <param name="exports">The export names in ordinal order, which the loader's binary search requires.</param>
			public ForwarderImage(PEHeaderBuilder header, string[] exports)
				: base(header, ContentId)
			{
				_exports = exports;
			}

			protected override ImmutableArray<Section> CreateSections()
			{
				return [new Section(".rdata", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead)];
			}

			protected override PEDirectoriesBuilder GetDirectories()
			{
				return new PEDirectoriesBuilder { ExportTable = _exportTable };
			}

			// The export directory, then the address table (one forwarder-string RVA per export: an address inside the
			// export directory's range is what marks an export as forwarded), the name pointer table, the ordinal table,
			// and last the strings it all points at.
			protected override BlobBuilder SerializeSection(string name, SectionLocation location)
			{
				var count = _exports.Length;
				var addressTable = location.RelativeVirtualAddress + ExportDirectorySize;
				var nameTable = addressTable + 4 * count;
				var ordinalTable = nameTable + 4 * count;
				var stringsAt = ordinalTable + 2 * count;

				var strings = new BlobBuilder();
				var dllName = AddString(FileName);
				var names = _exports.Select(AddString).ToArray();
				var forwarders = _exports.Select(e => AddString(ForwarderFor(e))).ToArray();

				var section = new BlobBuilder();
				section.WriteUInt32(0); // Characteristics
				section.WriteUInt32(0); // TimeDateStamp
				section.WriteUInt16(0); // MajorVersion
				section.WriteUInt16(0); // MinorVersion
				section.WriteInt32(dllName);
				section.WriteInt32(1); // Base: the first ordinal
				section.WriteInt32(count); // NumberOfFunctions
				section.WriteInt32(count); // NumberOfNames
				section.WriteInt32(addressTable);
				section.WriteInt32(nameTable);
				section.WriteInt32(ordinalTable);
				foreach (var forwarder in forwarders)
					section.WriteInt32(forwarder);
				foreach (var exportName in names)
					section.WriteInt32(exportName);
				for (var i = 0; i < count; i++)
					section.WriteUInt16((ushort)i);
				section.LinkSuffix(strings);

				_exportTable = new DirectoryEntry(location.RelativeVirtualAddress, section.Count);
				return section;

				int AddString(string value)
				{
					var at = stringsAt + strings.Count;
					strings.WriteBytes(Encoding.ASCII.GetBytes(value));
					strings.WriteByte(0);
					return at;
				}
			}

			// A content-derived id, so the COFF timestamp and therefore the whole file depend on the exports alone.
			private static BlobContentId ContentId(IEnumerable<Blob> content)
			{
				using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
				foreach (var blob in content)
					hash.AppendData(blob.GetBytes());

				return BlobContentId.FromHash(hash.GetHashAndReset());
			}
		}
	}
}
