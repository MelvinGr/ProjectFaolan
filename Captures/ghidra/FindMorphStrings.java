// Locate the body-morph / cluster-deform code: search a batch of needle strings (CLUS/DPPM chunk tags,
// CHARACTERCREATOR MorphNode XML attrs, CLUS cluster names) and report each string's address + the
// functions that reference it. One project load for the whole batch.
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath Captures/ghidra -postScript FindMorphStrings.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.*;

public class FindMorphStrings extends GhidraScript {
    public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        ReferenceManager rm = currentProgram.getReferenceManager();
        String[] needles = {
            "DPPM", "CLUS", "INF0",
            "MorphNode", "MorphName", "min_value", "max_value",
            "BodyNodes", "FaceNodes", "MorphName_Negative", "MorphName_Positive",
            "bosom", "Bosom", "breasts", "thighs", "chest_middle", "torso_sides",
            "CHARACTERCREATOR", "CharacterCreator", "monsterdataid",
            "ClusterMesh", "MorphMesh", "ScalableMesh", "SkinCluster"
        };
        for (String needle : needles) {
            byte[] kb = needle.getBytes("US-ASCII");
            Address a = mem.findBytes(currentProgram.getMinAddress(), kb, null, true, monitor);
            int g = 0;
            while (a != null && g++ < 12) {
                // walk to string start (preceded by 0)
                Address cur = a;
                for (int i = 0; i < 600; i++) { if ((getByte(cur.subtract(1)) & 0xFF) == 0) break; cur = cur.subtract(1); }
                java.util.LinkedHashSet<String> fns = new java.util.LinkedHashSet<>();
                for (Reference r : rm.getReferencesTo(cur)) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    fns.add(f != null ? (f.getName() + "@" + f.getEntryPoint()) : ("xref@" + r.getFromAddress()));
                }
                if (!fns.isEmpty())
                    println("STR[" + needle + "] @" + cur + " refs=" + fns);
                a = mem.findBytes(a.add(1), kb, null, true, monitor);
            }
        }
        println("=== done ===");
    }
}
