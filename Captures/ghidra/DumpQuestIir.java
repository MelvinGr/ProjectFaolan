// Decompile the QuestFullUpdateIIR (and QuestJournalIIR) Read/Write SubClass methods to recover the on-wire
// field layout the captures can't pin (the per-quest "stat" fields + objective records). These methods
// reference unique error strings (e.g. "QuestFullUpdateIIR::ReadSubClass() Wrong IIR_t version"), so we find
// the string, find the function that references its address, and decompile it. Run:
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpQuestIir.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.io.PrintWriter;
import java.util.*;

public class DumpQuestIir extends GhidraScript {
    // Unique strings inside the methods whose field layout we want.
    static final String[] ANCHORS = {
        "QuestFullUpdateIIR::ReadSubClass",
        "iir\\questfullupdateiir.cpp",
        "QuestFullUpdateIIR::Activate",
        "QuestJournalIIR_c",
    };

    Address findStr(String s) throws Exception {
        return currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), s.getBytes("US-ASCII"), null, true, monitor);
    }

    @Override
    public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/quest_iir.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);
        ReferenceManager rm = currentProgram.getReferenceManager();
        Set<Long> dumped = new HashSet<>();

        for (String anchor : ANCHORS) {
            Address s = findStr(anchor);
            out.println("################## anchor \"" + anchor + "\" -> " + s);
            if (s == null) continue;
            // Find code references to this string's address.
            int g = 0;
            for (Reference r : rm.getReferencesTo(s)) {
                Function f = getFunctionContaining(r.getFromAddress());
                if (f == null || !dumped.add(f.getEntryPoint().getOffset())) continue;
                out.println("==== ref @" + r.getFromAddress() + " in " + f.getName() + " @" + f.getEntryPoint()
                            + " size=" + f.getBody().getNumAddresses() + " ====");
                try {
                    DecompileResults res = di.decompileFunction(f, 180, monitor);
                    out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
                } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
                out.println();
                if (++g > 6) break;
            }
        }
        out.close();
        println("DumpQuestIir done -> /home/melvin/Development/Ghidra/Aoc/quest_iir.txt");
    }
}
