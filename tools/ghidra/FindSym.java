import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.*;
// Q=substring : list symbols whose name contains Q (case-insensitive).
public class FindSym extends GhidraScript {
  public void run() throws Exception {
    String q=System.getenv("Q").toLowerCase();
    SymbolIterator it=currentProgram.getSymbolTable().getAllSymbols(true);
    int n=0;
    while(it.hasNext() && n<60){ Symbol s=it.next();
      if(s.getName().toLowerCase().contains(q)){ println(s.getAddress()+"  "+s.getName()); n++; } }
    println("== "+n+" matches ==");
  }
}
