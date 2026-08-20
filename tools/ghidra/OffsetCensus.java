// #658: per-function census over a SET of struct offsets (OFFS env, comma-sep hex). Reports every function
// that touches the KEY offset (KEY env) in any operand position with a non-stack base register, together with
// its touch count for each other offset - the co-occurrence fingerprint that identifies a handler of one
// specific class when the key offset alone is too common (BuffInfo_c here: +0x24 with +0xa4/+0xa8/+0xac/+0xc0).
//   OFFS=14,18,1c,20,24,28,2c,30,34,a4,a8,ac,c0 KEY=24 MIN=2 analyzeHeadless ... -postScript OffsetCensus.java
// MIN = minimum number of DISTINCT non-key offsets also touched. Output: $FAOLAN_GHIDRA_OUT/offset_census.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;
import java.util.*;
import java.util.regex.*;

public class OffsetCensus extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String[] offs = System.getenv().getOrDefault("OFFS", "24").toLowerCase().split(",");
        String key = System.getenv().getOrDefault("KEY", offs[0]).toLowerCase();
        int min = Integer.parseInt(System.getenv().getOrDefault("MIN", "2"));
        // [REG + 0xNN] with REG in EAX..EDI but not ESP/EBP
        Map<String, Pattern> pats = new LinkedHashMap<>();
        for (String o : offs)
            pats.put(o, Pattern.compile("\\[(EAX|EBX|ECX|EDX|ESI|EDI) \\+ 0x" + o + "\\]"));

        Map<Function, Map<String, Integer>> hits = new LinkedHashMap<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            String full = ins.toString();
            if (!full.contains("[")) continue;
            for (var e : pats.entrySet()) {
                if (e.getValue().matcher(full).find()) {
                    Function f = getFunctionContaining(ins.getAddress());
                    if (f == null) continue;
                    hits.computeIfAbsent(f, k -> new HashMap<>()).merge(e.getKey(), 1, Integer::sum);
                }
            }
        }
        int n = 0;
        try (PrintWriter w = new PrintWriter(OUT + "offset_census.txt")) {
            for (var e : hits.entrySet()) {
                Map<String, Integer> m = e.getValue();
                if (!m.containsKey(key)) continue;
                int others = 0;
                for (String o : m.keySet()) if (!o.equals(key)) others++;
                if (others < min) continue;
                n++;
                StringBuilder sb = new StringBuilder();
                sb.append(e.getKey().getName()).append(" @").append(e.getKey().getEntryPoint()).append("   ");
                for (String o : offs)
                    if (m.containsKey(o)) sb.append("+0x").append(o).append("x").append(m.get(o)).append(" ");
                w.println(sb);
            }
        }
        println("functions: " + n + " -> " + OUT + "offset_census.txt");
    }
}
