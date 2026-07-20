import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
public class ReadSlotFn extends GhidraScript {
    public void run() throws Exception {
        long vt=Long.parseLong(System.getenv("VTADDR"),16);
        long off=Long.parseLong(System.getenv("OFF"),16);
        Address a=toAddr(vt+off);
        long p=getInt(a)&0xffffffffL;
        Function f=getFunctionContaining(toAddr(p));
        println("slot @0x"+Long.toHexString(vt+off)+" -> 0x"+Long.toHexString(p)+"  "+(f!=null?f.getName():"?"));
        if(f==null) return;
        DecompInterface di=new DecompInterface(); di.openProgram(currentProgram);
        DecompileResults r=di.decompileFunction(f,60,monitor);
        println(r.getDecompiledFunction().getC());
    }
}
