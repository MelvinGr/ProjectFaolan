import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.*;
import java.util.*;
public class DumpRecipe extends GhidraScript {
    Memory mem;
    Address ss(Address a) throws Exception { Address c=a; for(int i=0;i<400;i++){ if((getByte(c.subtract(1))&0xFF)==0) return c; c=c.subtract(1);} return a; }
    String rs(Address a,int m){StringBuilder s=new StringBuilder();try{for(int i=0;i<m;i++){int b=getByte(a.add(i))&0xFF;if(b==0)break;s.append(b>=0x20&&b<0x7F?(char)b:'.');}}catch(Exception e){}return s.toString();}
    public void run() throws Exception {
        mem=currentProgram.getMemory(); ReferenceManager rm=currentProgram.getReferenceManager();
        String[] nd={"Recipe","recipe","RequestRecipeInfo","KnownRecipe","RecipeBook","AddRecipe","LearnRecipe","Tradeskill","tradeskill"};
        Set<Long> seen=new HashSet<>(); Set<String> fns=new LinkedHashSet<>();
        for(String n:nd){ byte[] kb=n.getBytes("US-ASCII"); Address a=mem.findBytes(currentProgram.getMinAddress(),kb,null,true,monitor); int g=0;
            while(a!=null&&g++<200){ Address st=ss(a); if(seen.add(st.getOffset())){ String full=rs(st,90); List<String> rf=new ArrayList<>();
                for(Reference r:rm.getReferencesTo(st)){ Function f=getFunctionContaining(r.getFromAddress()); if(f!=null){rf.add(f.getName()+"@"+f.getEntryPoint()); fns.add(f.getEntryPoint()+" "+f.getName());}}
                if(!rf.isEmpty()&&full.length()>4) println("STR "+st+" \""+full+"\" <- "+rf); }
                a=mem.findBytes(a.add(1),kb,null,true,monitor);} }
        println("\n== fns ("+fns.size()+") =="); for(String f:fns) println("FN "+f);
    }
}
