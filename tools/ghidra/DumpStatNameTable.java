// The client's STAT-DEFINITION table: a long static-init run that registers every stat id with a NAME string.
// Each stat is registered as several 0x20-byte records - `MOV dword ptr [<global>],<statId>` - and the FIRST
// record of each stat is preceded by `PUSH <name string>`. So walking the init in address order and pairing each
// PUSH-of-a-string with the stat ids that follow it recovers id -> name for the WHOLE table, including the
// 1000000+ range the LDB stat table (category 615) does not name at all.
//   FROM=01de0000 TO=01df0000 analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpStatNameTable.java
// Output: $FAOLAN_GHIDRA_OUT/stat_name_table.txt  (TSV: statId <tab> hex <tab> name)
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
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
        long from = Long.parseLong(System.getenv().getOrDefault("FROM", "01dd0000").trim(), 16);
        long to = Long.parseLong(System.getenv().getOrDefault("TO", "01e00000").trim(), 16);
        Map<Long, String> named = new LinkedHashMap<>();
        Map<Long, Long> slots = new LinkedHashMap<>();
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
                    boolean plausible = (id > 0 && id < 0x2000) || (id >= 0xF4240 && id <= 0xF4400) || (id >= 0x1E8000 && id <= 0x1E9000);
                    if (plausible) {
                        named.putIfAbsent(id, pending);
                        slots.putIfAbsent(id, slot);
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
        println("stats named: " + named.size());
    }
}
