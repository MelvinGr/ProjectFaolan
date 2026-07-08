using System;
using System.Collections.Generic;
using System.Reflection;

namespace Faolan.Core.Network.Codecs
{
	// #244 stat-core lift: decomposes a protobuf-net stat-core sub-message (CharacterStats / NpcStats / CharacterBcc)
	// into a flat name->value map of exactly the fields that are SET, and recomposes an identical message from such a
	// map. The three sub-messages carry only scalar fields and NO protobuf extension data (verified across all 1043
	// captured cores), so a message rebuilt from the map re-serialises byte-for-byte - which is what lets the NPC
	// spawn body be constructed from typed columns instead of spliced from the captured Data blob. Reflection-driven
	// (keyed on the generated ShouldSerializeX() presence methods) so it needs no per-field code and can't drift from
	// the proto. The type/instance ROUTING (which field lands on Npc vs NpcSpawn) is the caller's job; this codec only
	// does the lossless message<->map round-trip.
	public static class NpcStatCoreCodec
	{
		// name -> boxed value, for every field whose ShouldSerializeX() is true (i.e. explicitly set on the wire).
		public static Dictionary<string, object> Decompose(object message)
		{
			var map = new Dictionary<string, object>();
			if (message == null)
				return map;

			var t = message.GetType();
			foreach (var (name, prop, should) in Fields(t))
			{
				if (should.Invoke(message, null) is true)
					map[name] = prop.GetValue(message);
			}

			return map;
		}

		// Builds a fresh T with exactly the fields present in `map` set (missing keys stay unset, so ShouldSerialize
		// stays false and the field is omitted on the wire).
		public static T Compose<T>(IReadOnlyDictionary<string, object> map) where T : new()
		{
			var msg = new T();
			if (map == null)
				return msg;

			var t = typeof(T);
			foreach (var (name, prop, _) in Fields(t))
			{
				if (map.TryGetValue(name, out var value) && value != null)
					prop.SetValue(msg, Convert.ChangeType(value, prop.PropertyType));
			}

			return msg;
		}

		// The proto scalar fields of a protobuf-net message: public read/write properties that have a matching
		// ShouldSerialize{Name}() presence method (the extension-data / reset helpers have no such property pair).
		private static IEnumerable<(string Name, PropertyInfo Prop, MethodInfo Should)> Fields(Type t)
		{
			foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
					continue;
				var should = t.GetMethod("ShouldSerialize" + prop.Name, Type.EmptyTypes);
				if (should != null && should.ReturnType == typeof(bool))
					yield return (prop.Name, prop, should);
			}
		}
	}
}
