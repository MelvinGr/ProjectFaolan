using System;
using Faolan.GameServer.World;
using Xunit;
using CreateFlowService = Faolan.GameServer.CreateFlow.CreateFlowService;

namespace Faolan.Tests
{
	// #223: the two create-world ScryScriptIIR (0xA36D3B74) records - the "Slave Galley" PreloadAssets step and the
	// "PlayOrSkip" ScriptMessage - are now CONSTRUCTED via ScryScript.BuildStep from typed protobuf fields instead of
	// the verbatim hex blobs in RetailCreateEditorCreationScriptBurst. These prove the construction is byte-identical
	// to the captured records (so the create-world cutscene wire is unchanged), for an arbitrary player handle.
	public class ScryScriptCreateBurstTests
	{
		// the captured record suffixes after [subop][0xC350][handle] = [flag][u32 pbLen][protobuf]
		private const string Rec1Suffix =
			"00000002f708051007180022b9013c72656d6f7465666f726d617420746f6b656e3d22706c61796f72736b6970283732297c6f6e636c69656e74636f6e6e65637465642831297c4e45575f6368617261637465725f6372656174696f6e20536c6176652047616c6c657920287365727665722d7369646529222063617465676f72793d22353030323322206b65793d222671756f743b425d535a2e414752477241794f47652a42472d22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e2aa604313031303030343a3739333731333b313031303030343a3739333731343b313037303030333a343030303b313030303039373a343030303b313031303033303a3135363231313b313031303033303a3233393434383b313031303033303a3536353931333b313031303030343a333732383533333b313036303636383a343030303b313030303035393a343030303b313036303636373a3136373336383b313031303030343a333037353434353b313031303030343a333138303032353b313031303030343a3232313238353b313031303030343a333636333139303b313031303030343a3230383932333b313031303030343a3230383236363b313031303030343a3232363839313b313031303030343a333636333139313b313031303030343a333635363130393b313031303033303a3231303036393b313031303030313a3231303037303b313031303030343a333037353435323b313031303030343a333037353435303b313031303030343a333037353435313b313031303330303a343030303b313030303034393a343030303b313036363631313a343030303b313030303031333a34303030303030303b313030303031353a34303030303030303b313031303530363a343030303b313030303035383a343030303b313031303230373a3131303030363b313031303230373a3236323234313b313031303230373a3536383830363b313031303230373a333432303333313b313031303230373a313637313833320a08dc84f90210df8bbf04";

		private const string Rec2Suffix = "000000001a080510021800220a506c61794f72536b69702a00320408001000";
		private const uint Handle = 0x0802E5D4u;

		[Fact]
		public void PlayOrSkipStep_BuildsTheCapturedRecord()
		{
			var body = ScryScript.BuildStep(Handle, "PlayOrSkip");
			Assert.Equal(Expected(Rec2Suffix), Convert.ToHexStringLower(body));
		}

		[Fact]
		public void PreloadAssetsStep_BuildsTheCapturedRecord()
		{
			var body = ScryScript.BuildStep(Handle, CreateFlowService.CreateWorldScriptToken, 7, resources: CreateFlowService.CreateWorldResourceManifest, f6Field1: 6177372, f6Field2: 9422303);
			Assert.Equal(Expected(Rec1Suffix), Convert.ToHexStringLower(body));
		}

		// The post-morph StartScriptGroupOnClient callback (no token form, #223) from CreateWorldHandshake.
		[Fact]
		public void StartScriptGroup_BuildsTheCapturedRecord()
		{
			// captured suffix [flag=00][u32 pbLen=0E][08 05 10 00 18 EC 97 02 32 04 08 00 10 00]
			var body = ScryScript.BuildStartScriptGroup(Handle, 35820);
			Assert.Equal(Expected("000000000e0805100018ec9702320408001000"), Convert.ToHexStringLower(body));
		}

		private static string Expected(string suffix)
		{
			return "a36d3b740000c350" + Handle.ToString("x8") + suffix;
		}
	}
}
