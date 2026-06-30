set pagination off
set confirm off
set height 0
set logging file /tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/b0778add-a126-477b-8758-2620da06bf1b/scratchpad/armor_capture.log
set logging overwrite on
set logging enabled on
attach 1854199
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIG34 nostop noprint pass
handle SIGPWR nostop noprint pass
# FUN_008b9ca0 body-armour builder: at 0x008b9d05 EAX = getAttr(item,0x2da) = armour meshindex id,
# EBX = getAttr(item,0x30b) = secondary (material/look) id, [ECX+0x14]=0xf696e (meshindex type).
break *0x008b9d05
commands
  silent
  printf "ARMORMESH meshindex=%u (0x%x)  attr0x30b=%u  type=0x%x\n", $eax, $eax, $ebx, *(unsigned int*)($ecx+0x14)
  continue
end
echo READY_ARMOR_CAPTURE\n
continue
