using System;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// #234/#193: the create-cutscene Kalanthes MoveTasks are CONSTRUCTED from typed data (positions + the typed
	// MoveTask codec/composite), not replayed from a hex blob. These goldens are the captured MoveTasks
	// (Blablabla_* f657 intro + f890 walk, handle 0x2C1C) - the from-scratch builders must reproduce them byte-exact.
	public class KalanthesCutsceneMoveTests
	{
		private const uint CapturedHandle = 0x2C1C;

		[Fact]
		public void BuildKalanthesIntroMove_ReproducesTheCapturedTransform()
		{
			var expected = Convert.FromHexString(
				"96c467400000c35000002c1c000043a6999a43124bf2443081aa0243a6999a43124bf2443081aa00000000bf3e417f000000003f2b49150000");
			Assert.Equal(expected, GameServerListener.BuildKalanthesIntroMove(CapturedHandle));
		}

		[Fact]
		public void BuildKalanthesWalkMove_ReproducesTheCapturedPath()
		{
			var expected = Convert.FromHexString(
				"96c467400000c35000002c1c000043a6999a43124bf2443081aa040100020101000000000243a6999a43124bf2443081aa43a25c9743116eff4430489300410912a000000000000000000000000043a6999a43124bf2443081aa0001");
			Assert.Equal(expected, GameServerListener.BuildKalanthesWalkMove(CapturedHandle));
		}
	}
}
