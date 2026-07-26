import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;
public class DumpFeatLoader extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    // Find the feat-table loader/decrypt: FeatClassTable_c::LoadStaticData + any func referencing the feat error
    // strings, decompile them (+ callees) to expose the RDB read + the TEA/decrypt key.
    static final String[] STR = {"FeatClassTable_c::LoadStaticData","No RDB FeatClass data found for FeatID",
        "GDB and RDB data for feat","FeatPointsTable_c","pcFeat->GetCategory","LoadStaticData"};
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "feat_loader.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        Set<Address> addrs = new HashSet<>();
        DataIterator data = currentProgram.getListing().getDefinedData(true);
        while (data.hasNext()) { Data d=data.next(); Object v=d.getValue(); if(v==null) continue; String s=v.toString();
            for(String n:STR) if(s.contains(n)){ out.println("STR \""+s+"\" @"+d.getAddress()); addrs.add(d.getAddress()); } }
        for (Symbol sym: currentProgram.getSymbolTable().getAllSymbols(false)){ String n=sym.getName(true);
            if(n.contains("FeatClassTable")||n.contains("FeatTable")||n.contains("LoadStaticData")&&n.contains("Feat"))
                out.println("SYM "+n+" @"+sym.getAddress()); }
        out.println();
        Set<String> seen=new HashSet<>(); int budget=14;
        for(Address sa: addrs){ for(Reference ref: getReferencesTo(sa)){ Function f=getFunctionContaining(ref.getFromAddress());
            if(f==null||!seen.add(f.getEntryPoint().toString())) continue; if(budget--<=0) break;
            out.println("========= "+f.getName()+" @"+f.getEntryPoint()+" size="+f.getBody().getNumAddresses()+" =========");
            DecompileResults res=di.decompileFunction(f,120,monitor);
            out.println(res!=null&&res.decompileCompleted()?res.getDecompiledFunction().getC():"  <fail>"); out.println(); } }
        out.close(); println("done");
    }
}
