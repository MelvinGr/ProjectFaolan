set pagination off
set confirm off
set height 0
set logging file /tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/b0778add-a126-477b-8758-2620da06bf1b/scratchpad/builders.log
set logging overwrite on
set logging enabled on
attach 1858810
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIG34 nostop noprint pass
handle SIGPWR nostop noprint pass
# At each "MOV [reg+0x14],0xf696e" (build a meshindex resource ref), the meshindex id is in a GP register
# (EAX for the 008b9ca0 body-armour builder). Dump all GP regs; the armour mesh id is identifiable among them.
break *0x008b9cfe
break *0x008b9d95
break *0x008b005d
break *0x008b01e8
break *0x008b05c5
break *0x008b0653
break *0x0080ad2a
commands 1
  silent
  printf "B_8b9ca0 eax=%u ebx=%u ecx=0x%x edx=%u esi=%u edi=%u\n", $eax,$ebx,$ecx,$edx,$esi,$edi
  continue
end
commands 2
  silent
  printf "B_8b9d40 eax=%u ebx=%u ecx=0x%x edx=%u esi=%u edi=%u\n", $eax,$ebx,$ecx,$edx,$esi,$edi
  continue
end
commands 3
  silent
  printf "B_8afd00 eax=%u ebx=%u ecx=0x%x edx=%u esi=%u edi=%u\n", $eax,$ebx,$ecx,$edx,$esi,$edi
  continue
end
commands 4
  silent
  printf "B_8b00b0 eax=%u ebx=%u ecx=0x%x edx=%u esi=%u edi=%u\n", $eax,$ebx,$ecx,$edx,$esi,$edi
  continue
end
commands 5
  silent
  printf "B_8b0370 eax=%u ebx=%u ecx=0x%x edx=%u esi=%u edi=%u\n", $eax,$ebx,$ecx,$edx,$esi,$edi
  continue
end
commands 6
  silent
  printf "B_8b0600 eax=%u ebx=%u ecx=0x%x edx=%u esi=%u edi=%u\n", $eax,$ebx,$ecx,$edx,$esi,$edi
  continue
end
commands 7
  silent
  printf "B_80acd0 eax=%u ebx=%u ecx=0x%x edx=%u esi=%u edi=%u\n", $eax,$ebx,$ecx,$edx,$esi,$edi
  continue
end
echo READY_BUILDERS\n
continue
