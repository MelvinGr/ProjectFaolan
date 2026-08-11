// Finds the CONSUMER of a struct field by looking for functions that touch the field's NEIGHBOURS too.
//
// Written for Gitea #796: the chat-tier friend state's ONLINE stamp is stored into `ppj::Buddy_t + 0x30` and
// nothing known reads it back. A bare "who reads [reg+0x30]" scan is useless - 0x30 is a common displacement - so
// the discriminator is CO-OCCURRENCE: a Buddy_t consumer addresses several of the record's five scalar slots off
// ONE base register, and a coincidental +0x30 does not.
//
//   Buddy_t: [+0x00 nUserId][+0x04 name std::string, 0x1C bytes][+0x20 level][+0x24 class][+0x28 lastSeen]
//            [+0x2c online][+0x30 onlineStamp]
//
// So: for every function, group [base+disp] memory operands by base register, keep the groups whose displacement
// set intersects the field block in at least MIN_FIELDS places, and report the ones that include TARGET.
//
//   TARGET=30 FIELDS=20,24,28,2c,30 MIN_FIELDS=3 \
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpBuddyFieldReaders.java
//
// ⚠️ Reads AND writes are reported, separately: the store site is a known-good positive control, and a scan that
// cannot see its own control cannot be trusted when it reports nothing else.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.lang.Register;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;

public class DumpBuddyFieldReaders extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        long target = Long.parseLong(System.getenv().getOrDefault("TARGET", "30").trim(), 16);
        int minFields = Integer.parseInt(System.getenv().getOrDefault("MIN_FIELDS", "3").trim());
        Set<Long> fields = new TreeSet<>();
        for (String s : System.getenv().getOrDefault("FIELDS", "20,24,28,2c,30").split(","))
            fields.add(Long.parseLong(s.trim(), 16));

        PrintWriter out = new PrintWriter(OUT + "buddy_field_readers.txt");
        out.println("functions addressing >= " + minFields + " of " + fields + " off ONE base register, including +0x"
                + Long.toHexString(target));
        out.println();

        int scanned = 0, hits = 0;
        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
        while (it.hasNext() && !monitor.isCancelled()) {
            Function f = it.next();
            scanned++;

            // base register -> the displacements this function uses off it, and where.
            Map<String, Set<Long>> byBase = new HashMap<>();
            Map<String, List<String>> whereByBase = new HashMap<>();

            for (Instruction in : currentProgram.getListing().getInstructions(f.getBody(), true)) {
                for (int op = 0; op < in.getNumOperands(); op++) {
                    Object[] objs = in.getOpObjects(op);
                    Register base = null;
                    Long disp = null;
                    for (Object o : objs) {
                        if (o instanceof Register r && base == null)
                            base = r;
                        else if (o instanceof Scalar s)
                            disp = s.getUnsignedValue();
                    }

                    if (base == null || disp == null || !fields.contains(disp))
                        continue;

                    String key = base.getName();
                    byBase.computeIfAbsent(key, k -> new LinkedHashSet<>()).add(disp);
                    whereByBase.computeIfAbsent(key, k -> new ArrayList<>())
                            .add(in.getAddress() + ": " + in);
                }
            }

            for (Map.Entry<String, Set<Long>> e : byBase.entrySet()) {
                if (e.getValue().size() < minFields || !e.getValue().contains(target))
                    continue;

                hits++;
                out.println("---- " + f.getName() + " @" + f.getEntryPoint() + "  base " + e.getKey()
                        + "  fields " + hex(e.getValue()));
                for (String line : whereByBase.get(e.getKey()))
                    out.println("        " + line);
                out.println();
            }
        }

        out.println("scanned " + scanned + " function(s), " + hits + " (function, base) group(s) matched.");
        out.close();
        println("wrote " + OUT + "buddy_field_readers.txt (" + hits + " groups over " + scanned + " functions)");
    }

    private static String hex(Set<Long> v) {
        StringBuilder sb = new StringBuilder("[");
        for (Long l : v)
            sb.append(sb.length() > 1 ? ", " : "").append("0x").append(Long.toHexString(l));

        return sb.append("]").toString();
    }
}
