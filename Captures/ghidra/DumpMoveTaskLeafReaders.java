import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
public class DumpMoveTaskLeafReaders extends GhidraScript {
    static final String[] TARGETS = { "PathMoveTask::vftable","BallisticMoveTask::vftable","AnimatedMoveTask::vftable","FormationMoveTask::vftable" };
    Address vt(String n){ for (Symbol s: currentProgram.getSymbolTable().getAllSymbols(false)) if (s.getName(true).equals(n)) return s.getAddress(); return null; }
    @Override public void run() throws Exception {
        java.io.PrintWriter out=new java.io.PrintWriter("Captures/ghidra/output/movetask_leaf_readers.txt");
        DecompInterface di=new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (String t: TARGETS){
            Address v=vt(t); out.println("###### "+t+" -> "+v);
            if (v==null) continue;
            for (int slot: new int[]{14,10}){
                long p=getInt(v.add(slot*4L))&0xffffffffL;
                Function f=getFunctionContaining(toAddr(p));
                out.println("  -- slot "+slot+" -> 0x"+Long.toHexString(p)+" "+(f==null?"null":f.getName()));
                if (f!=null && slot==14){ DecompileResults r=di.decompileFunction(f,160,monitor);
                    out.println(r!=null&&r.decompileCompleted()?r.getDecompiledFunction().getC():"<fail>"); }
            }
            out.println();
        }
        out.close(); println("done");
    }
}
