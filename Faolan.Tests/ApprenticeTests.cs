using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.Apprentice;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.GameServer.Apprentice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// Apprenticeship (#144). No capture performs one, so there is no golden blob to compare against - what these gate
	// is the two things that CAN be wrong without a capture: the bodies against the client's own method signatures,
	// and the rules against the client's own strings.
	public class ApprenticeTests
	{
		private const uint Teacher = 0x00001234;
		private const uint Student = 0x00005678;

		// `DispatchRequestApprentice(const ID32& i_cTeacher)` - one ID32 and nothing else. An ID32 on this wire is
		// `[u32 class][u32 instance]`, and a character's class word is 0xC350.
		[Fact]
		public void RequestApprentice_IsOneCharacterId32()
		{
			byte[] expected = [0x00, 0x00, 0xC3, 0x50, 0x00, 0x00, 0x12, 0x34];
			Assert.Equal(expected, RequestApprenticeCodec.Build(Teacher));
			Assert.Equal(Teacher, RequestApprenticeCodec.Parse(expected).Teacher.Id);
		}

		// `DispatchAddApprenticeEntry(i_cTeacher, i_cStudent)` - the pair, teacher first, as the signature names them.
		[Fact]
		public void ApprenticeEntry_IsTheTeacherThenTheStudent()
		{
			byte[] expected =
			[
				0x00, 0x00, 0xC3, 0x50, 0x00, 0x00, 0x12, 0x34,
				0x00, 0x00, 0xC3, 0x50, 0x00, 0x00, 0x56, 0x78
			];
			Assert.Equal(expected, ApprenticeEntryCodec.Build(Teacher, Student));

			var parsed = ApprenticeEntryCodec.Parse(expected);
			Assert.Equal(Teacher, parsed.Teacher.Id);
			Assert.Equal(Student, parsed.Student.Id);
		}

		// `DispatchApprenticeModeOnOff(i_bOnOff)` - one bool, one byte.
		[Fact]
		public void ApprenticeMode_IsOneByte()
		{
			Assert.Equal([0x01], ApprenticeModeCodec.Build(true));
			Assert.Equal([0x00], ApprenticeModeCodec.Build(false));
			Assert.True(ApprenticeModeCodec.Parse([0x01]).On);
			Assert.False(ApprenticeModeCodec.Parse([0x00]).On);
		}

		// The two level bounds are the client's own refusal strings: "at least 2 levels higher" and "not more than 20".
		// Both are inclusive, so a gap of exactly 2 and exactly 20 are the legal extremes.
		[Theory]
		[InlineData(10u, 11u, false)] // 1 up - too close
		[InlineData(10u, 12u, true)]  // exactly 2 up
		[InlineData(10u, 30u, true)]  // exactly 20 up
		[InlineData(10u, 31u, false)] // 21 up - too far
		[InlineData(10u, 10u, false)] // same level
		public async Task RequestIsRefusedOutsideTheClientsOwnLevelWindow(uint studentLevel, uint teacherLevel, bool allowed)
		{
			var outbound = new FakeOutbound { Levels = { [Teacher] = teacherLevel, [Student] = studentLevel } };
			var service = new ApprenticeService(outbound, new FakeTeams(Teacher, Student), NullLogger.Instance);

			await service.Request(Teacher, Student);
			Assert.Equal(allowed, outbound.Requests.Contains((Student, Teacher)));
		}

		[Fact]
		public async Task RequestIsRefusedOutsideTheTeam()
		{
			var outbound = new FakeOutbound { Levels = { [Teacher] = 30, [Student] = 10 } };
			var service = new ApprenticeService(outbound, new FakeTeams(), NullLogger.Instance);

			await service.Request(Teacher, Student);
			Assert.Empty(outbound.Requests);
			Assert.Contains(outbound.Feedback, f => f.Message.Contains("same group"));
		}

		// The accept binds from the PENDING entry, not from the body - so an accept naming a teacher who never asked
		// cannot conjure a bond. Same rule the trade start and the quest-share accept follow.
		[Fact]
		public async Task AcceptWithoutARequestBindsNothing()
		{
			var outbound = new FakeOutbound { Levels = { [Teacher] = 30, [Student] = 10 } };
			var service = new ApprenticeService(outbound, new FakeTeams(Teacher, Student), NullLogger.Instance);

			await service.Accept(Student, Teacher);
			Assert.Null(service.TeacherOf(Student));
			Assert.Empty(outbound.Entries);
		}

		// The whole point of the bond: the apprentice's ApprenticeLevel becomes the teacher's, which is the term the
		// client's armour mitigation divides the real level by.
		[Fact]
		public async Task AcceptBondsBothSidesAndRaisesTheApprenticeLevel()
		{
			var outbound = new FakeOutbound { Levels = { [Teacher] = 30, [Student] = 10 } };
			var service = new ApprenticeService(outbound, new FakeTeams(Teacher, Student), NullLogger.Instance);

			await service.Request(Teacher, Student);
			await service.Accept(Student, Teacher);

			Assert.Equal(Teacher, service.TeacherOf(Student));
			Assert.Contains((Teacher, Teacher, Student, true), outbound.Entries);
			Assert.Contains((Student, Teacher, Student, true), outbound.Entries);
			Assert.Contains((Student, true), outbound.Modes);
			Assert.Equal(30u, Assert.Single(outbound.Levels_Pushed, l => l.Character == Student).Level);
		}

		// Leaving restores the neutral term rather than clearing it - ApprenticeLevel 0 would divide by zero in the
		// client's mitigation formula.
		[Fact]
		public async Task LeavingRestoresTheApprenticesOwnLevel()
		{
			var outbound = new FakeOutbound { Levels = { [Teacher] = 30, [Student] = 10 } };
			var service = new ApprenticeService(outbound, new FakeTeams(Teacher, Student), NullLogger.Instance);

			await service.Request(Teacher, Student);
			await service.Accept(Student, Teacher);
			await service.Leave(Student);

			Assert.Null(service.TeacherOf(Student));
			Assert.Contains((Teacher, Teacher, Student, false), outbound.Entries);
			Assert.Contains((Student, false), outbound.Modes);
			Assert.Equal(10u, outbound.Levels_Pushed.Last(l => l.Character == Student).Level);
		}

		// A bond cannot outlive either connection, and it has to end from the TEACHER's side too - otherwise the
		// apprentice keeps a pushed level boost with nobody on the other end of it.
		[Fact]
		public async Task TheTeacherDisconnectingEndsTheBond()
		{
			var outbound = new FakeOutbound { Levels = { [Teacher] = 30, [Student] = 10 } };
			var service = new ApprenticeService(outbound, new FakeTeams(Teacher, Student), NullLogger.Instance);

			await service.Request(Teacher, Student);
			await service.Accept(Student, Teacher);
			await service.Forget(Teacher);

			Assert.Null(service.TeacherOf(Student));
			Assert.Equal(10u, outbound.Levels_Pushed.Last(l => l.Character == Student).Level);
		}

		[Fact]
		public async Task AStudentMayOnlyHaveOneTeacher()
		{
			const uint other = 0x00009999;
			var outbound = new FakeOutbound { Levels = { [Teacher] = 30, [Student] = 10, [other] = 25 } };
			var service = new ApprenticeService(outbound, new FakeTeams(Teacher, Student, other), NullLogger.Instance);

			await service.Request(Teacher, Student);
			await service.Accept(Student, Teacher);
			await service.Request(other, Student);

			Assert.Contains(outbound.Feedback, f => f.Message.Contains("already has a teacher"));
			Assert.DoesNotContain((Student, other), outbound.Requests);
		}

		private sealed class FakeTeams(params uint[] members) : ICsPlayerAgentProvider
		{
			public Task<bool> NotifyMailReceived(uint recipientId, ulong mailId)
			{
				return Task.FromResult(false); // no CS tier in these tests
			}

			public Task<bool> DepositToGuildBank(uint characterId, ulong amountTin)
			{
				return Task.FromResult(false); // no guild tier in these tests
			}

			public IReadOnlyList<uint> TeamMembersOf(uint characterId)
			{
				return members.Contains(characterId) ? members : [];
			}

			// One fixed team, so its id is a constant - this fake exists for the roster, not for #512's routing.
			public PvpMatchWorldEntry? PvpMatchEntryOf(uint characterId, uint mapId) => null;
			public Task<string> ForcePvpMatchStop(uint characterId, string message) => Task.FromResult("");
			public bool ArePvpMatchOpponents(uint attackerId, uint victimId) => false;
			public Task CreditPvpMatchKill(uint killerId, uint victimId) => Task.CompletedTask;

			public Task<string> ForcePvpMatchStart() => Task.FromResult("not in this fake");

			public uint? TeamIdOf(uint characterId)
			{
				return members.Contains(characterId) ? 1u : null;
			}

			public Task<bool> LeaveTeam(uint characterId)
			{
				return Task.FromResult(false);
			}

			public Task<bool> KickFromTeam(uint leaderId, uint characterId)
			{
				return Task.FromResult(false);
			}
		}

		private sealed class FakeOutbound : IApprenticeOutbound
		{
			public Dictionary<uint, uint> Levels { get; } = [];
			public List<(uint Student, uint Teacher)> Requests { get; } = [];
			public List<(uint Recipient, uint Teacher, uint Student, bool Added)> Entries { get; } = [];
			public List<(uint Recipient, bool On)> Modes { get; } = [];
			public List<(uint Character, uint Level)> Levels_Pushed { get; } = [];
			public List<(uint Character, string Message)> Feedback { get; } = [];

			public Task<bool> SendApprenticeRequest(uint studentId, uint teacherId)
			{
				Requests.Add((studentId, teacherId));
				return Task.FromResult(true);
			}

			public Task SendApprenticeEntry(uint recipientId, uint teacherId, uint studentId, bool added)
			{
				Entries.Add((recipientId, teacherId, studentId, added));
				return Task.CompletedTask;
			}

			public Task SendApprenticeMode(uint recipientId, bool on)
			{
				Modes.Add((recipientId, on));
				return Task.CompletedTask;
			}

			public Task SendApprenticeLevel(uint characterId, uint apprenticeLevel)
			{
				Levels_Pushed.Add((characterId, apprenticeLevel));
				return Task.CompletedTask;
			}

			public Task SendApprenticeFeedback(uint characterId, string message)
			{
				Feedback.Add((characterId, message));
				return Task.CompletedTask;
			}

			public uint? LevelOf(uint characterId)
			{
				return Levels.TryGetValue(characterId, out var level) ? level : null;
			}

			public string? NameOf(uint characterId)
			{
				return $"Char{characterId:X}";
			}
		}
	}
}
