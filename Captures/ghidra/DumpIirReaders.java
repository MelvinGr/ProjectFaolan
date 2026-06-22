// Decompile the ReadSubClass readers for the remaining verbatim IIR types (#187 dynel/loot, #188 knubot).
// Anchors on the .cpp filename / type-name strings the readers reference.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.io.PrintWriter;
import java.util.*;
public class DumpIirReaders extends GhidraScript {
    static final String[] ANCHORS = {
        "simpledynelfullupdateiir.cpp", "lootbagfullupdateiir.cpp",
        "KnubotAnswerListIIR_c::ReadSubClass", "KnubotBaseIIR_c::ReadSubClass",
        "KnubotOpenChatWindowIIR_c", "KnubotAppendTextIIR_c",
    };
    Address findStr(String s) throws Exception {
        return currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), s.getBytes("US-ASCII"), null, true, monitor);
    }
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/iir_readers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        ReferenceManager rm = currentProgram.getReferenceManager();
        Set<Long> dumped = new HashSet<>();
        for (String anchor : ANCHORS) {
            Address s = findStr(anchor);
            out.println("################## anchor \""+anchor+"\" -> "+s);
            if (s==null) continue;
            int g=0;
            for (Reference r : rm.getReferencesTo(s)) {
                Function f = getFunctionContaining(r.getFromAddress());
                if (f==null || !dumped.add(f.getEntryPoint().getOffset())) continue;
                out.println("==== ref @"+r.getFromAddress()+" in "+f.getName()+" @"+f.getEntryPoint()+" size="+f.getBody().getNumAddresses()+" ====");
                try { DecompileResults res=di.decompileFunction(f,150,monitor);
                    out.println(res!=null&&res.decompileCompleted()?res.getDecompiledFunction().getC():"  <fail>"); } catch(Exception ex){ out.println(ex); }
                out.println();
                if (++g>3) break;
            }
        }
        out.close(); println("done -> /home/melvin/Development/Ghidra/Aoc/iir_readers.txt");
    }
}
