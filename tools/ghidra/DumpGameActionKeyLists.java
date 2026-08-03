// Recovers every GameAction type descriptor's KEY LIST - and its per-slot field NAMES - statically, from the
// initialiser FUN_008bdf40. The key list is the type's field grammar: a GameAction element carries exactly one
// u32 per key, in key order, which is what makes an element's WIDTH predictable (8 words + one per key) and what
// names the words of the RDB feat/spell/item modifier record (#591).
//
// This supersedes the live-Frida keyList dump that produced the 68-entry docs/re/gameaction_types.json: static
// recovery reaches all 260 registered types, and it disagrees with that file on a few (AddProc has SIX keys, not
// four) - the RDB record widths corroborate the static reading.
//
// The descriptor is 0xF4 bytes; the initialiser writes it in two forms, both handled here:
//   ABSOLUTE  the address is constant-folded:  MOV [base+8], <enumId> / MOV [base+0], <name ptr>
//   INDEXED   through a running cursor:        PUSH <name>; MOV ECX,<enumId>; CALL FUN_008bde20
//                                              MOV [ECX + EAX*4 + 0xc + 4j], <key>
//   base+0x00  char*  name          "e_Type_GC_GameAction_<Name>"
//   base+0x04  char*  label         short name, mostly null
//   base+0x08  u32    enumId        0xCF09..0xD026
//   base+0x0c  u32[]  keyList       key INDEX per slot, terminated by 0xFF
//   base+0x5c  char*[] fieldNames   parallel to keyList; named on ~20 types, and those names transfer BY KEY
//   base+0xac  u32[]  (unused, zero throughout)
//
//   FAOLAN_GHIDRA_OUT=<dir> /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpGameActionKeyLists.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.*;

public class DumpGameActionKeyLists extends GhidraScript {
	static final String OUT = (System.getenv("FAOLAN_GHIDRA_OUT") != null
			? System.getenv("FAOLAN_GHIDRA_OUT") : System.getProperty("java.io.tmpdir")) + "/";

	// The initialiser. Everything below is read out of this one function.
	static final long INIT_FN = 0x008bdf40L;
	// The per-slot key list starts here in the descriptor, one u32 per slot.
	static final int KEYS = 0x0c;
	// The parallel field-name array, same slot stride.
	static final int NAMES = 0x5c;
	// The key list ends at this sentinel rather than carrying a count.
	static final long TERMINATOR = 0xffL;
	// The id space the descriptor ARRAY spans, straight out of the lookup `FUN_008c3910`:
	// `descriptor = &DAT_025077c8 + (id - 0xCF09) * 0x41` undefined4s (= 0x104 bytes), guarded by
	// `(id - 0xCF09) <= 0x11D`. So the array has 0x11E = 286 slots, and an id in range whose slot the
	// initialiser never writes is a HOLE - a retired type, not a type this script missed. See the
	// `unregistered` line the run prints (Gitea #596).
	static final long FIRST_ID = 0xcf09L;
	static final long LAST_ID = 0xd026L;

