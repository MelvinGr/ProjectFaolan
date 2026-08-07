// The client's STAT-DEFINITION table: a long static-init run that registers every stat id with a NAME string.
// Each stat is registered as several records in a 0xA0-byte descriptor, and the FIRST record of each stat is
// preceded by `PUSH <name string>`. So walking the init in address order and pairing each PUSH-of-a-string with
// the id store that follows it recovers id -> name for the WHOLE table, including the 1000000+ range the LDB stat
// table (category 615) does not name at all.
//   FROM=01d00000 TO=01e80000 analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpStatNameTable.java
// Output: $FAOLAN_GHIDRA_OUT/stat_name_table.txt  (TSV: statId <tab> hex <tab> name)
//
// ⚠️ THE PAIR IS FOUND BY THE DESCRIPTOR SLOT, NOT BY THE ID (Gitea #625). The first version of this script kept a
// pair when the STORED VALUE looked like a plausible stat id, which is a guess about the data rather than a bound
// on the table - and below 768 the false pairs dominated it, so the whole sub-768 range had to be thrown away (an
// audio-preset init nearby stores `8` "Cave", `11` "Carpeted Hallway", `2` "LoadingMusic" in exactly this shape).
//
// The descriptors live in two contiguous global regions and the id sits at a FIXED offset inside each 0xA0-byte
// record, so the destination address settles it with no appeal to what the value looks like:
//
//   0x0298F000..0x029CC000   the main table       id at slot % 0xA0 in { 0x00, 0x20, 0x40, 0x60, 0x80 }
//   0x021B8000..0x021BC000   the 1000000+ table    id at slot % 0xA0 == 0x98
//
// 0x60 carries 1361 of the 1511 rows and the rest are the table's variant records; what the set EXCLUDES is the
// point - 0x44 and 0x64 are the two neighbouring fields a descriptor also writes, and they are where the
// head-of-region artifacts live (four descriptors whose kind word is 8, which would otherwise claim id 8 from
// `TimeExist`).
//
// Under that rule the scan yields 1511 rows with 1511 DISTINCT ids and no duplicates - which is the check that
// says the filter is right, because a descriptor writes its id exactly once. It also reproduces every one of the
// 747 names the id-filtered version found, with zero disagreements, so it is a strict superset rather than a
// different reading.
//
// ⚠️ CONSUME and EMIT are deliberately different tests, and getting that wrong costs rows in both directions. A
// descriptor writes several fields in this shape, so the pending name is consumed by the first store into the
// TABLE that carries a non-zero value - not by every store (which pairs a name with a kind word: it is what used
// to lose `GameAction_LocalizerType` and seven others) and not only by an id-slot store (which lets a name run
// past its own descriptor into the next one's id, mislabelling `3 AttackSpeed` as `VolumeMass`).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.LinkedHashMap;
import java.util.Map;

public class DumpStatNameTable extends GhidraScript {
    static final java.util.regex.Pattern STORE =
            java.util.regex.Pattern.compile("MOV\\s+dword ptr \\[0x([0-9a-fA-F]+)\\],0x([0-9a-fA-F]+)");
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    // The descriptor stride, and where the id sits inside one.
    static final long Stride = 0xA0;

    private static boolean IsInTable(long slot) {
        return (slot >= 0x0298F000L && slot <= 0x029CC000L) || (slot >= 0x021B8000L && slot <= 0x021BC000L);
    }

    private static boolean IsIdSlot(long slot) {
        var offset = slot % Stride;
        if (slot >= 0x0298F000L && slot <= 0x029CC000L)
            return offset == 0x60 || offset == 0x80 || offset == 0x40 || offset == 0x20 || offset == 0x00;

        return slot >= 0x021B8000L && slot <= 0x021BC000L && offset == 0x98;
    }

    private String stringAt(long a) {
        StringBuilder sb = new StringBuilder();
        try {
            for (int i = 0; i < 64; i++) {
                int b = getByte(toAddr(a + i)) & 0xFF;
                if (b == 0) break;
                if (b < 0x20 || b > 0x7e) return null;
                sb.append((char) b);
            }
        } catch (Exception e) { return null; }
        return sb.length() >= 3 ? sb.toString() : null;
    }

    @Override
    public void run() throws Exception {
        long from = Long.parseLong(System.getenv().getOrDefault("FROM", "01d00000").trim(), 16);
        long to = Long.parseLong(System.getenv().getOrDefault("TO", "01e80000").trim(), 16);
        Map<Long, String> named = new LinkedHashMap<>();
        Map<Long, Long> slots = new LinkedHashMap<>();
        int collisions = 0;
        Listing l = currentProgram.getListing();
        String pending = null;
        // getInstructionAt only hits an exact instruction start; iterate from the address instead so an
        // arbitrary FROM lands on the next instruction rather than returning null.
        ghidra.program.model.listing.InstructionIterator it = l.getInstructions(toAddr(from), true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            if (ins.getAddress().getOffset() > to) break;
            String m = ins.getMnemonicString();
            if (m.equals("PUSH")) {
                Object[] ops = ins.getOpObjects(0);
                if (ops.length == 1 && ops[0] instanceof Scalar) {
                    String s = stringAt(((Scalar) ops[0]).getUnsignedValue());
                    if (s != null) pending = s;
                }
            } else if (m.equals("MOV") && ins.getNumOperands() == 2) {
                // Match on the rendered form - `MOV dword ptr [0x029c6c80],0xf425c` - because a MOV whose
                // destination is an absolute memory operand does not hand back an Address from getOpObjects.
                java.util.regex.Matcher mm = STORE.matcher(ins.toString());
                if (mm.matches() && pending != null) {
                    long slot = Long.parseLong(mm.group(1), 16);
                    long id = Long.parseLong(mm.group(2), 16);
                    if (id != 0 && IsInTable(slot)) {
                        if (IsIdSlot(slot)) {
                            // A descriptor writes its id once, so a second sighting is a bug in the filter rather
                            // than a stat with two names - report it instead of silently keeping the first.
                            if (named.containsKey(id)) collisions++;
                            named.putIfAbsent(id, pending);
                            slots.putIfAbsent(id, slot);
                        }

                        pending = null; // one name per stat; the stat's later records carry no PUSH
                    }
                }
            }
        }
        PrintWriter out = new PrintWriter(OUT + "stat_name_table.txt");
        named.entrySet().stream()
             .sorted(Map.Entry.comparingByKey())
             .forEach(e -> out.println(e.getKey() + "\t0x" + Long.toHexString(e.getKey()).toUpperCase() + "\t" + e.getValue()
                     + "\t0x" + Long.toHexString(slots.get(e.getKey()))));
        out.close();
        println("stats named: " + named.size() + " (id collisions: " + collisions + ")");
    }
}
