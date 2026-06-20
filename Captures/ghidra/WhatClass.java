import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.symbol.*;
import ghidra.program.model.listing.*;
// ADDR=<vtable addr>: print symbol there + walk MSVC RTTI (vtable-4 -> col -> typedesc name)
public class WhatClass extends GhidraScript {
  public void run() throws Exception {
    long a=Long.parseLong(System.getenv("ADDR"),16);
    Address vt=toAddr(a);
    Symbol s=getSymbolAt(vt);
    println("symbol @"+vt+": "+(s!=null?s.getName():"(none)"));
    // RTTI: [vt-4] = CompleteObjectLocator ptr
    try {
      long col=getInt(vt.subtract(4))&0xFFFFFFFFL;
      Address colA=toAddr(col);
      println("COL @"+colA+": "+(getSymbolAt(colA)!=null?getSymbolAt(colA).getName():"?"));
      long td=getInt(colA.add(12))&0xFFFFFFFFL; // +0xc TypeDescriptor
      Address tdA=toAddr(td);
      println("TypeDescriptor @"+tdA+": "+(getSymbolAt(tdA)!=null?getSymbolAt(tdA).getName():"?"));
      // type name string at td+8
      Address nm=tdA.add(8);
      StringBuilder sb=new StringBuilder();
      for(int i=0;i<80;i++){ byte b=getByte(nm.add(i)); if(b==0)break; sb.append((char)b);}
      println("RTTI name: "+sb);
    } catch(Exception e){ println("RTTI walk failed: "+e); }
  }
}
