using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Apprentice
{
	/// <summary>
	///     Apprenticeship (mentoring): a higher-level teacher takes a lower-level team-mate as an apprentice, and the
	///     apprentice fights at the teacher's level for as long as the bond lasts.
	/// </summary>
	/// <remarks>
	///     <para>
	///         No capture performs an apprenticeship, so - like quest sharing - the client's own method table is the
	///         grammar and the client's own strings are the rules. Every rule here is quoted from the LDB; the citation
	///         is the string id in category 211711 (the English UI page):
	///     </para>
	///     <list type="bullet">
	///         <item>[993] "The teacher and apprentice need to be in the same group."</item>
	///         <item>[2748] "The teacher needs to be at least 2 levels higher than the apprentice."</item>
	///         <item>[2828] "The teacher cannot be more than 20 levels higher than the apprentice."</item>
	///         <item>[1147] "The apprentice already has a teacher."</item>
	///         <item>[1031] "%s declined apprentice request." / [1296] "%s accepted apprentice request"</item>
	///         <item>[1879] "Your apprentice level increased to %d."</item>
	///     </list>
	///     <para>
	///         The client checks these before it sends, so a refusal here is the belt-and-braces half. It is still
	///         answered rather than dropped: a client that got past its own check is worth telling.
	///     </para>
	///     <para>
	///         A bond is live state - it cannot outlive either connection, so nothing here is persisted. The
	///         apprentice's level boost is a pushed stat, not a stored one, which is what makes a disconnect
	///         self-healing.
	///     </para>
	/// </remarks>
	public sealed class ApprenticeService
	{
		// The client's own window, from its two refusal strings; inclusive on both ends ("at least 2", "not more
		// than 20"), so a teacher exactly 1 level up is refused and so is one 21 up.
		public const uint MinLevelsAbove = 2;
		public const uint MaxLevelsAbove = 20;

		private readonly IApprenticeOutbound _outbound;
		private readonly ICsPlayerAgentProvider _teams;
		private readonly ILogger _logger;

		// student -> teacher. A student has at most one teacher ("The apprentice already has a teacher"), while a
		// teacher may hold several - which is why the roster message is per entry rather than per character.
		private readonly ConcurrentDictionary<uint, uint> _teacherOf = [];

		// student -> the teacher whose request is open on them. Keyed by the student for the same reason the trade and
		// quest-share pending maps are keyed by their target: the accept body names the other party, but what it binds
		// comes from here, so a forged accept cannot conjure a teacher who never asked.
		private readonly ConcurrentDictionary<uint, uint> _pendingByStudent = [];

		public ApprenticeService(IApprenticeOutbound outbound, ICsPlayerAgentProvider teams, ILogger logger)
		{
			_outbound = outbound;
			_teams = teams;
			_logger = logger;
		}

		/// <summary>The teacher <paramref name="characterId" /> is apprenticed to, or null.</summary>
		public uint? TeacherOf(uint characterId)
		{
			return _teacherOf.TryGetValue(characterId, out var teacher) ? teacher : null;
		}

		/// <summary>c2s RequestApprenticeStudent (GameCharAgentProject 0x04): the teacher names a student.</summary>
		public async Task Request(uint teacherId, uint studentId)
		{
			if (teacherId == studentId)
				return;

			var teacherLevel = _outbound.LevelOf(teacherId);
			var studentLevel = _outbound.LevelOf(studentId);
			if (teacherLevel == null || studentLevel == null)
			{
				await _outbound.SendApprenticeFeedback(teacherId, "That player is not available.");
				return;
			}

			// The roster includes the caller, so a solo player's is 0 or 1 - the CS tier only mints a team once
			// someone joins.
			if (!_teams.TeamMembersOf(teacherId).Contains(studentId))
			{
				await _outbound.SendApprenticeFeedback(teacherId, "The teacher and apprentice need to be in the same group.");
				return;
			}

			if (_teacherOf.ContainsKey(studentId))
			{
				await _outbound.SendApprenticeFeedback(teacherId, "The apprentice already has a teacher.");
				return;
			}

			// Both bounds are on the gap, and both refusals are the client's own wording.
			if (teacherLevel.Value < studentLevel.Value + MinLevelsAbove)
			{
				await _outbound.SendApprenticeFeedback(teacherId, "The teacher needs to be at least 2 levels higher than the apprentice.");
				return;
			}

			if (teacherLevel.Value > studentLevel.Value + MaxLevelsAbove)
			{
				await _outbound.SendApprenticeFeedback(teacherId, "The teacher cannot be more than 20 levels higher than the apprentice.");
				return;
			}

			if (!await _outbound.SendApprenticeRequest(studentId, teacherId))
			{
				await _outbound.SendApprenticeFeedback(teacherId, "That player is not available.");
				return;
			}

			// Recorded last, so a request that never reached the student leaves nothing to accept.
			_pendingByStudent[studentId] = teacherId;
			_logger.LogInformation("Apprentice: {Teacher} (level {TeacherLevel}) asked {Student} (level {StudentLevel}).",
				teacherId, teacherLevel, studentId, studentLevel);
		}

		/// <summary>
		///     c2s AcceptApprenticeTeacher (GameCharAgentProject 0x02): the student accepts. The body names the teacher,
		///     but the bond is made from the pending entry - the body only has to agree with it.
		/// </summary>
		public async Task Accept(uint studentId, uint teacherId)
		{
			if (!_pendingByStudent.TryRemove(studentId, out var pending) || pending != teacherId)
			{
				_logger.LogInformation("Apprentice: {Student} accepted {Teacher}, who has no open request.", studentId, teacherId);
				return;
			}

			var teacherLevel = _outbound.LevelOf(teacherId);
			if (teacherLevel == null)
			{
				await _outbound.SendApprenticeFeedback(studentId, "That player is not available.");
				return;
			}

			_teacherOf[studentId] = teacherId;

			// Both sides get the roster entry, so each renders the pairing it is in.
			await _outbound.SendApprenticeEntry(teacherId, teacherId, studentId, true);
			await _outbound.SendApprenticeEntry(studentId, teacherId, studentId, true);
			await _outbound.SendApprenticeMode(studentId, true);

			// The effect of the bond: the apprentice fights at the teacher's level. The client's mitigation term is
			// `(2.0 - level / ApprenticeLevel)`, so raising this above the real level is what makes the apprentice
			// survivable next to their teacher; leaving the two equal is the neutral 1.0 sent everywhere else.
			await _outbound.SendApprenticeLevel(studentId, teacherLevel.Value);

			var studentName = _outbound.NameOf(studentId);
			if (studentName != null)
				await _outbound.SendApprenticeFeedback(teacherId, $"{studentName} accepted apprentice request");

			_logger.LogInformation("Apprentice: {Student} is now apprenticed to {Teacher} at level {Level}.", studentId, teacherId, teacherLevel);
		}

		/// <summary>c2s DeclineApprenticeTeacher (GameCharAgentProject 0x03).</summary>
		public async Task Decline(uint studentId, uint teacherId)
		{
			if (!_pendingByStudent.TryRemove(studentId, out var pending) || pending != teacherId)
				return;

			var studentName = _outbound.NameOf(studentId);
			if (studentName != null)
				await _outbound.SendApprenticeFeedback(teacherId, $"{studentName} declined apprentice request.");

			_logger.LogInformation("Apprentice: {Student} declined {Teacher}.", studentId, teacherId);
		}

		/// <summary>
		///     c2s LeaveApprenticeMode (GameCharAgentProject 0x05): either side ends it. A teacher leaving ends every
		///     bond they hold - the client has one "Leave apprenticeship" button for both roles.
		/// </summary>
		public async Task Leave(uint characterId)
		{
			await End(characterId, "left");
		}

		/// <summary>
		///     The character is gone (disconnect, or a teleport that drops them out of the world). Ends every bond they
		///     are in, the same as leaving - a bond cannot outlive either connection.
		/// </summary>
		public async Task Forget(uint characterId)
		{
			_pendingByStudent.TryRemove(characterId, out _);
			foreach (var student in _pendingByStudent.Where(p => p.Value == characterId).Select(p => p.Key).ToList())
				_pendingByStudent.TryRemove(student, out _);

			await End(characterId, "disconnected");
		}

		private async Task End(uint characterId, string why)
		{
			var ended = new List<(uint Teacher, uint Student)>();

			if (_teacherOf.TryRemove(characterId, out var teacher))
				ended.Add((teacher, characterId));

			foreach (var student in _teacherOf.Where(p => p.Value == characterId).Select(p => p.Key).ToList())
				if (_teacherOf.TryRemove(student, out var mine))
					ended.Add((mine, student));

			foreach (var (t, s) in ended)
			{
				await _outbound.SendApprenticeEntry(t, t, s, false);
				await _outbound.SendApprenticeEntry(s, t, s, false);
				await _outbound.SendApprenticeMode(s, false);

				// Back to the neutral term. The student's own level is the value everything else pushes, so this is a
				// restore rather than a clear - ApprenticeLevel 0 would divide the mitigation by zero.
				var studentLevel = _outbound.LevelOf(s);
				if (studentLevel != null)
					await _outbound.SendApprenticeLevel(s, studentLevel.Value);

				_logger.LogInformation("Apprentice: {Teacher}/{Student} ended ({Why}).", t, s, why);
			}
		}
	}
}
