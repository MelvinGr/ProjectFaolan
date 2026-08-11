// OFF=14 BIT=3 [WINDOW=14]: every site that loads the dword at `[reg + OFF]` and then SHIFTS it to select one bit
// - `SHR reg,BIT` (often followed by `AND reg,1` / `NOT`) - which is how this client writes a bit test.
// ⚠️ The `AND`/`TEST` mask form is the OTHER spelling and is what DumpQuestLineFlagBits.java scans for; a compiler
// picks whichever suits, so a bit that comes up empty in one scan has to be run through the other before "nothing
// tests it" means anything. The Quest_c `@+0x14` flags are the case in point: bits 4 and 10 are read as
// `~(flags >> 4) & 1` / `~(flags >> 10) & 1`, which no mask scan sees (Gitea #570).
//
//   OFF=14 BIT=3 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath tools/ghidra -postScript FindBitShiftAtOffset.java
// Output: $FAOLAN_GHIDRA_OUT/bit_shift_sites.txt, one block per site with the load, the shift and what follows.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.*;

public class FindBitShiftAtOffset extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override public void run() throws Exception {
        String off = "0x" + System.getenv().getOrDefault("OFF", "14").toLowerCase();
        long bit = Long.parseLong(System.getenv().getOrDefault("BIT", "3").trim());
        int window = Integer.parseInt(System.getenv().getOrDefault("WINDOW", "14").trim());
        PrintWriter out = new PrintWriter(OUT + "bit_shift_sites.txt");
        out.println("sites loading [reg + " + off + "] then shifting right by " + bit + " (bit " + bit + " select)");
        int hits = 0;

        for (Function fn : currentProgram.getFunctionManager().getFunctions(true)) {
            if (monitor.isCancelled()) break;
            InstructionIterator it = currentProgram.getListing().getInstructions(fn.getBody(), true);
            // register -> the instruction that last loaded the field into it, and how many instructions ago
            Map<String, Instruction> loaded = new HashMap<>();
            Map<String, Integer> age = new HashMap<>();
            while (it.hasNext()) {
                Instruction ins = it.next();
                for (var e : age.entrySet()) e.setValue(e.getValue() + 1);
                age.entrySet().removeIf(e -> e.getValue() > window);
                loaded.keySet().retainAll(age.keySet());

                String text = ins.toString();
                if (text.startsWith("MOV ") && text.contains("[") && (text.endsWith("+ " + off + "]"))) {
                    String dst = text.substring(4, text.indexOf(','));
                    loaded.put(dst, ins);
                    age.put(dst, 0);
                    continue;
                }

                if (!(text.startsWith("SHR ") || text.startsWith("SAR "))) continue;
                int comma = text.indexOf(',');
                if (comma < 0) continue;
                String reg = text.substring(4, comma);
                Scalar amount = null;
                for (Object o : ins.getOpObjects(1)) if (o instanceof Scalar s) amount = s;
                if (amount == null || amount.getUnsignedValue() != bit) continue;
                // the shifted register, or its 8/16-bit alias, must hold the field
                Instruction load = loaded.get(reg);
                if (load == null) load = loaded.get("E" + reg.substring(0, Math.max(1, reg.length() - 1)) + "X");
                if (load == null) {
                    for (var e : loaded.entrySet())
                        if (e.getKey().length() == 3 && e.getKey().substring(1, 2).equals(reg.substring(0, 1))) load = e.getValue();
                }
                if (load == null) continue;

                hits++;
                out.println();
                out.println(fn.getName() + " @" + fn.getEntryPoint());
                out.println("    load  " + load.getAddress() + ":  " + load);
                out.println("    shift " + ins.getAddress() + ":  " + ins);
                Instruction n = ins;
                for (int i = 0; i < 4 && n != null; i++) {
                    n = n.getNext();
                    if (n != null) out.println("          " + n.getAddress() + ":  " + n);
                }
            }
        }
        out.flush(); out.close();
        println("WROTE " + OUT + "bit_shift_sites.txt (" + hits + " site(s))");
    }
}
