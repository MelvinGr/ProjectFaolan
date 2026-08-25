// Every defined string CONTAINING a substring (STR env, ';'-separated, case-insensitive), with the functions
// that reference each one. The broad first sweep into an unknown subsystem: you rarely know a log line exactly,
// but you almost always know a word that appears in it.
//
//   STR=waypoint;compass /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript ListStr.java
//
// Output: $FAOLAN_GHIDRA_OUT/liststr.txt (falling back to the system temp dir). It writes a FILE rather than
// printing, for the reason DecompileFns.java gives: analyzeHeadless prefixes only the FIRST line of a
// multi-line println, so the usual `grep <script>` over its output silently drops everything after line one.
//
// How it differs from the two neighbours, so a third is not needed:
//   * FindNeedles.java   - EXACT needles, capped at 12 hits each, stdout. Use when you know the whole string.
//   * DumpStringsInRange - every string in an ADDRESS RANGE. Use as the follow-up: once this finds one hit,
//                          that hit's neighbours are usually the rest of the table.
// This one is the step before both: substring, unbounded, and it names the referring function for each hit,
// which is what turns a word into a set of entry points.
//
// ⭐ Worked example (Gitea #464): `STR=waypoint;compass` returned 62 strings, among them
// `"...DispatchAddWayPoint() not possible, stream bad after 'i_vRadius'!"` - the client naming a wire field in
// its own validation message. That named a quest-goal scalar four rounds of offset scans had not.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;

public class ListStr extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String env = System.getenv("STR");
        if (env == null || env.isEmpty()) { println("set STR=word[;word...]"); return; }
        String[] needles = env.toLowerCase().split(";");

        PrintWriter out = new PrintWriter(OUT + "liststr.txt");
        DataIterator it = currentProgram.getListing().getDefinedData(true);
        int n = 0;
        while (it.hasNext()) {
            Data d = it.next();
            Object v = d.getValue();
            if (!(v instanceof String)) continue;
            String s = (String) v, ls = s.toLowerCase();
            boolean hit = false;
            for (String needle : needles) if (!needle.isEmpty() && ls.contains(needle)) hit = true;
            if (!hit) continue;

            StringBuilder refs = new StringBuilder();
            for (Reference r : getReferencesTo(d.getAddress())) {
                Function f = getFunctionContaining(r.getFromAddress());
                refs.append(" ").append(f == null ? r.getFromAddress().toString() : f.getEntryPoint().toString());
            }
            out.println(d.getAddress() + "  \"" + s.replace("\n", "\\n") + "\" <-" + refs);
            n++;
        }
        out.close();
        println("WROTE " + OUT + "liststr.txt  hits=" + n);
    }
}
