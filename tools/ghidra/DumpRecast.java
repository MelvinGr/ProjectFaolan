// #271: find how the client reads/builds a SetSpellRecastTime GameAction (property 0x2f9=761).
// Locate the strings + the functions that reference 'SetSpellRecastTime'/'recast'/'RecastTime'.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.*;
import java.util.*;
public class DumpRecast extends GhidraScript {
    Memory mem;
    Address stringStart(Address a) throws Exception { Address cur=a; for(int i=0;i<400;i++){ if((getByte(cur.subtract(1))&0xFF)==0) return cur; cur=cur.subtract(1);} return a; }
    String readStr(Address a,int max){StringBuilder sb=new StringBuilder();try{for(int i=0;i<max;i++){int b=getByte(a.add(i))&0xFF;if(b==0)break;sb.append(b>=0x20&&b<0x7F?(char)b:'.');}}catch(Exception e){}return sb.toString();}
    public void run() throws Exception {
        mem=currentProgram.getMemory(); ReferenceManager rm=currentProgram.getReferenceManager();
        String[] needles={"SetSpellRecastTime","RecastTime","recast","SpellRecast","SetRecast","GameAction","e_Type_GC_GameAction"};
        Set<Long> seen=new HashSet<>(); Set<String> fns=new LinkedHashSet<>();
        for(String n:needles){ byte[] kb=n.getBytes("US-ASCII"); Address a=mem.findBytes(currentProgram.getMinAddress(),kb,null,true,monitor); int g=0;
            while(a!=null&&g++<150){ Address st=stringStart(a); if(seen.add(st.getOffset())){ String full=readStr(st,120); List<String> rf=new ArrayList<>();
                for(Reference r:rm.getReferencesTo(st)){ Function f=getFunctionContaining(r.getFromAddress()); if(f!=null){rf.add(f.getName()+"@"+f.getEntryPoint()); fns.add(f.getEntryPoint()+" "+f.getName());}}
                if(!rf.isEmpty()) println("STR "+st+"  \""+full+"\"  <- "+rf); }
                a=mem.findBytes(a.add(1),kb,null,true,monitor);} }
        println("\n==== fns ("+fns.size()+") ===="); for(String f:fns) println("FN "+f);
    }
}
