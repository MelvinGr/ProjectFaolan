set pagination off
set confirm off
set height 0
set logging file /tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/b0778add-a126-477b-8758-2620da06bf1b/scratchpad/armorbuild.log
set logging overwrite on
set logging enabled on
set non-stop off
set target-async off
attach PIDHERE
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
# Body-armor visual builder FUN_008b9ca0. At 0x008b9d05 (MOV [ECX+0x10],EAX): EAX = the resolved
# meshindex id (runtime attr 0x2da), EBX = attr 0x30b (secondary). Type is 0xf696e (MESHINDEXFILE).
# Fires once per avatar (re)build — re-equip in-world to trigger; cache of mesh DATA is irrelevant.
break *0x008b9d05
commands
  silent
  printf "ARMORBUILD meshindex=%u (0x%x)  attr30b=%u  ecx=0x%x\n", $eax, $eax, $ebx, $ecx
  continue
end
# Sibling builders for other slots/bodyparts — dump EAX/EBX at each so we don't miss the gown's slot.
break *0x008b9d95
commands
  silent
  printf "BUILD_8b9d40 eax=%u ebx=%u\n", $eax, $ebx
  continue
end
break *0x008b005d
commands
  silent
  printf "BUILD_8afd00 eax=%u ebx=%u\n", $eax, $ebx
  continue
end
break *0x008b01e8
commands
  silent
  printf "BUILD_8b00b0 eax=%u ebx=%u\n", $eax, $ebx
  continue
end
break *0x008b05c5
commands
  silent
  printf "BUILD_8b0370 eax=%u ebx=%u\n", $eax, $ebx
  continue
end
break *0x008b0653
commands
  silent
  printf "BUILD_8b0600 eax=%u ebx=%u\n", $eax, $ebx
  continue
end
break *0x0080ad2a
commands
  silent
  printf "BUILD_80acd0 eax=%u ebx=%u\n", $eax, $ebx
  continue
end
echo READY_ARMORBUILD\n
continue
