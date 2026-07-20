import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
public class Callers extends GhidraScript {
    public void run() throws Exception {
        String hex=System.getenv("FN");
        Address a=currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(Long.parseLong(hex,16));
        Function tgt=getFunctionContaining(a);
        println("callers of "+hex+" ("+(tgt!=null?tgt.getName():"?")+"):");
        ReferenceManager rm=currentProgram.getReferenceManager();
        for (Reference r : rm.getReferencesTo(tgt!=null?tgt.getEntryPoint():a)) {
            if (!r.getReferenceType().isCall() && !r.getReferenceType().isData()) continue;
            Function f=getFunctionContaining(r.getFromAddress());
            println("  <- "+r.getFromAddress()+" in "+(f!=null?(f.getName()+"@"+f.getEntryPoint()):"?")+" ["+r.getReferenceType()+"]");
        }
    }
}
