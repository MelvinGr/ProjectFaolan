import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.*;
public class FindStr extends GhidraScript {
    public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        ReferenceManager rm = currentProgram.getReferenceManager();
        String needle = System.getenv("NEEDLE");
        byte[] kb = needle.getBytes("US-ASCII");
        Address a = mem.findBytes(currentProgram.getMinAddress(), kb, null, true, monitor);
        int g=0;
        while (a != null && g++<50) {
            // walk to string start
            Address cur=a; for(int i=0;i<400;i++){ if((getByte(cur.subtract(1))&0xFF)==0)break; cur=cur.subtract(1);}
            java.util.List<String> fns=new java.util.ArrayList<>();
            for (Reference r : rm.getReferencesTo(cur)) {
                Function f=getFunctionContaining(r.getFromAddress());
                fns.add(f!=null? (f.getName()+"@"+f.getEntryPoint()) : ("?@"+r.getFromAddress()));
            }
            println("STR "+cur+" refs="+fns);
            a = mem.findBytes(a.add(1), kb, null, true, monitor);
        }
    }
}
