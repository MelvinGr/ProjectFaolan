// #658: find READERS of a pointer field - instructions `MOV Rd, [Rs + OFF]` followed (within the same
// function, before Rd is clobbered, WINDOW instructions max) by a memory access based at Rd with a small
// displacement (<= MAXDISP). That is the double-indirection signature of code consuming a pointed-at struct,
// e.g. the 7-word buff descriptor CoreMagicHolder::UpdateBuff stores raw at BuffInfo_c+0x24.
//   OFF=24 WINDOW=8 MAXDISP=18 analyzeHeadless ... -postScript FindLoadDeref.java
// Output: $FAOLAN_GHIDRA_OUT/load_deref.txt, grouped per function.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;
import java.util.*;

public class FindLoadDeref extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String needle = "0x" + System.getenv().getOrDefault("OFF", "24").toLowerCase() + "]";
        int window = Integer.parseInt(System.getenv().getOrDefault("WINDOW", "8"));
        long maxDisp = Long.parseLong(System.getenv().getOrDefault("MAXDISP", "18"), 16);

        Map<Function, List<String>> hits = new LinkedHashMap<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            if (!ins.getMnemonicString().equals("MOV") || ins.getNumOperands() != 2)
                continue;
            String src = ins.getDefaultOperandRepresentation(1);
            if (!src.contains(needle) || !src.contains("["))
                continue;
            String dst = ins.getDefaultOperandRepresentation(0);
            if (dst.contains("[")) // store, not load
                continue;
            Function f = getFunctionContaining(ins.getAddress());
            if (f == null)
                continue;
            // walk forward: look for [dst] or [dst + small]
            Instruction cur = ins;
            for (int i = 0; i < window; i++) {
                cur = cur.getNext();
                if (cur == null || getFunctionContaining(cur.getAddress()) != f)
                    break;
                boolean clobbered = false;
                for (int op = 0; op < cur.getNumOperands(); op++) {
                    String s = cur.getDefaultOperandRepresentation(op);
                    if (!s.contains("[") && op == 0 && s.equals(dst) && cur.getMnemonicString().startsWith("MOV")) {
                        clobbered = true;
                        break;
                    }
                    if (s.contains("[" + dst + "]") || s.contains("[" + dst + " +")) {
                        // check displacement
                        long disp = 0;
                        int plus = s.indexOf(dst + " + 0x");
                        if (plus >= 0) {
                            String rest = s.substring(plus + dst.length() + 5);
                            rest = rest.replaceAll("[^0-9a-fA-F].*", "");
                            try { disp = Long.parseLong(rest, 16); } catch (Exception e) { disp = 0x10000; }
                        } else if (s.contains("[" + dst + " +")) {
                            disp = 0x10000; // register-indexed, skip
                        }
                        if (disp <= maxDisp) {
                            hits.computeIfAbsent(f, k -> new ArrayList<>()).add(
                                ins.getAddress() + "  " + ins + "   ->  " + cur.getAddress() + "  " + cur);
                        }
                    }
                }
                if (clobbered)
                    break;
                if (cur.getMnemonicString().startsWith("CALL") || cur.getMnemonicString().startsWith("RET"))
                    break;
            }
        }

        try (PrintWriter w = new PrintWriter(OUT + "load_deref.txt")) {
            w.println("== load [reg+" + needle + " then deref, " + hits.size() + " functions ==");
            for (var e : hits.entrySet()) {
                w.println("\n" + e.getKey().getName() + " @" + e.getKey().getEntryPoint() + "  (" + e.getValue().size() + ")");
                for (String s : e.getValue())
                    w.println("   " + s);
            }
        }
        println("functions: " + hits.size() + " -> " + OUT + "load_deref.txt");
    }
}
