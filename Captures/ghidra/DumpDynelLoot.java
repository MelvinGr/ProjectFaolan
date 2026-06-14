// Decompile the SimpleDynel/LootBag FullUpdate readers (#187). Their ReadSubClass references the .cpp filename
// string for assertions; Ghidra's ref index missed it, so we find the string address and search the code for
// that address used as a 4-byte LE immediate (push <addr>), then decompile the containing function.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;
import java.util.*;
public class DumpDynelLoot extends GhidraScript {
    static final String[] ANCHORS = {
        "simpledynelfullupdateiir.cpp", "lootbagfullupdateiir.cpp", "climbingdynelfullupdateiir.cpp",
        "GameCodeDynelFullUpdateIIR.cpp",
    };
    Address findStr(String s) throws Exception {
        return currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), s.getBytes("US-ASCII"), null, true, monitor);
    }
    byte[] le(long v){ return new byte[]{(byte)v,(byte)(v>>8),(byte)(v>>16),(byte)(v>>24)}; }
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/tmp/ghidra-aoc/dynel_loot.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        Set<Long> dumped = new HashSet<>();
        for (String anchor : ANCHORS) {
            Address s = findStr(anchor);
            out.println("################## \""+anchor+"\" -> "+s);
            if (s==null) continue;
            byte[] needle = le(s.getOffset());
            Address a = currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), needle, null, true, monitor);
            int g=0;
            while (a!=null && g<12) {
                Function f = getFunctionContaining(a);
                if (f!=null && dumped.add(f.getEntryPoint().getOffset())) {
                    out.println("==== ref @"+a+" in "+f.getName()+" @"+f.getEntryPoint()+" size="+f.getBody().getNumAddresses()+" ====");
                    try { DecompileResults res=di.decompileFunction(f,150,monitor);
                        out.println(res!=null&&res.decompileCompleted()?res.getDecompiledFunction().getC():"<fail>"); } catch(Exception ex){ out.println(ex); }
                    out.println(); g++;
                }
                a = currentProgram.getMemory().findBytes(a.add(1), needle, null, true, monitor);
            }
        }
        out.close(); println("done -> /tmp/ghidra-aoc/dynel_loot.txt");
    }
}
