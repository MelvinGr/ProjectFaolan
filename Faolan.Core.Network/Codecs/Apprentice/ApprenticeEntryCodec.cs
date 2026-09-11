using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Apprentice
{
	/// <summary>
	///     <c>DispatchAddApprenticeEntry(i_cTeacher, i_cStudent)</c> /
	///     <c>DispatchRemoveApprenticeEntry(i_cTeacher, i_cStudent)</c> - the pairing roster: one entry per
	///     teacher-student bond, added when the student accepts and removed when either side leaves.
	/// </summary>
	/// <remarks>
	///     Both methods carry the same pair with the verb in the method, so they share one codec and declare
	///     <see cref="HandledMethods" /> rather than naming one of the two as the method. The pair is ordered
	///     teacher-then-student, as the client's own signature names them.
	/// </remarks>
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
