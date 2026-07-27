// Names every one of the client's CoreTables data tables in ONE run: RDB type -> loader -> C++ class.
//
// The two halves each answer half the question and neither answers it alone:
//   1. Every table's LoadStaticData calls the same loader helper with its RDB type as an IMMEDIATE, so the
//      references to that helper enumerate the loaders and the immediates give the types (docs/rdb/client-data-tables.md).
//   2. The loader's address then sits in its class's vtable, and an MSVC vtable is preceded by a pointer to its
//      Complete Object Locator: COL+0xC is the type descriptor, TD+8 is the decorated name. Walking back from the
//      vtable SLOT that holds the loader to the head of the vtable therefore NAMES the table.
// Step 2 is the part that is easy to give up on - LoadStaticData is virtual on some of these classes and not on
// others, so a miss is "not in a vtable", not "wrong approach". Every hit is exact.
//
//   HELPER=104d760 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath tools/ghidra -postScript DumpDataTableRegistry.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

public class DumpDataTableRegistry extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    // The RDB type-id space the data tables live in. Wide enough to catch a table nobody has seen yet, narrow
    // enough that ordinary constants in the same function do not read as types.
    static final long TYPE_LO = 1_000_000, TYPE_HI = 1_100_000;

    // How far back from a vtable slot to look for the head of the vtable. The longest of these is well under 64.
    static final int MAX_VTABLE_SLOTS = 96;

    @Override
    public void run() throws Exception {
        Address helper = toAddr(Long.parseLong(System.getenv().getOrDefault("HELPER", "104d760").trim(), 16));
        PrintWriter out = new PrintWriter(OUT + "data_table_registry.txt");
        out.println("######## data tables via the loader helper at " + helper + " ########");
        out.println();
        out.printf("%-28s  %-12s  %s%n", "rdb types", "loader", "class");

        Set<Function> loaders = new LinkedHashSet<>();
        for (Reference r : getReferencesTo(helper)) {
            Function f = getFunctionContaining(r.getFromAddress());
            if (f != null) loaders.add(f);
        }

        for (Function loader : loaders) {
            List<Long> types = typeImmediates(loader);
            String name = className(loader.getEntryPoint());
            out.printf("%-28s  %-12s  %s%n",
                types.isEmpty() ? "(none found)" : join(types),
                loader.getEntryPoint().toString(),
                name == null ? "(not in a vtable)" : name);
        }

        out.flush();
        out.close();
        println("WROTE " + OUT + "data_table_registry.txt (" + loaders.size() + " loaders)");
    }

    // Every immediate in the function that falls in the RDB type-id space. A loader normally has exactly one; the
    // handful that serve several tables push one per table.
    private List<Long> typeImmediates(Function f) {
        List<Long> found = new ArrayList<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
        while (it.hasNext()) {
            Instruction in = it.next();
            for (int op = 0; op < in.getNumOperands(); op++)
                for (Object o : in.getOpObjects(op))
                    if (o instanceof Scalar s) {
                        long v = s.getUnsignedValue();
                        if (v >= TYPE_LO && v <= TYPE_HI && !found.contains(v)) found.add(v);
                    }
        }
        return found;
    }

    // The decorated class name of the vtable that holds `fn`, or null when it is not in one.
    private String className(Address fn) {
        for (Reference r : getReferencesTo(fn)) {
            Address slot = r.getFromAddress();
            if (getFunctionContaining(slot) != null) continue; // a call site, not a vtable slot

            // Walk back over the slots to the head of the vtable: the word before it is the COL pointer.
            for (int back = 0; back < MAX_VTABLE_SLOTS; back++) {
                try {
                    Address head = slot.subtract(back * 4L);
                    String name = nameFromCol(readPtr(head.subtract(4)));
                    if (name != null) return name;
                    if (!isCodePointer(readPtr(head))) break; // ran off the front of the table
                } catch (Exception ignored) {
                    break;
                }
            }
        }
        return null;
    }

    // COL -> type descriptor -> its ".?AV<Name>@@" string, or null if `col` is not a Complete Object Locator.
    private String nameFromCol(long col) {
        try {
            long td = readPtr(toAddr(col + 0xC));
            byte[] raw = new byte[128];
            currentProgram.getMemory().getBytes(toAddr(td + 8), raw);
            StringBuilder sb = new StringBuilder();
            for (byte b : raw) {
                if (b == 0) break;
                sb.append((char) (b & 0xFF));
            }
            String s = sb.toString();
            return s.startsWith(".?AV") ? s : null;
        } catch (Exception e) {
            return null;
        }
    }

    private boolean isCodePointer(long p) {
        try {
            return getFunctionContaining(toAddr(p)) != null;
        } catch (Exception e) {
            return false;
        }
    }

    private long readPtr(Address a) throws Exception {
        return currentProgram.getMemory().getInt(a) & 0xFFFFFFFFL;
    }

    private static String join(List<Long> xs) {
        StringBuilder sb = new StringBuilder();
        for (long x : xs) sb.append(sb.length() == 0 ? "" : ", ").append(x);
        return sb.toString();
    }
}
