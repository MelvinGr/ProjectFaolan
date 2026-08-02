// Names the two unexplained bits of the Quest_c record's `@+0x14` flag word (Gitea #570). Bit 4 (0x10) is settled -
// swept in-client, it greys the journal's `Share` button - but bits 3 (0x08) and 10 (0x400) rendered PIXEL-IDENTICALLY
// in that same sweep, so whatever they drive is not the journal panel and the captures cannot reach it. The corpus is
// exhausted too: bit 3 is 6 quests and bit 10 is 9, and both correlate with sets (progression lines, the Tortage
// Destiny storyline) that have already produced two superseded readings. A correlation is not a mechanism.
//
// The client's own CODE is the oracle: whichever branch tests the bit is what it means.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpQuestLineFlagBits.java
//
// Output: $FAOLAN_GHIDRA_OUT/questline_flag_bits.txt
//   PART A - every site that loads a dword at [reg+0x14] and, within a short window, ANDs/TESTs it against 0x08 or
//            0x400. This is the pattern a `if (rec->lineFlags & BIT)` compiles to, and it is cheap to scan for
//            because it needs no type propagation - the record's field offset IS the anchor.
//   PART B - the same, decompiled, for the enclosing functions, so the branch's CONSEQUENCE is readable rather than
//            just its existence. That consequence is the answer; the mask alone only says "something reads it".
//
// ⚠️ A hit is a CANDIDATE, not a naming. Offset 0x14 is a common structure offset and 8/0x400 are common masks, so
// this will surface unrelated code; it narrows a whole-binary question to a readable handful. Confirm a candidate by
// checking the enclosing function actually handles quest records (its strings, its callers, the reader FUN_0091df50),
// and prefer to finish with an in-client sweep of the named behaviour the way bit 4 was settled.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.*;

public class DumpQuestLineFlagBits extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    // ⚠️ A bit test does NOT have to appear as `AND dword [reg+0x14], 0x400`. A compiler routinely narrows it to the
    // byte that actually holds the bit, so bit 10 compiles to `TEST byte ptr [reg+0x15], 0x4` - a different offset AND
    // a different immediate, which a naive scan for 0x400 at 0x14 misses completely. Both forms are enumerated here:
    // (displacement, immediate) pairs that select bit 3 or bit 10 of the dword at +0x14, whatever width the access is.
    // The dword field's own offset stays the anchor; only the projection varies.
    static final long[][] SITES = {
            { 0x14L, 0x08L }, // bit 3, byte 0 of the field (also the dword form: dword & 8)
            { 0x14L, 0x400L }, // bit 10, dword or word access at the field itself
            { 0x15L, 0x04L } // bit 10, narrowed to byte 1 - the form a compiler prefers
    };
    static final int WINDOW = 12; // instructions between the load and the test - a compiler keeps these close

    public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "questline_flag_bits.txt");
        out.println("Quest_c @+0x14 bits 3 (0x08) and 10 (0x400): sites that load [reg+0x14] then mask it (#570)");
        out.println("A hit is a CANDIDATE - 0x14 is a common offset and these are common masks. See the script header.");
        out.println();

        // Ordered set so PART B decompiles each enclosing function once, in address order.
        Set<Function> enclosing = new LinkedHashSet<>();

        out.println("=== PART A - load-then-mask sites ===");
        int hits = 0;
        for (Function fn : currentProgram.getFunctionManager().getFunctions(true)) {
            if (monitor.isCancelled()) break;

            // Walk the function's instructions, remembering the register a [reg+0x14] load landed in and how long ago.
            InstructionIterator it = currentProgram.getListing().getInstructions(fn.getBody(), true);
            List<Instruction> recent = new ArrayList<>();
            while (it.hasNext()) {
                Instruction ins = it.next();
                recent.add(ins);
                if (recent.size() > WINDOW) recent.remove(0);

                Long mask = maskOf(ins);
                if (mask == null) continue;

                // The load may be THIS instruction (`TEST byte ptr [reg+0x15], 4` does both) or a recent one that put
                // the field in a register. Missing the single-instruction case is what made the first run come back
                // empty over the quest module.
                Instruction load = null;
                for (Instruction prev : recent) {
                    if (loadsFieldOffset(prev, mask)) load = prev;
                }
                if (load == null) continue;

                hits++;
                enclosing.add(fn);
                out.printf("%s  %s%n", fn.getName(), fn.getEntryPoint());
                if (load != ins) out.printf("    load @ %s : %s%n", load.getAddress(), load);
                out.printf("    mask @ %s : %s   (0x%X)%n", ins.getAddress(), ins, mask);
            }
        }
        out.printf("%n%d candidate site(s) in %d function(s).%n%n", hits, enclosing.size());

        out.println("=== PART B - the enclosing functions, decompiled (read the BRANCH, not the mask) ===");
        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);
        try {
            for (Function fn : enclosing) {
                if (monitor.isCancelled()) break;
                out.printf("%n----- %s  %s -----%n", fn.getName(), fn.getEntryPoint());
                DecompileResults res = decomp.decompileFunction(fn, 60, monitor);
                out.println(res != null && res.decompileCompleted()
                        ? res.getDecompiledFunction().getC()
                        : "  (decompilation failed)");
            }
        } finally {
            decomp.dispose();
        }

        out.close();
        println("wrote " + OUT + "questline_flag_bits.txt (" + hits + " sites, " + enclosing.size() + " functions)");
    }

    // The AND/TEST/BT immediate, when this instruction selects one of the bits we are naming; else null. BT is
    // included because it indexes the BIT rather than masking: `BT dword [reg+0x14], 0xa` is bit 10 and `, 3` is bit 3.
    private Long maskOf(Instruction ins) {
        String op = ins.getMnemonicString().toUpperCase();
        boolean isBitTest = op.equals("BT");
        if (!op.equals("AND") && !op.equals("TEST") && !isBitTest) return null;

        for (int i = 0; i < ins.getNumOperands(); i++) {
            for (Object obj : ins.getOpObjects(i)) {
                if (!(obj instanceof Scalar)) continue;
                long v = ((Scalar) obj).getUnsignedValue();
                if (isBitTest) {
                    if (v == 3 || v == 10) return 1L << (int) v; // report the mask, not the index
                    continue;
                }

                for (long[] site : SITES) {
                    if (v == site[1]) return v;
                }
            }
        }
        return null;
    }

    // Whether this instruction reads the field at the displacement that pairs with `mask` - so a 0x04 immediate only
    // counts against [reg+0x15], not against any bracket that happens to be nearby.
    private boolean loadsFieldOffset(Instruction ins, long mask) {
        String op = ins.getMnemonicString().toUpperCase();
        if (!op.startsWith("MOV") && !op.equals("TEST") && !op.equals("AND") && !op.equals("BT")) return false;

        for (int i = 0; i < ins.getNumOperands(); i++) {
            String text = ins.getDefaultOperandRepresentation(i);
            if (text == null || !text.contains("[")) continue;

            for (long[] site : SITES) {
                if (site[1] != mask && !(mask == 0x08L && site[1] == 0x08L)) continue;

                String disp = "0x" + Long.toHexString(site[0]);
                if (text.contains(disp + "]") || text.contains("+ " + disp)) return true;
            }
        }
        return false;
    }
}
