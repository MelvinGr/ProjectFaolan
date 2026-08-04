using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Apprentice
{
	// DispatchAddApprenticeEntry(i_cTeacher, i_cStudent) / DispatchRemoveApprenticeEntry(i_cTeacher, i_cStudent) -
	// GameCharInterfaceProject 0x04 / 0x05. The pairing ROSTER: one entry per teacher-student bond, added when the
	// student accepts and removed when either side leaves. Both methods carry the same pair, with the verb in the
	// method, so they share one codec (#530: `HandledMethods` rather than picking one of the two as *the* Method).
	//
	// The pair is ordered teacher-then-student, as the client's own signature names them.
	public static class ApprenticeEntryCodec
	{
		public static readonly GameCharInterfaceProjectMethods[] HandledMethods =
		[
			GameCharInterfaceProjectMethods.DispatchAddApprenticeEntry,
			GameCharInterfaceProjectMethods.DispatchRemoveApprenticeEntry
		];

		public static byte[] Build(ApprenticeEntry value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint teacherCharacterId, uint studentCharacterId)
		{
			return Build(new ApprenticeEntry(EntityIds.Character(teacherCharacterId), EntityIds.Character(studentCharacterId)));
		}

		public static ApprenticeEntry Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ApprenticeEntryCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ApprenticeEntry Read(ref SpanReader r)
		{
			return new ApprenticeEntry(EntityIdCodec.Read(ref r), EntityIdCodec.Read(ref r));
		}

		internal static void Write(FaolanStream s, ApprenticeEntry v)
		{
			EntityIdCodec.Write(s, v.Teacher);
			EntityIdCodec.Write(s, v.Student);
		}
	}

	public sealed record ApprenticeEntry(EntityId Teacher, EntityId Student);
}