	String strAt(long a) {
		if (a < 0x400000L) return null;
		try {
			Data d = getDataAt(toAddr(a));
			if (d != null && d.hasStringValue()) return (String) d.getValue();
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < 80; i++) {
				byte b = getByte(toAddr(a).add(i));
				if (b == 0) break;
				if (b < 32 || b > 126) return null;
				sb.append((char) b);
			}
			return sb.length() >= 4 ? sb.toString() : null;
		} catch (Exception e) {
			return null;
		}
	}

	// One store the initialiser makes: either to an absolute address or to <cursor> + off.
	static final class Store {
		final boolean indexed;
		final long addr;      // absolute address, or the descriptor offset when indexed
		final Long value;     // null when the source register's constant was not tracked
		final String text;    // the string the value points at, when it is one

		Store(boolean indexed, long addr, Long value, String text) {
			this.indexed = indexed; this.addr = addr; this.value = value; this.text = text;
		}
	}

	static final class Desc {
		long enumId;
		String name = "";
		String label = "";
		final List<Long> keys = new ArrayList<>();
		final List<String> fields = new ArrayList<>();
		String form;
	}

	public void run() throws Exception {
		Function fn = getFunctionAt(toAddr(INIT_FN));
		if (fn == null) { println("no function at " + Long.toHexString(INIT_FN)); return; }

		// Pass 1 - walk the function linearly, tracking constant registers, and record every store plus every
		// CALL-form registration in order.
		Map<String, Long> regs = new HashMap<>();
		List<Object[]> events = new ArrayList<>();   // {"ABS"|"IDX"|"REG"|"ADD", payload...}
		String pendingName = null;
		InstructionIterator it = currentProgram.getListing().getInstructions(fn.getBody(), true);
		while (it.hasNext()) {
			Instruction in = it.next();
			String mn = in.getMnemonicString();
			if (mn.equals("PUSH")) {
				Object[] o = in.getOpObjects(0);
				if (o.length == 1 && o[0] instanceof Scalar) {
					String s = strAt(((Scalar) o[0]).getUnsignedValue());
					if (s != null && s.startsWith("e_Type_GC_GameAction")) pendingName = s;
				}
				continue;
			}
			if (mn.equals("CALL")) {
				if (pendingName != null) { events.add(new Object[] { "REG", regs.get("ECX"), pendingName }); pendingName = null; }
				regs.remove("EAX"); regs.remove("ECX"); regs.remove("EDX");
				continue;
			}
			if (mn.equals("XOR") && in.getNumOperands() == 2) {
				Object[] a = in.getOpObjects(0), b = in.getOpObjects(1);
				if (a.length == 1 && b.length == 1 && a[0] instanceof Register && b[0] instanceof Register
						&& ((Register) a[0]).getName().equals(((Register) b[0]).getName())) {
					regs.put(((Register) a[0]).getName(), 0L);
					continue;
				}
			}
			if (mn.equals("MOV") && in.getNumOperands() == 2) {
				Object[] dst = in.getOpObjects(0), src = in.getOpObjects(1);
				if (dst.length == 1 && dst[0] instanceof Register) {
					String r = ((Register) dst[0]).getName();
					if (src.length == 1 && src[0] instanceof Scalar) regs.put(r, ((Scalar) src[0]).getUnsignedValue() & 0xFFFFFFFFL);
					else if (src.length == 1 && src[0] instanceof Register && regs.containsKey(((Register) src[0]).getName()))
						regs.put(r, regs.get(((Register) src[0]).getName()));
					else regs.remove(r);
					continue;
				}
				Long v = null;
				if (src.length == 1 && src[0] instanceof Scalar) v = ((Scalar) src[0]).getUnsignedValue() & 0xFFFFFFFFL;
				else if (src.length == 1 && src[0] instanceof Register) v = regs.get(((Register) src[0]).getName());
				String text = v != null ? strAt(v) : null;
				// MOV [abs], v
				if (dst.length == 1 && (dst[0] instanceof Scalar || dst[0] instanceof Address)) {
					long a = dst[0] instanceof Scalar ? (((Scalar) dst[0]).getUnsignedValue() & 0xFFFFFFFFL) : ((Address) dst[0]).getOffset();
					events.add(new Object[] { "ABS", new Store(false, a, v, text) });
					continue;
				}
				// MOV [ECX + EAX*4 + off], v  - the cursor form; the displacement IS the descriptor offset
				String rep = in.toString();
				int plus = rep.indexOf("EAX*0x4 + 0x");
				if (rep.contains("[ECX + EAX*0x4") && plus > 0) {
					int end = rep.indexOf(']', plus);
					long off = Long.parseLong(rep.substring(plus + "EAX*0x4 + 0x".length(), end), 16);
					events.add(new Object[] { "IDX", new Store(true, off, v, text) });
				}
				continue;
			}
			if (mn.equals("ADD") && in.getNumOperands() == 2) {
				Object[] d = in.getOpObjects(0);
				if (d.length == 1 && d[0] instanceof Register && ((Register) d[0]).getName().equals("EAX")) {
					events.add(new Object[] { "ADD" });
					regs.remove("EAX");
					continue;
				}
			}
			if (mn.equals("TEST") || mn.equals("CMP") || mn.equals("RET") || mn.equals("NOP") || mn.startsWith("J")) continue;
			for (Object o : in.getResultObjects()) if (o instanceof Register) regs.remove(((Register) o).getName());
		}

		// Pass 2 - the absolute form: an in-range id whose descriptor base carries an e_Type_ name.
		Map<Long, Store> abs = new HashMap<>();
		List<Store> absOrder = new ArrayList<>();
		for (Object[] e : events)
			if (e[0].equals("ABS")) { Store s = (Store) e[1]; abs.put(s.addr, s); absOrder.add(s); }
		TreeMap<Long, Desc> out = new TreeMap<>();
		for (Store s : absOrder) {
			if (s.value == null || s.value < 0xcf09L || s.value > 0xd026L) continue;
			long base = s.addr - 8;
			Store nm = abs.get(base);
			if (nm == null || nm.text == null || !nm.text.startsWith("e_Type_GC_GameAction")) continue;
			Desc d = new Desc();
			d.enumId = s.value; d.name = nm.text; d.form = "abs";
			Store lab = abs.get(base + 4);
			d.label = lab != null && lab.text != null ? lab.text : "";
			for (int j = 0; j < 24; j++) {
				Store k = abs.get(base + KEYS + 4L * j);
				if (k == null || k.value == null || k.value == TERMINATOR) break;
				d.keys.add(k.value);
				Store f = abs.get(base + NAMES + 4L * j);
				d.fields.add(f != null && f.text != null ? f.text : "");
			}
			out.put(d.enumId, d);
		}

		// Pass 3 - the indexed form: the stores after a CALL registration, up to the cursor advance.
		for (int i = 0; i < events.size(); i++) {
			if (!events.get(i)[0].equals("REG")) continue;
			Long id = (Long) events.get(i)[1];
			if (id == null) continue;
			Map<Long, Store> slots = new HashMap<>();
			int j = i + 1;
			for (; j < events.size() && !events.get(j)[0].equals("REG") && !events.get(j)[0].equals("ADD"); j++)
				if (events.get(j)[0].equals("IDX")) { Store s = (Store) events.get(j)[1]; slots.putIfAbsent(s.addr, s); }
			if (out.containsKey(id)) continue;
			Desc d = new Desc();
			d.enumId = id; d.name = (String) events.get(i)[2]; d.form = "reg";
			for (int k = 0; k < 24; k++) {
				Store s = slots.get((long) (KEYS + 4 * k));
				if (s == null || s.value == null || s.value == TERMINATOR) break;
				d.keys.add(s.value);
				Store f = slots.get((long) (NAMES + 4 * k));
				d.fields.add(f != null && f.text != null ? f.text : "");
			}
			out.put(d.enumId, d);
		}

		// A field name is a property of the KEY, not of the type that happens to name it - so a name learned on one
		// type resolves that key everywhere. ONLY where the key is named consistently: key 39 is "Value" on a
		// modifier and "ScriptID" on a script action, so it carries no transferable name and stays blank rather
		// than borrowing whichever type was registered first.
		Map<Long, Set<String>> seen = new TreeMap<>();
		for (Desc d : out.values())
			for (int i = 0; i < d.keys.size(); i++)
				if (!d.fields.get(i).isEmpty()) seen.computeIfAbsent(d.keys.get(i), k -> new TreeSet<>()).add(d.fields.get(i));
		Map<Long, String> keyNames = new TreeMap<>();
		for (Map.Entry<Long, Set<String>> e : seen.entrySet())
			if (e.getValue().size() == 1) keyNames.put(e.getKey(), e.getValue().iterator().next());

		try (PrintWriter w = new PrintWriter(OUT + "gameaction-keylists.tsv")) {
			w.println("typeId\ttypeIdHex\tname\tlabel\twidthWords\tkeys\tfields\tform");
			for (Desc d : out.values()) {
				StringBuilder keys = new StringBuilder(), fields = new StringBuilder();
				for (int i = 0; i < d.keys.size(); i++) {
					if (i > 0) { keys.append(';'); fields.append(';'); }
					keys.append(d.keys.get(i));
					String f = d.fields.get(i).isEmpty() ? keyNames.getOrDefault(d.keys.get(i), "") : d.fields.get(i);
					fields.append(f);
				}
				w.println(d.enumId + "\t0x" + Long.toHexString(d.enumId).toUpperCase() + "\t" + d.name + "\t" + d.label
						+ "\t" + (8 + d.keys.size()) + "\t" + keys + "\t" + fields + "\t" + d.form);
			}
		}
		// The HOLES. The array is indexed by id, so "in range but never written" is a fact about the client, not a
		// gap in this walk - and it is the only thing that can tell a retired type apart from one the walk missed.
		// Corroborate it outside the initialiser before believing it: a registered type owns an
		// `e_Type_GC_GameAction_<Name>` string, so the image holding exactly as many of those as there are
		// descriptors says every named type was reached. Mirrored in `GameActionTypes.UnregisteredIds`.
		StringBuilder holes = new StringBuilder();
		for (long id = FIRST_ID; id <= LAST_ID; id++)
			if (!out.containsKey(id)) holes.append(holes.length() == 0 ? "" : ", ").append(id);
		int named = 0;
		for (Data d : currentProgram.getListing().getDefinedData(true))
			if (d.hasStringValue() && String.valueOf(d.getValue()).startsWith("e_Type_GC_GameAction_")
					&& String.valueOf(d.getValue()).length() > "e_Type_GC_GameAction_".length()) named++;

		println("descriptors: " + out.size() + " of " + (LAST_ID - FIRST_ID + 1) + " slots"
				+ "  named keys: " + keyNames.size() + "  e_Type_GC_GameAction_* strings in the image: " + named);
		println("unregistered: " + holes);
		println("wrote " + OUT + "gameaction-keylists.tsv");
	}
}
