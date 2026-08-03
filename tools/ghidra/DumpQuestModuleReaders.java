// List the quest module's stream readers - #550. `quest.cpp` lives around 0x0091d000-0x00930000 (Quest_c's reader
// FUN_0091df50, its writer FUN_0091d260, the QuestGoal_c accessors FUN_009260c0/FUN_00926220/FUN_00926320 and the
// log stubs at FUN_0092730x-FUN_0092748x are all in it). This prints every function in the range with the count of
// BinaryStream scalar reads it makes and whether it stores to the goal object's `+0x38`/`+0x3c` pair, so the goal
// record's reader can be picked out rather than guessed.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpQuestModuleReaders extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long LO = 0x0091d000L, HI = 0x00930000L;
    static final long[] STREAM_FNS = { 0x0048f900L, 0x0048f840L, 0x0048f720L };

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "quest_module_readers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        Set<Address> streamEntries = new HashSet<>();
        for (long a : STREAM_FNS) {
            Function t = getFunctionContaining(toAddr(a));
            if (t != null) streamEntries.add(t.getEntryPoint());
        }

        List<Function> ordered = new ArrayList<>();
        FunctionIterator fi = currentProgram.getFunctionManager().getFunctions(true);
        while (fi.hasNext()) {
            Function f = fi.next();
            long e = f.getEntryPoint().getOffset();
            if (e >= LO && e < HI) ordered.add(f);
        }

        out.println("functions in [" + Long.toHexString(LO) + "," + Long.toHexString(HI) + "): " + ordered.size());
        List<Function> readers = new ArrayList<>();
        for (Function f : ordered) {
            int reads = 0;
            boolean has38 = false, has3c = false;
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                for (Reference r : ins.getReferencesFrom())
                    if (streamEntries.contains(r.getToAddress())) reads++;
                String s = ins.toString();
                if (s.contains("0x38]")) has38 = true;
                if (s.contains("0x3c]")) has3c = true;
            }
            if (reads == 0) continue;
            out.println(String.format("  %s @%s size=%d streamCalls=%d +0x38=%b +0x3c=%b",
                f.getName(), f.getEntryPoint(), f.getBody().getNumAddresses(), reads, has38, has3c));
            if (has3c || reads >= 8) readers.add(f);
        }

        out.println();
        for (Function f : readers) {
            out.println("################## " + f.getName() + " @" + f.getEntryPoint());
            try {
                DecompileResults res = di.decompileFunction(f, 200, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile failed>");
            } catch (Exception ex) { out.println("  <exception " + ex + ">"); }
        }

        out.close();
        println("DumpQuestModuleReaders done -> " + OUT + "quest_module_readers.txt");
    }
}
