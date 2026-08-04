using System.Threading.Tasks;

namespace Faolan.GameServer.Apprentice
{
	// The wire side of apprenticeship, as a narrow port the listener implements - the same shape IQuestShareOutbound
	// takes for the other two-player interaction. ApprenticeService owns the rules and reaches everything else through
	// this, so it never holds the listener.
	//
	// Everything is addressed by CHARACTER ID: an apprenticeship always spans two connections, and the service has no
	// business resolving either.
	public interface IApprenticeOutbound
	{
		/// <summary>
		///     s2c DispatchRequestApprentice (GameCharInterfaceProject 0x03): raise the mentor prompt on
		///     <paramref name="studentId" />'s client, naming the teacher. False when that character is not connected here.
		/// </summary>
		Task<bool> SendApprenticeRequest(uint studentId, uint teacherId);

		/// <summary>
		///     s2c DispatchAddApprenticeEntry / DispatchRemoveApprenticeEntry (0x04 / 0x05): one roster entry for the
		///     teacher-student bond. Sent to both parties, so each sees the pairing it is in.
		/// </summary>
		Task SendApprenticeEntry(uint recipientId, uint teacherId, uint studentId, bool added);

		/// <summary>s2c DispatchApprenticeModeOnOff (GameCharInterface 0x81): light or clear the apprenticeship UI.</summary>
		Task SendApprenticeMode(uint recipientId, bool on);

		/// <summary>
		///     Push stat <c>ApprenticeLevel</c> (0x3C5) for <paramref name="characterId" />. This is the effect of the
		///     bond - the client's armour mitigation divides the real level by it (see CombatResolver).
		/// </summary>
		Task SendApprenticeLevel(uint characterId, uint apprenticeLevel);

		/// <summary>A plain on-screen line, for the refusals the client did not filter out before sending.</summary>
		Task SendApprenticeFeedback(uint characterId, string message);

		/// <summary><paramref name="characterId" />'s level, or null when they are not in the world here.</summary>
		uint? LevelOf(uint characterId);

		/// <summary><paramref name="characterId" />'s name, or null when they are not in the world here.</summary>
		string? NameOf(uint characterId);
	}
}
