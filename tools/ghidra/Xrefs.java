import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
public class Xrefs extends GhidraScript {
    public void run() throws Exception {
        long a=Long.parseLong(System.getenv("ADDR"),16);
        Address t=toAddr(a);
        ReferenceManager rm=currentProgram.getReferenceManager();
        java.util.Set<String> fns=new java.util.LinkedHashSet<>();
        for (Reference r: rm.getReferencesTo(t)){
            Function f=getFunctionContaining(r.getFromAddress());
            fns.add((f!=null?(f.getName()+"@"+f.getEntryPoint()):"?")+" ["+r.getReferenceType()+"]");
        }
        for(String s: fns) println("  "+s);
    }
}
