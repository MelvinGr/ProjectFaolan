// OFFS=24,38 : every function whose instructions reference a [reg + off] displacement for EACH listed byte offset
// (co-occurrence, not either-alone), printed with the hit addresses. Written for #464's quest-goal consumer hunt:
// a single struct offset like +0x38 is far too common to filter on (778 functions touch +0xf0), but the marker
// consumer reads markerX/markerY (+0x24/+0x28) AND unknown1/unknown2 (+0x38/+0x3c) on the SAME goal object, so the
// intersection is specific. Optionally NEAR=<n> requires the two hits within n instructions of each other.
//
//   OFFS=24,38 /opt/ghidra/support/analyzeHeadless <ghidra-proj> aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpCoOffsetReaders.java
//
// Output: $FAOLAN_GHIDRA_OUT/co_offset_readers.txt  (grep "DumpCoOffsetReaders.java>" for stdout, or read the file)
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.*;

public class DumpCoOffsetReaders extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        List<Long> wanted = new ArrayList<>();
        for (String p : System.getenv("OFFS").split(","))
            wanted.add(Long.parseLong(p.trim(), 16));
        int near = System.getenv("NEAR") != null ? Integer.parseInt(System.getenv("NEAR")) : 0;

        PrintWriter out = new PrintWriter(new java.io.File(OUT, "co_offset_readers.txt"));
        int fnCount = 0, hitCount = 0;

        FunctionIterator fns = currentProgram.getFunctionManager().getFunctions(true);
        while (fns.hasNext() && !monitor.isCancelled()) {
            Function f = fns.next();
            fnCount++;
            // per offset: the instruction indices (within the function body) where it appears
            Map<Long, List<Integer>> where = new HashMap<>();
            for (Long o : wanted) where.put(o, new ArrayList<Integer>());

            int idx = 0;
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            List<Long> idxAddr = new ArrayList<>();
            while (it.hasNext()) {
                Instruction insn = it.next();
                idxAddr.add(insn.getAddress().getOffset());
                for (int op = 0; op < insn.getNumOperands(); op++) {
                    for (Object obj : insn.getOpObjects(op)) {
                        if (obj instanceof Scalar) {
                            long v = ((Scalar) obj).getUnsignedValue();
                            if (where.containsKey(v)) where.get(v).add(idx);
                        }
                    }
                }
                idx++;
            }

            boolean all = true;
            for (Long o : wanted) if (where.get(o).isEmpty()) { all = false; break; }
            if (!all) continue;

            // Optional proximity gate: the first offset's hit must sit within `near` instructions of some hit of
            // every other offset (guards against a huge function that touches both offsets far apart by chance).
            if (near > 0) {
                boolean close = false;
                for (int a : where.get(wanted.get(0))) {
                    boolean allClose = true;
                    for (int k = 1; k < wanted.size(); k++) {
                        boolean any = false;
                        for (int b : where.get(wanted.get(k))) if (Math.abs(a - b) <= near) { any = true; break; }
                        if (!any) { allClose = false; break; }
                    }
                    if (allClose) { close = true; break; }
                }
                if (!close) continue;
            }

            hitCount++;
            StringBuilder sb = new StringBuilder();
            sb.append(f.getName()).append("@").append(f.getEntryPoint());
            for (Long o : wanted) sb.append("  +0x").append(Long.toHexString(o)).append("x").append(where.get(o).size());
            out.println(sb.toString());
            println(sb.toString());
        }
        out.printf("scanned %d functions, %d matched%n", fnCount, hitCount);
        println("scanned " + fnCount + " functions, " + hitCount + " matched");
        out.close();
    }
}
