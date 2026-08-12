// For each call to a UniversalConfig getter, recover the KEY STRING the client built just before it.
// The pattern is  FUN_0040EC10(<key literal>, <len>)  then  <getter>(<default>) - see FUN_00A7EBE0.
//   TARGETS=a7e1f0,... /opt/ghidra/support/analyzeHeadless <proj> aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath <dir> -postScript DumpConfigKeys.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.mem.MemoryAccessException;
import java.io.PrintWriter;
import java.util.*;

public class DumpConfigKeys extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    String stringAt(Address a) {
        if (a == null) return null;
        StringBuilder sb = new StringBuilder();
        try {
            for (int i = 0; i < 96; i++) {
                byte b = getByte(a.add(i));
                if (b == 0) break;
                if (b < 0x20 || b > 0x7e) return null;
                sb.append((char) b);
            }
        } catch (Exception e) { return null; }
        return sb.length() >= 3 ? sb.toString() : null;
    }

    public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "config_keys.txt");
        ReferenceManager rm = currentProgram.getReferenceManager();
        Listing lst = currentProgram.getListing();
        var seen = new TreeMap<String, List<String>>();
        int sites = 0, named = 0;
        for (String h : System.getenv("TARGETS").split(",")) {
            Address t = toAddr(Long.parseLong(h.trim(), 16));
            Function tf = getFunctionContaining(t);
            for (Reference r : rm.getReferencesTo(tf != null ? tf.getEntryPoint() : t)) {
                if (!r.getReferenceType().isCall()) continue;
                sites++;
                Instruction ins = lst.getInstructionAt(r.getFromAddress());
                String key = null;
                for (int i = 0; i < 30 && ins != null && key == null; i++) {
                    ins = ins.getPrevious();
                    if (ins == null) break;
                    for (int op = 0; op < ins.getNumOperands() && key == null; op++) {
                        for (Object o : ins.getOpObjects(op)) {
                            if (o instanceof Scalar s) {
                                String v = stringAt(toAddr(s.getUnsignedValue()));
                                if (v != null && v.matches("[A-Za-z][A-Za-z0-9_]{2,}")) { key = v; break; }
                            }
                        }
                    }
                }
                if (key == null) continue;
                named++;
                Function f = getFunctionContaining(r.getFromAddress());
                seen.computeIfAbsent(key, k -> new ArrayList<>())
                    .add(r.getFromAddress() + (f == null ? "" : " " + f.getName()));
            }
        }
        out.println("call sites=" + sites + "  keys recovered=" + named + "  distinct=" + seen.size());
        for (var e : seen.entrySet()) out.println(e.getKey() + "\t" + e.getValue().size() + "\t" + e.getValue().get(0));
        out.close();
        println("wrote " + OUT + "config_keys.txt (" + seen.size() + " distinct)");
    }
}
