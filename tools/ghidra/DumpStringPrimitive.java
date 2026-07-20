// Settles two open questions at once (Gitea #388 + the ASCII/UTF-8/Latin1 inconsistency):
//
//   1. What LENGTH WIDTH does the client's string reader use? Our codecs model the same <remoteformat> content
//      two ways - QuestcRecord reads [u16 BE len][text] while QuestObjectiveGoal reads [0x00][u8 len][text].
//      Those are the same bytes below 256, and the longest captured record is 119 B, so the corpus cannot decide.
//   2. Does the client TRANSCODE the bytes (ASCII / Latin1 / UTF-8), or copy them raw into a std::string? Our
//      codebase mixes Encoding.ASCII (58 sites), Latin1 (23) and UTF8 (12); every decodable captured string is
//      pure ASCII, so again the corpus cannot decide.
//
//   ln -sfn "$PWD/Captures/ghidra/output" /tmp/aoc_gh
//   mkdir -p /tmp/aoc_ghscripts && cp Captures/ghidra/*.java /tmp/aoc_ghscripts/
//   /opt/ghidra/support/analyzeHeadless /tmp/aoc_gh aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath /tmp/aoc_ghscripts -postScript DumpStringPrimitive.java
//
// Output: Captures/ghidra/output/string_primitive.txt
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;

public class DumpStringPrimitive extends GhidraScript {
    // FUN_0048f840 = ReadString (docs/re/fullplayer-snapshot-reader.md: "the only string reader in the tails").
    // FUN_00922c90 = the List B / @609 per-record reader ("u32 + 3x ReadString + ACGHash").
    // FUN_0091df50 = the Quest_c per-record reader, for comparison (known to use ReadString for its 5 headers).
    static final String[] FNS = { "0040f000", "00cbf840", "0048f840" };

    @Override
    public void run() throws Exception {
        java.io.File file = new java.io.File(getProjectRootFolder().getProjectLocator().getLocation(), "string_primitive.txt");
        PrintWriter out = new PrintWriter(file);
        DecompInterface d = new DecompInterface();
        d.openProgram(currentProgram);

        for (String s : FNS) {
            Address a = currentProgram.getAddressFactory().getAddress(s);
            Function fn = getFunctionContaining(a);
            out.println("\n==================== FUN_" + s + " ====================");
            if (fn == null) {
                out.println("(no function at this address)");
                continue;
            }
            DecompileResults r = d.decompileFunction(fn, 180, monitor);
            out.println(r != null && r.getDecompiledFunction() != null
                ? r.getDecompiledFunction().getC()
                : "(decompile failed)");
        }

        out.close();
        d.dispose();
        println("wrote " + file);
    }
}
