set pagination off
set confirm off
set height 0
set logging file /tmp/armor_capture3.log
set logging overwrite on
set logging enabled on
attach PIDHERE
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIG34 nostop noprint pass
handle SIGPWR nostop noprint pass
# Every meshindex (type 0x f696e) load via the visual loader, with caller (id@+0x10, type@+0x14, this=ECX).
break *0x005b07e0
commands
  silent
  if *(unsigned int*)($ecx+0x14) == 0xf696e
    printf "MESHIDX id=%u caller=0x%x\n", *(unsigned int*)($ecx+0x10), *(unsigned int*)($esp)
  end
  continue
end
echo READY_ARMOR3\n
continue
