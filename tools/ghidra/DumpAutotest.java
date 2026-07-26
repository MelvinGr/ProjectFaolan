// Find the "autotest.txt" / "Autotest" string loader and decompile the referencing functions.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.mem.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpAutotest extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    String readStr(Address a, int max) {
        StringBuilder sb = new StringBuilder();
        try { for (int i=0;i<max;i++){int b=getByte(a.add(i))&0xFF; if(b==0)break; sb.append(b>=0x20&&b<0x7F?(char)b:'.');} } catch(Exception e){}
        return sb.toString();
    }
    public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "autotest.txt");
        Memory mem = currentProgram.getMemory();
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        Set<Function> done = new HashSet<>();
        for (String needle : new String[]{"autotest.txt","Autotest"}) {
            byte[] pat = (needle+"\0").getBytes("ASCII");
            Address found = mem.findBytes(currentProgram.getMinAddress(), pat, null, true, monitor);
            out.println("\n========== string \""+needle+"\" @ "+found+" ==========");
            if (found==null) continue;
            ReferenceManager rm = currentProgram.getReferenceManager();
            ReferenceIterator ri = rm.getReferencesTo(found);
            List<Address> refs = new ArrayList<>();
            while (ri.hasNext()) refs.add(ri.next().getFromAddress());
            // Ghidra may reference via a pointer-data; also scan for the LE32 of the address in code operands
            out.println("direct refs: "+refs);
            for (Address fa : refs) {
                Function f = getFunctionContaining(fa);
                if (f==null || !done.add(f)) continue;
                out.println("\n---- ref from "+fa+" in "+f.getName()+" @"+f.getEntryPoint()+" ----");
                // referenced strings in this function
                Listing lst = currentProgram.getListing();
                InstructionIterator it = lst.getInstructions(f.getBody(), true);
                while (it.hasNext()) { Instruction ins=it.next();
                    for (int oi=0;oi<ins.getNumOperands();oi++) for (Object o: ins.getOpObjects(oi))
                        if (o instanceof ghidra.program.model.scalar.Scalar) {
                            long v=((ghidra.program.model.scalar.Scalar)o).getUnsignedValue();
                            if (v>0x01000000L && v<0x02400000L) { Address sa=toAddr(v);
                                try{int b=getByte(sa)&0xFF; if(b>=0x20&&b<0x7F){String s=readStr(sa,80); if(s.length()>=4) out.println("    str @"+String.format("%08x",v)+" \""+s+"\"");}}catch(Exception e){}
                            }
                        }
                }
                out.println("  --- decompile ---");
                DecompileResults res = di.decompileFunction(f, 120, monitor);
                if (res!=null && res.decompileCompleted()) out.println(res.getDecompiledFunction().getC());
            }
        }
        out.close();
        println("done -> " + OUT + "autotest.txt");
    }
}
