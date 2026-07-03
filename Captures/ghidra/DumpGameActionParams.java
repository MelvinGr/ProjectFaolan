// Map every GameAction param-getter call (FUN_0089bd80(action, PARAMID)) to its PARAMID constant + containing
// function, so we can find the GameAction_MinLevel usability check (reads MinLevel, compares to a player level).
// Also decompile the small functions that read a param AND reference a "level"/"Level"/requirement so we can see
// which player field the requirement is compared against.
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpGameActionParams.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpGameActionParams extends GhidraScript {
    static final long PARAM_READER = 0x0089bd80L;

    // Walk backwards from a call site to find the immediate pushed as the 2nd integer arg (the param id).
    Long findParamId(Instruction call) {
        Instruction in = call;
        for (int i = 0; i < 8 && in != null; i++) {
            in = in.getPrevious();
            if (in == null) break;
            String m = in.getMnemonicString();
            if (m.startsWith("PUSH")) {
                for (Object o : in.getOpObjects(0))
                    if (o instanceof Scalar) {
                        long v = ((Scalar) o).getUnsignedValue() & 0xffffffffL;
                        if (v > 0x200 && v < 0x400) return v; // GameAction param ids cluster ~0x2xx-0x3xx
                    }
            }
        }
        return null;
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/gameaction_params.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);

        Function pr = getFunctionAt(toAddr(PARAM_READER));
        if (pr == null) { out.println("param reader not found"); out.close(); return; }

        // 1. Map paramId -> the functions that read it.
        Map<Long, Set<String>> byParam = new TreeMap<>();
        Map<String, Set<Long>> byFunc = new HashMap<>();
        for (Reference ref : getReferencesTo(pr.getEntryPoint())) {
            Instruction call = getInstructionAt(ref.getFromAddress());
            if (call == null) continue;
            Function f = getFunctionContaining(ref.getFromAddress());
            String fn = f == null ? "?" : f.getName() + "@" + f.getEntryPoint();
            Long id = findParamId(call);
            if (id == null) continue;
            byParam.computeIfAbsent(id, k -> new TreeSet<>()).add(fn);
            byFunc.computeIfAbsent(fn, k -> new TreeSet<>()).add(id);
        }
        out.println("===== paramId -> reader functions =====");
        for (Map.Entry<Long, Set<String>> e : byParam.entrySet())
            out.println(String.format("  0x%X : %s", e.getKey(), e.getValue()));
        out.println();

        // 2. Decompile functions that read >= 2 distinct params (likely requirement validators reading MinLevel +
        //    Profession together) OR are small single-param readers - the requirement check reads MinLevel + compares.
        out.println("===== multi-param functions (likely requirement validators) =====");
        Set<String> seen = new HashSet<>();
        for (Map.Entry<String, Set<Long>> e : byFunc.entrySet()) {
            if (e.getValue().size() < 2) continue;
            String[] parts = e.getKey().split("@");
            Address a = toAddr(parts[1]);
            Function f = getFunctionContaining(a);
            if (f == null || f.getBody().getNumAddresses() > 2500 || !seen.add(f.getName())) continue;
            out.println("========= " + e.getKey() + " reads params " + e.getValue() + " size=" + f.getBody().getNumAddresses() + " =========");
            DecompileResults res = di.decompileFunction(f, 90, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
            out.println();
        }
        out.close();
        println("wrote /home/melvin/Development/Ghidra/Aoc/gameaction_params.txt");
    }
}
