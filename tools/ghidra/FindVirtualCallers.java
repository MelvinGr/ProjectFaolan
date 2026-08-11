// SLOT=0x24 [MINCOUNT=1]: every function that CALLS a virtual at that vtable offset - `MOV E?X,[reg + SLOT];
// CALL E?X` or `CALL dword ptr [reg + SLOT]`. The reverse of a vtable dump: given a slot whose meaning is known
// (`slot 9 = Apply` on the GameAction executors, #709), this finds the code that INVOKES it, which is what says
// when the behaviour actually runs. Writes $FAOLAN_GHIDRA_OUT/virtual_callers.txt.
//   SLOT=0x24 analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript FindVirtualCallers.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;
import java.util.*;

public class FindVirtualCallers extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override public void run() throws Exception {
        String slot = System.getenv().getOrDefault("SLOT", "0x24").trim();
        int min = Integer.parseInt(System.getenv().getOrDefault("MINCOUNT", "1").trim());
        // Both spellings of the same call: through a register loaded from the vtable, or straight off memory.
        String load = "ptr [" , disp = " + " + slot + "]";
        Map<String, List<String>> hits = new LinkedHashMap<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        String[] pending = new String[8];
        while (it.hasNext()) {
            Instruction in = it.next();
            String s = in.toString();
            boolean direct = s.startsWith("CALL dword ptr [") && s.endsWith(disp);
            boolean viaReg = false;
            if (s.startsWith("MOV ") && s.contains(load) && s.endsWith(disp)) {
                // remember the destination register; the CALL usually follows within a few instructions
                String dst = s.substring(4, s.indexOf(','));
                Instruction n = in;
                for (int i = 0; i < 6 && n != null; i++) {
                    n = n.getNext();
                    if (n == null) break;
                    if (n.toString().equals("CALL " + dst)) { viaReg = true; break; }
                }
            }
            if (!direct && !viaReg) continue;
            Function f = getFunctionContaining(in.getAddress());
            String key = f == null ? "<none>" : f.getName() + "@" + f.getEntryPoint();
            hits.computeIfAbsent(key, k -> new ArrayList<>()).add(in.getAddress() + ":  " + s);
        }
        PrintWriter out = new PrintWriter(OUT + "virtual_callers.txt");
        out.println("######## callers of virtual slot " + slot + " (" + hits.size() + " functions)");
        for (var e : hits.entrySet()) {
            if (e.getValue().size() < min) continue;
            out.println(e.getKey() + "  x" + e.getValue().size());
            for (String l : e.getValue()) out.println("    " + l);
        }
        out.flush(); out.close();
        println("WROTE " + OUT + "virtual_callers.txt (" + hits.size() + " functions)");
        if (pending.length == 0) println("");
    }
}
