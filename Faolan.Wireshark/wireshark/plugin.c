#ifdef HAVE_CONFIG_H
#include "config.h"
#endif

#include <stdint.h>

#include <epan/proto.h>
#include <wireshark.h>
#include <ws_version.h>
#include <wsutil/plugins.h>

#include "packet-faolan-gameserver-zlib.h"

#ifndef FAOLAN_PLUGIN_VERSION
#define FAOLAN_PLUGIN_VERSION "0.1.0"
#endif

WS_DLL_PUBLIC_DEF const char plugin_version[] = FAOLAN_PLUGIN_VERSION;
WS_DLL_PUBLIC_DEF const int plugin_want_major = WIRESHARK_VERSION_MAJOR;
WS_DLL_PUBLIC_DEF const int plugin_want_minor = WIRESHARK_VERSION_MINOR;

WS_DLL_PUBLIC void plugin_register(void)
{
    static proto_plugin plug;

    plug.register_protoinfo = proto_register_faolan_gs_zlib;
    plug.register_handoff = proto_reg_handoff_faolan_gs_zlib;
    proto_register_plugin(&plug);
}

WS_DLL_PUBLIC uint32_t plugin_describe(void)
{
    return WS_PLUGIN_DESC_DISSECTOR | WS_PLUGIN_DESC_EPAN;
}
