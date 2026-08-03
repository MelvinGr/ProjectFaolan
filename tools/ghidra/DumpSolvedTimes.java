// Pin QuestGoal_c's SolvedTimes accessor and the goal-record READER that feeds it - #550, the reserved-block
// lead word (`QuestGoalDefinition.Extra0`). The client's string pool names `QuestGoal_c::SetSolvedTimes()`, and
// the assert string sits INSIDE the setter, so the function referencing it is the setter itself. From there the
// callers say which stream field lands in the goal's progress counter.
//
// Also anchors the neighbouring goal API (`SetParent`, `GetQuestLevel`) so the goal object's layout can be read
// off the accessors rather than guessed from the reader.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.mem.MemoryBlock;
import java.io.PrintWriter;
import java.util.*;

public class DumpSolvedTimes extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final String[] NEEDLES = { "SetSolvedTimes", "SolvedTimes", "SetParent()", "GetQuestLevel", "UpdateCurrentProgress" };

    PrintWriter out;
    DecompInterface di;

    void decomp(Function f, String why) {
        if (f == null) { out.println("  <no fn> " + why); return; }
        out.println("---- " + why + ": " + f.getName() + " @" + f.getEntryPoint()
            + " size=" + f.getBody().getNumAddresses() + " ----");
        try {
            DecompileResults res = di.decompileFunction(f, 200, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
        } catch (Exception ex) { out.println("  <exception " + ex + ">"); }
    }

    @Override public void run() throws Exception {
        out = new PrintWriter(OUT + "solved_times.txt");
        di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        // Every defined string carrying one of the needles, and every function referencing it.
        Set<Function> setters = new LinkedHashSet<>();
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            if (!b.isInitialized()) continue;
            DataIterator it = currentProgram.getListing().getDefinedData(b.getStart(), true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Data d = it.next();
                if (d.getMinAddress().compareTo(b.getEnd()) > 0) break;
                Object v = d.getValue();
                if (!(v instanceof String s)) continue;
                boolean hit = false;
                for (String n : NEEDLES) if (s.contains(n)) { hit = true; break; }
                if (!hit) continue;

                out.println("##### STRING @" + d.getMinAddress() + " : " + s.replace("\n", "\\n"));
                for (Reference r : currentProgram.getReferenceManager().getReferencesTo(d.getMinAddress())) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    out.println("   <- " + r.getFromAddress() + " in " + (f == null ? "?" : f.getName() + "@" + f.getEntryPoint()));
                    if (f != null && s.contains("SolvedTimes")) setters.add(f);
                }
            }
        }

        out.println();
        for (Function f : setters) {
            decomp(f, "SolvedTimes-referencing function");
            out.println("  -- callers --");
            for (Reference r : currentProgram.getReferenceManager().getReferencesTo(f.getEntryPoint())) {
                if (!r.getReferenceType().isCall()) continue;
                Function c = getFunctionContaining(r.getFromAddress());
                out.println("     <- " + r.getFromAddress() + " in " + (c == null ? "?" : c.getName() + "@" + c.getEntryPoint()));
            }
            out.println();
        }

        out.close();
        println("DumpSolvedTimes done -> " + OUT + "solved_times.txt");
    }
}
