// Decompile a list of functions AND every function they call, one level deep. DecompileFns.java gives you the
// function alone, which is rarely enough for a wire reader: the reads themselves live in tiny shared helpers
// (ReadU32 / ReadU8 / ReadU64), so the caller is a list of opaque calls until you can see what each one consumes.
//   FNS=01122b10,0145ac80 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DecompileWithCallees.java
// Set DEPTH=2 to recurse a second level (helpers that wrap helpers). Output: $FAOLAN_GHIDRA_OUT/decompile_tree.txt
// (a FILE, because analyzeHeadless prefixes only the first line of a multi-line println and drops the rest).
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.*;

public class DecompileWithCallees extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    DecompInterface di;
    final Set<Function> done = new LinkedHashSet<>();

    String decomp(Function f) {
        try {
            DecompileResults r = di.decompileFunction(f, 240, monitor);
            return r != null && r.decompileCompleted() ? r.getDecompiledFunction().getC() : "<decompile failed>";
        } catch (Exception ex) { return "<exception: " + ex + ">"; }
    }

    List<Function> callees(Function f) {
        List<Function> out = new ArrayList<>();
        Set<Function> seen = new LinkedHashSet<>();
        for (Instruction ins = getInstructionAt(f.getEntryPoint());
             ins != null && f.getBody().contains(ins.getAddress()); ins = ins.getNext())
            for (Reference ref : ins.getReferencesFrom()) {
                Function c = getFunctionAt(ref.getToAddress());
                if (c != null && c != f && seen.add(c)) out.add(c);
            }
        return out;
    }

    void emit(PrintWriter out, Function f, int depth, int maxDepth, String label) {
        if (!done.add(f)) {
            out.println("\n" + "  ".repeat(depth) + "----- " + label + " " + f.getName() + " (already dumped above) -----");
            return;
        }
        out.println("\n" + "=".repeat(Math.max(4, 60 - depth * 4)));
        out.println(label + " " + f.getName() + " @" + f.getEntryPoint() + " (size " + f.getBody().getNumAddresses() + ")");
        out.println("=".repeat(Math.max(4, 60 - depth * 4)));
        out.println(decomp(f));
        if (depth >= maxDepth) return;
        for (Function c : callees(f)) emit(out, c, depth + 1, maxDepth, "callee(" + (depth + 1) + ")");
    }

    @Override public void run() throws Exception {
        String fns = System.getenv("FNS");
        if (fns == null || fns.isBlank()) { println("set FNS=<hex addr>[,<hex addr>...]"); return; }
        int maxDepth = Integer.parseInt(System.getenv().getOrDefault("DEPTH", "1"));

        PrintWriter out = new PrintWriter(OUT + "decompile_tree.txt");
        di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        for (String tok : fns.split(",")) {
            Address a = toAddr(Long.parseLong(tok.trim(), 16));
            Function f = getFunctionContaining(a);
            if (f == null) { out.println("\n#### no function at " + a); continue; }
            emit(out, f, 0, maxDepth, "ROOT");
        }
        out.close();
        println("DecompileWithCallees done -> " + OUT + "decompile_tree.txt (" + done.size() + " functions)");
    }
}
