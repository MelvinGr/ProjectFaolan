# Ghidra RPC-opcode recovery (2026-06-13)

Recovers the 16-bit RPC method opcodes (`0x2000..0x20FF`) from `AgeOfConan.exe`. Result + full tables:
`docs/re/aoc-rpc-method-opcodes.md`; finding summary in `docs/re/aoc-opcode-ids.md` (Stage 3b).

## Reproduce
Analyzed Ghidra project at `tools/ghidra/output` (re-import if `/tmp` was cleared — exe is the client's
`AgeOfConan.exe` from its install dir, PE32 i386, stripped).
Run each post-script against the already-analyzed program (no re-analysis):

```
/opt/ghidra/support/analyzeHeadless tools/ghidra/output aoc -process AgeOfConan.exe -noanalysis \
  -scriptPath <this dir> -postScript <Script>.java
```

1. **DumpRpcTables.java** -> `tools/ghidra/output/rpc_tables.txt`. Every `<Class>Proxy_t::<Method>` /
   `Dispatch_t::DispatchMethod` string in address order (= c2s registration order) + the proxy
   constructors (the `min allowed` base check). Proves c2s order == `aoc-client-rpc-methods.json` order,
   base 0x2000.
2. **DumpDispatch.java** -> `tools/ghidra/output/dispatch.txt`. Decompiles each `<Class>InterfaceDispatch_t::
   DispatchMethod` — a `switch(methodIndex)` whose case N -> handler N. The switch is the authoritative
   s2c table (resolves the drift).
3. Parse the switch cases into `disp_cases.json` (case -> handler FUN address), then
   **DumpHandlerNames.java** (reads `tools/ghidra/output/handler_addrs.txt`) -> `tools/ghidra/output/handler_names.txt`
   (handler FUN -> its `<Class>Dispatch_t::<Method>` string).
4. Assemble case+name -> `s2c_tables.json` and the markdown doc (see the inline python in the handoff /
   git history, or regenerate from `disp_cases.json` + `handler_names.txt`).

The intermediate outputs (`rpc_tables.txt`, `dispatch.txt`, `handler_names.txt`, `disp_cases.json`,
`s2c_tables.json`) are written to the external Ghidra project dir (`tools/ghidra/output`)
and are NOT committed here — only these `.java` post-scripts are. The assembled ground-truth tables live
in `docs/re/aoc-rpc-method-opcodes.md` (committed), so the result is available without re-running Ghidra.
