using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Apprentice
{
	// DispatchRequestApprentice(i_cTeacher) - GameCharInterfaceProject 0x03. The invitation: a teacher named a
	// student with `RequestApprenticeStudent(i_cStudentID)`, and this is what reaches that student. The client
	// renders the prompt itself off its own string ("%s wants you to become his mentor!", LDB 211711[2491]), so
	// the body is the teacher's ID32 and nothing else.
	//
	// Signature from the client's own method table (docs/re/aoc-client-rpc-methods.md); no capture performs an
	// apprenticeship, so the METHOD is the grammar (see the `client_method_table_decodes_uncaptured_bodies` note
	// in docs/subsystems/apprentice.md).
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
