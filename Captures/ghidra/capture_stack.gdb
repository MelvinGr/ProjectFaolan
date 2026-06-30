set pagination off
set confirm off
set height 0
set logging file /tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/b0778add-a126-477b-8758-2620da06bf1b/scratchpad/stack.log
set logging overwrite on
set logging enabled on
set non-stop off
set target-async off
attach 1864049
handle SIGSEGV nostop noprint pass
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIGPWR nostop noprint pass
handle SIGXCPU nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIG34 nostop noprint pass
handle SIG35 nostop noprint pass
handle SIG36 nostop noprint pass
handle SIG37 nostop noprint pass
handle SIG38 nostop noprint pass
handle SIG39 nostop noprint pass
handle SIG40 nostop noprint pass
handle SIG41 nostop noprint pass
handle SIG42 nostop noprint pass
handle SIG43 nostop noprint pass
handle SIG44 nostop noprint pass
handle SIG63 nostop noprint pass
handle SIG64 nostop noprint pass
set $n = 0
# FUN_005b07e0 = visual resource loader, thiscall: ECX = resource-ref struct {id@+0x10, type@+0x14}.
# Dump the id+type+caller for every load (capped). Meshindex type = 0xf696e (1010030).
break *0x005b07e0
commands
  silent
  if $n < 600
    printf "L%d: ecx=0x%x id@10=%u type@14=0x%x f0c=%u ret=0x%x a1=0x%x a2=0x%x\n", $n, $ecx, *(unsigned int*)($ecx+0x10), *(unsigned int*)($ecx+0x14), *(unsigned int*)($ecx+0xc), *(unsigned int*)($esp), *(unsigned int*)($esp+4), *(unsigned int*)($esp+8)
    set $n = $n + 1
  end
  continue
end
echo READY_STACK\n
continue
