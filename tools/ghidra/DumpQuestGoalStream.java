// Find QuestGoal_c's stream READER and WRITER by their field signature - #550.
//
// `CoreQuestHolder::QuestGoalEvent` (FUN_00810070) pins two adjacent members of the goal object: `+0x38` is the
// goal's required count and `+0x3c` is `SolvedTimes`, the progress counter (`SetSolvedTimes(goal, goal->0x38)` on
// GoalFinished, `SetSolvedTimes(goal, goal->0x3c + delta)` on progress). The question #550 asks is which WIRE word
// lands in `+0x3c`, so what is wanted is the function that stores a freshly-read stream word there.
//
// Anchor: the BinaryStream u32 primitive is FUN_0048f900 (see DumpStringPrimitive / the questc decode notes).
// Every function that both calls it and writes `[reg + 0x3c]` is a candidate; the reader is the one that also
// writes `+0x38` immediately before.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpQuestGoalStream extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    // The BinaryStream scalar primitives the quest readers go through.
    static final long[] STREAM_FNS = { 0x0048f900L, 0x0048f840L, 0x0048f720L };

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "questgoal_stream.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        // Every function calling a stream primitive.
        Set<Function> candidates = new LinkedHashSet<>();
        for (long a : STREAM_FNS) {
            Function t = getFunctionContaining(toAddr(a));
            if (t == null) continue;
            for (Reference r : currentProgram.getReferenceManager().getReferencesTo(t.getEntryPoint())) {
                if (!r.getReferenceType().isCall()) continue;
                Function c = getFunctionContaining(r.getFromAddress());
                if (c != null) candidates.add(c);
            }
        }
        out.println("stream-primitive callers: " + candidates.size());

        // Keep the ones whose disassembly touches BOTH +0x38 and +0x3c - the required-count / SolvedTimes pair.
        List<Function> hits = new ArrayList<>();
        for (Function f : candidates) {
            if (monitor.isCancelled()) break;
            boolean has38 = false, has3c = false;
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                String s = it.next().toString();
                if (s.contains("0x38]") || s.contains("0x38 ]")) has38 = true;
                if (s.contains("0x3c]") || s.contains("0x3c ]")) has3c = true;
            }
            if (has38 && has3c) hits.add(f);
        }

        out.println("candidates touching both +0x38 and +0x3c: " + hits.size());
        for (Function f : hits)
            out.println("  " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses());
        out.println();

        for (Function f : hits) {
            out.println("################## " + f.getName() + " @" + f.getEntryPoint());
            try {
                DecompileResults res = di.decompileFunction(f, 200, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
            } catch (Exception ex) { out.println("  <exception " + ex + ">"); }
        }

        out.close();
        println("DumpQuestGoalStream done -> " + OUT + "questgoal_stream.txt");
    }
}
