using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Apprentice
{
	/// <summary>
	///     <c>DispatchRequestApprentice(i_cTeacher)</c> - the invitation. A teacher names a student with
	///     <c>RequestApprenticeStudent(i_cStudentID)</c>, and this is what reaches that student.
	/// </summary>
	/// <remarks>
	///     The client renders the prompt off its own string ("%s wants you to become his mentor!", LDB 211711[2491]),
	///     so the body is the teacher's ID32 and nothing else. The signature comes from the client's own method table
	///     (docs/re/aoc-client-rpc-methods.md); no capture performs an apprenticeship, so the method declaration is the
	///     grammar. See docs/subsystems/apprentice.md.
	/// </remarks>
	public static class RequestApprenticeCodec
	{
		public const uint Method = (uint)GameCharInterfaceProjectMethods.DispatchRequestApprentice;

		public static byte[] Build(RequestApprentice value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint teacherCharacterId)
		{
			return Build(new RequestApprentice(EntityIds.Character(teacherCharacterId)));
		}

		public static RequestApprentice Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RequestApprenticeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static RequestApprentice Read(ref SpanReader r)
		{
			return new RequestApprentice(EntityIdCodec.Read(ref r));
		}

		internal static void Write(FaolanStream s, RequestApprentice v)
		{
			EntityIdCodec.Write(s, v.Teacher);
		}
	}

	public sealed record RequestApprentice(EntityId Teacher);
}
