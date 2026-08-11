// The client's GameAction EXECUTOR table: for every `e_Type_GC_GameAction_*` kind, the function that APPLIES it
// and the one that REMOVES it (Gitea #709/#782).
//
// `docs/re/gameaction-keylists.tsv` says what an element's fields ARE; it says nothing about what the client DOES
// with them. That is one class per kind - MSVC lays each out as an 11-slot vtable whose slots 1..6 are the same
// six shared base thunks on every one of them, which is the signature this scans for:
//
//   [ 0] the destructor
//   [ 1] 00433ac0   [ 2] 00433df0   [ 3] 00433e10   [ 4] 00433ab0   [ 5] 00433b00   [ 6] 00433ae0   <- shared
//   [ 7] per-kind    [ 8] 0089c8d0 (shared)
//   [ 9] APPLY   -   e.g. GameActionMethods_t::TempChangeSkillGameAction (008b1f20), which reads the element's
//                    keys 0x4b6/0x4b7/0x2d2 and calls SetStatOverride(statTable, stat, lerp(lo, hi, t))
//   [10] REMOVE  -   its twin, calling ClearStatOverride(statTable, stat)
//
// The RTTI name off each vtable is what ties a row back to a kind: `.?AV<Name>GameAction@@` matches
// `e_Type_GC_GameAction_<Name>` in `docs/re/gameaction-type-ids.tsv` (200 of the 214 classes join by name).
//
// ⚠️ The six shared thunks are NOT unique to GameActions - they are a common base's, so the raw signature also
// matches ~1,000 UI/XML classes. The RTTI name is the filter, and it is the reason the scan is by signature at
// all: there is no flat `type -> executor` array to read, one class per kind is the whole registry.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpGameActionExecutors.java
//
// Output: $FAOLAN_GHIDRA_OUT/gameaction_executors.tsv (falling back to the system temp dir); the committed join
// against the type ids is `docs/re/gameaction-executors.tsv`.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.MemoryBlock;

import java.io.PrintWriter;

public class DumpGameActionExecutors extends GhidraScript {
	static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

	// Slots 1..6, identical on every GameAction methods class - the signature that finds the table without
	// knowing where it starts. Taken from the two vtables reached by xref from a known executor pair
	// (TempChangeSkill @01fd7be8 and the plain stat one @01fd6d08).
	static final long[] SHARED = { 0x00433ac0L, 0x00433df0L, 0x00433e10L, 0x00433ab0L, 0x00433b00L, 0x00433ae0L };

	public void run() throws Exception {
		PrintWriter w = new PrintWriter(OUT + "gameaction_executors.tsv");
		w.println("vtable\tclass\tapply\tremove\tstub\tslot0\tslot7\tslot8");

		int found = 0;
		for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
			if (!b.isInitialized() || b.isExecute())
				continue;

			for (Address a = b.getStart(); a.compareTo(b.getEnd().subtract(0x2c)) < 0; a = a.add(4)) {
				if (!matchesShared(a))
					continue;

				String cls = rttiName(a);
				if (!cls.endsWith("GameAction@@"))
					continue;

				long apply = readU32(a.add(0x24)), remove = readU32(a.add(0x28));
				// A kind whose BOTH executors are a bare `return 1` does nothing in the client: the action is
				// server-authoritative (Hit, DPSHit, BindAtLocation, TeleportToBindLocation are all in this set).
				// That is a result, not a gap, so it gets a column rather than being filtered out.
				String stub = isStub(apply) && isStub(remove) ? "stub" : "";
				w.printf("%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s%n", a, cls,
					fn(apply), fn(remove), stub, fn(readU32(a)), fn(readU32(a.add(0x1c))), fn(readU32(a.add(0x20))));
				found++;
			}
		}

		w.close();
		println("WROTE " + OUT + "gameaction_executors.tsv (" + found + " classes)");
	}

	boolean matchesShared(Address vtable) {
		try {
			for (int i = 0; i < SHARED.length; i++)
				if (readU32(vtable.add(4L * (i + 1))) != SHARED[i])
					return false;
			return true;
		} catch (Exception e) {
			return false;
		}
	}

	// `mov al, 1; ret` is 3 bytes - anything this short cannot read a key, let alone act on one.
	boolean isStub(long va) {
		Function f = va == 0 ? null : getFunctionContaining(toAddr(va));
		return f != null && f.getBody().getNumAddresses() <= 8;
	}

	String fn(long va) {
		if (va == 0) return "-";
		Function f = getFunctionContaining(toAddr(va));
		return f == null ? String.format("%08x", va) : f.getName();
	}

	// MSVC RTTI: vtable-4 -> complete object locator, +0xC -> type descriptor, +8 -> the decorated name.
	String rttiName(Address vtable) {
		try {
			long col = readU32(vtable.subtract(4));
			long td = readU32(toAddr(col).add(0xc));
			String s = stringAt(toAddr(td).add(8));
			return s == null ? "?" : s;
		} catch (Exception e) {
			return "?";
		}
	}

	long readU32(Address a) throws Exception {
		return currentProgram.getMemory().getInt(a) & 0xFFFFFFFFL;
	}

	String stringAt(Address a) {
		if (!currentProgram.getMemory().contains(a)) return null;
		StringBuilder sb = new StringBuilder();
		try {
			for (int i = 0; i < 128; i++) {
				byte c = currentProgram.getMemory().getByte(a.add(i));
				if (c == 0) break;
				if (c < 0x20 || c > 0x7e) return null;
				sb.append((char) c);
			}
		} catch (Exception e) {
			return null;
		}
		return sb.length() >= 3 ? sb.toString() : null;
	}
}
