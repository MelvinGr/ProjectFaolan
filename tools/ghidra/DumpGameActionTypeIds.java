// Full GameAction catalog. FUN_008bdf40 initialises a descriptor table at 0x025077C8, stride 0x41:
//   descriptor+0 = name pointer, descriptor+8 = enum id.
// The descriptor INDEX yields the wire type id: typeId = 0xCF09 + index.
// Emit every registration with both ids so the 68-entry json can be cross-checked and extended.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class DumpGameActionTypeIds extends GhidraScript {
	static final String OUT = (System.getenv("FAOLAN_GHIDRA_OUT") != null
			? System.getenv("FAOLAN_GHIDRA_OUT") : System.getProperty("java.io.tmpdir")) + "/";

	static final long TABLE = 0x025077C8L;
	static final int STRIDE = 0x41;
	static final int FIRST = 0xCF09;

	String strAt(Address a) {
		Data d = getDataAt(a);
		if (d != null && d.getValue() instanceof String) return (String) d.getValue();
		return null;
	}

	public void run() throws Exception {
		PrintWriter w = new PrintWriter(new FileWriter(OUT + "ga_full.tsv"));
		w.println("name\tdescAddr\tindex\ttypeId\ttypeIdHex\tenumId\tenumIdHex\taligned");

		Function f = getFunctionAt(toAddr(0x008bdf40L));
		InstructionIterator ii = currentProgram.getListing().getInstructions(f.getBody(), true);

		// Pass 1: collect "MOV [abs], <ptr-to-e_Type-string>" -> descriptor base address
		// Pass 2: for each, the enum id is the immediate written at descBase+8 (search the whole fn).
		Map<Long, String> nameAt = new LinkedHashMap<>();   // descBase -> name
		Map<Long, Long> immAt = new HashMap<>();            // absolute addr -> immediate written

		List<Instruction> all = new ArrayList<>();
		while (ii.hasNext()) all.add(ii.next());

		for (Instruction in : all) {
			if (!in.getMnemonicString().toUpperCase().startsWith("MOV")) continue;
			// destination operand 0 must be an absolute memory address
			Long dst = null;
			for (Object o : in.getOpObjects(0)) {
				if (o instanceof Scalar) dst = ((Scalar) o).getUnsignedValue();
				if (o instanceof Address) dst = ((Address) o).getOffset();
			}
			if (dst == null) continue;

			// source: immediate, possibly a string pointer
			Long src = null;
			if (in.getNumOperands() > 1) {
				for (Object o : in.getOpObjects(1)) {
					if (o instanceof Scalar) src = ((Scalar) o).getUnsignedValue();
					if (o instanceof Address) src = ((Address) o).getOffset();
				}
			}
			if (src == null) continue;
			immAt.put(dst, src);

			String s = null;
			try { s = strAt(toAddr(src)); } catch (Exception e) { }
			if (s != null && s.startsWith("e_Type_")) nameAt.put(dst, s);
		}

		for (Map.Entry<Long, String> e : nameAt.entrySet()) {
			long base = e.getKey();
			long delta = base - TABLE;
			boolean aligned = delta >= 0 && delta % STRIDE == 0;
			long index = aligned ? delta / STRIDE : -1;
			long typeId = aligned ? FIRST + index : -1;
			Long enumId = immAt.get(base + 8);
			w.println(e.getValue() + "\t0x" + Long.toHexString(base) + "\t" + index + "\t" + typeId + "\t"
					+ (typeId < 0 ? "-" : "0x" + Long.toHexString(typeId)) + "\t"
					+ (enumId == null ? "-" : enumId.toString()) + "\t"
					+ (enumId == null ? "-" : "0x" + Long.toHexString(enumId)) + "\t" + aligned);
		}
		w.close();
		println("wrote " + OUT + "ga_full.tsv  entries=" + nameAt.size());
	}
}
