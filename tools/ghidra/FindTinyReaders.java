// OFFS=6c,50 MAXINS=12 : every SMALL function (at most MAXINS instructions) that reads [reg + off] for any listed
// offset - the accessor scan. A field whose consumer an offset census cannot find is often read through a one-line
// getter (`return this->m_flag`), and the getter's CALLERS are then the consumers a co-offset scan on the struct
// never sees, because they touch the field through a call rather than a displacement. Written for #929.
//
//   OFFS=6c,50 MAXINS=12 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript FindTinyReaders.java
//
// Output: $FAOLAN_GHIDRA_OUT/tiny_readers.txt - entry, size, the instruction, and how many call sites reference it.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class FindTinyReaders extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        List<String> needles = new ArrayList<>();
        for (String p : System.getenv("OFFS").split(","))
            needles.add("0x" + p.trim().toLowerCase() + "]");
        int maxIns = Integer.parseInt(System.getenv().getOrDefault("MAXINS", "12"));

        PrintWriter out = new PrintWriter(OUT + "tiny_readers.txt");
        int matched = 0;
        FunctionIterator fns = currentProgram.getFunctionManager().getFunctions(true);
        while (fns.hasNext() && !monitor.isCancelled()) {
            Function f = fns.next();
            List<Instruction> body = new ArrayList<>();
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                body.add(it.next());
                if (body.size() > maxIns) break;
            }
            if (body.size() > maxIns) continue;

            for (Instruction ins : body) {
                boolean hit = false;
                for (int op = 1; op < ins.getNumOperands() && !hit; op++) {
                    String s = ins.getDefaultOperandRepresentation(op);
                    if (!s.contains("[")) continue;
                    for (String n : needles) if (s.contains(n)) hit = true;
                }
                if (!hit) continue;

                int callers = 0;
                ReferenceIterator ri = currentProgram.getReferenceManager().getReferencesTo(f.getEntryPoint());
                while (ri.hasNext()) { Reference r = ri.next(); if (r.getReferenceType().isCall()) callers++; }
                out.printf("%s  %-16s  %2d ins  %-44s  callers=%d%n", f.getEntryPoint(), f.getName(), body.size(), ins.getAddress() + " " + ins, callers);
                matched++;
                break;
            }
        }
        out.close();
        println("FindTinyReaders: " + matched + " small function(s) read one of " + needles + " -> " + OUT + "tiny_readers.txt");
    }
}
