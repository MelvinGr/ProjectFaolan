// Enumerate every defined string whose text matches a GM-related regex, with the functions that reference it.
// One pass over the DataIterator. REGEX env overrides the default.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;
import java.util.regex.*;

public class DumpGmStrings extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    @Override
    public void run() throws Exception {
        String rx = System.getenv().getOrDefault("REGEX",
            "(?i)\\bgm\\b|gmflag|gm_?level|gmlevel|gamemaster|game master|petition|\\bgm[a-z]|[a-z]gm\\b|immort|invulner|no.?clip|noclip|godmode|god mode|privile|accesslevel|access level|debugclient|SetStat|isgm|nametag|admin");
        Pattern p = Pattern.compile(rx);
        PrintWriter out = new PrintWriter(OUT + "gm_strings.txt");
        ReferenceManager rm = currentProgram.getReferenceManager();
        Listing listing = currentProgram.getListing();
        DataIterator di = listing.getDefinedData(true);
        int n = 0;
        while (di.hasNext()) {
            Data d = di.next();
            if (d == null) continue;
            String t = d.getDataType().getName().toLowerCase();
            if (!(t.contains("unicode") || t.contains("string") || t.contains("char"))) continue;
            Object v = d.getValue();
            if (v == null) continue;
            String s = v.toString();
            if (s.length() < 2 || s.length() > 200) continue;
            if (!p.matcher(s).find()) continue;
            Address a = d.getAddress();
            StringBuilder refs = new StringBuilder();
            for (Reference r : rm.getReferencesTo(a)) {
                Function f = getFunctionContaining(r.getFromAddress());
                refs.append("  <- @").append(r.getFromAddress()).append(" ")
                    .append(f != null ? f.getName()+"@"+f.getEntryPoint() : "<none>");
            }
            out.println("@" + a + "  \"" + s.replace("\n","\\n") + "\"" + refs);
            n++;
        }
        out.flush(); out.close();
        println("WROTE " + OUT + "gm_strings.txt (" + n + " strings)");
    }
}
