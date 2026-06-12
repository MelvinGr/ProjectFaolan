#!/usr/bin/env python3
"""Decode the Tradepost (auction house) + mail RPC traffic from a capture JSON.

Framing (cracked 2026-06-12): every GameServer RPC packet is
  [len:u32][callId:u32][innerLen:u32][ RpcHeader protobuf ][ method:u16 (0x20xx) ][ body ]
RpcHeader = protobuf { field1 = Receiver endpoint, field2 = Sender endpoint },
each endpoint = ProtoEndpoint { 0x0d fixed32 classId (LITTLE-endian on wire), 0x10 varint instance }.

Tradepost classes: TradepostAgent 0x3D744981 (client->server), TradepostInterface 0xF2C2D5B5 (server->client).
"""
import json, base64, sys, struct

AGENT = 0x3D744981   # TradepostAgent  (client -> server)
IFACE = 0xF2C2D5B5   # TradepostInterface (server -> client)
AGENT_LE = struct.pack('<I', AGENT)
IFACE_LE = struct.pack('<I', IFACE)

def read_varint(b, i):
    shift = 0; val = 0
    while True:
        c = b[i]; i += 1
        val |= (c & 0x7f) << shift
        if not (c & 0x80): break
        shift += 7
    return val, i

def parse_endpoint(b):
    """ProtoEndpoint sub-message bytes -> (classId, instance)."""
    i = 0; cls = inst = None
    while i < len(b):
        tag = b[i]; i += 1
        field = tag >> 3; wt = tag & 7
        if wt == 5:      # fixed32
            cls = struct.unpack('<I', b[i:i+4])[0]; i += 4
        elif wt == 0:    # varint
            inst, i = read_varint(b, i)
        elif wt == 2:
            ln, i = read_varint(b, i); i += ln
        else: break
    return cls, inst

def parse_rpc(raw):
    """-> dict(callId, receiver=(cls,inst), sender=(cls,inst), method, body)."""
    if len(raw) < 12: return None
    total = struct.unpack('>I', raw[0:4])[0]
    callId = struct.unpack('>I', raw[4:8])[0]
    inner = struct.unpack('>I', raw[8:12])[0]
    p = raw[12:12+inner]
    i = 0; recv = send = None
    while i < len(p):
        tag = p[i]
        if tag in (0x0a, 0x12):  # field1/field2, wiretype 2
            i += 1; ln, i = read_varint(p, i)
            ep = parse_endpoint(p[i:i+ln]); i += ln
            if tag == 0x0a: recv = ep
            else: send = ep
        else:
            break
    method = struct.unpack('>H', p[i:i+2])[0] if i+2 <= len(p) else None
    body = raw[12+inner:]   # payload follows the RpcHeader+method region
    return dict(callId=callId, recv=recv, send=send, method=method, body=body)

# Method map (recovered 2026-06-12 by behaviour across the sell + buy/mail captures).
# These 0x20xx are per-class method selectors, not GameServer transport opcodes.
AGENT_METHODS = {  # C->S TradepostAgent
    0x2011: 'TradepostOpen', 0x200D: 'TradepostItemSearch', 0x200E: 'TradepostBuyItem',
    0x2010: 'TradepostClose', 0x2001: 'GetMail?', 0x2007: 'GetMailItems?', 0x2006: 'GetMailItem?',
    0x2003: 'Delete/MarkMail?', 0x2000: 'mail-poll?',
}
IFACE_METHODS = {  # S->C TradepostInterface
    0x2000: 'DispatchOpenTradepost', 0x2005: 'DispatchTradepostSearchResults',
    0x2002: 'DispatchMailMessage', 0x2004: 'ack', 0x2001: 'DispatchClose',
}

def method_name(method, dl):
    return (IFACE_METHODS if dl else AGENT_METHODS).get(method, '?')

def parse_search_query(b):
    """TradepostItemSearch body."""
    ln = struct.unpack('>H', b[0:2])[0]
    lang = b[2:2+ln].decode('latin1'); i = 2+ln
    cat = struct.unpack('>I', b[i:i+4])[0]; i += 4
    page = struct.unpack('>I', b[i:i+4])[0]; i += 4
    cat2 = struct.unpack('>I', b[i:i+4])[0]; i += 4
    itemId = struct.unpack('>I', b[i:i+4])[0]; i += 4
    rest = b[i:]
    return f'lang={lang} cat={cat} page={page} cat2={cat2} itemId=0x{itemId:04X} filter+price={rest.hex()}'

def parse_first_result_row(b):
    """First row of DispatchTradepostSearchResults (offsets still being grammared)."""
    head = struct.unpack('>I', b[0:4])[0]
    saleId = b[4:12].hex()
    mid = b[12:16].hex()
    nl = struct.unpack('>H', b[16:18])[0]
    seller = b[18:18+nl].decode('latin1', 'replace'); i = 18+nl
    # after the name: padding to fixed width, then price/qty/itemId
    return f'head=0x{head:08X} saleId={saleId} h={mid} seller="{seller}" tail={b[i:i+24].hex()}'

def parse_mail(b):
    """DispatchMailMessage body: mailId, recipient endpoint, sender endpoint, remoteformat body.
    For an auction-house delivery the SENDER is empty (system mail), recipient = the buyer."""
    mailId = b[0:8].hex(); i = 8
    def ep(i):
        cls = b[i:i+4].hex(); i += 4
        nl = struct.unpack('>H', b[i:i+2])[0]; i += 2
        s = b[i:i+nl].decode('latin1', 'replace'); i += nl
        return (cls, s), i
    recip, i = ep(i)
    sender, i = ep(i)
    return (f'mailId={mailId} recipient="{recip[1]}"(cls={recip[0]}) sender="{sender[1]}"(cls={sender[0]}) '
            f'body~={b[i:i+48].decode("latin1","replace")!r}')

def is_tradepost(raw):
    return AGENT_LE in raw or IFACE_LE in raw

def main(path):
    d = json.load(open(path))
    P = d['Packets']
    rows = []
    for idx, pk in enumerate(P):
        if pk['ProtocolName'] != 'GameServer': continue
        raw = base64.b64decode(pk['RawBytesBase64'])
        if not is_tradepost(raw): continue
        r = parse_rpc(raw)
        if not r: continue
        r['idx'] = idx; r['dl'] = pk['IsDownload']
        rows.append(r)
    print(f'{len(rows)} tradepost RPC packets\n')
    def nm(cls):
        return {AGENT:'TPAgent', IFACE:'TPIface'}.get(cls, f'0x{cls:08X}' if cls else '-')
    for r in rows:
        d_ = 'S->C' if r['dl'] else 'C->S'
        mn = method_name(r['method'], r['dl'])
        b = r['body']
        detail = b[:40].hex()
        try:
            if mn == 'TradepostItemSearch': detail = parse_search_query(b)
            elif mn == 'DispatchTradepostSearchResults' and len(b) > 20: detail = parse_first_result_row(b)
            elif mn == 'DispatchMailMessage': detail = parse_mail(b)
        except Exception as e:
            detail = f'<parse err {e}> ' + b[:32].hex()
        print(f"#{r['idx']:5} {d_} 0x{r['method']:04X} {mn:32} len={len(b):5}  {detail}")

if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else
         'account-faolan-character-necrol80-login-sell_items_to_trader-intract-with-auctionhouse.json')
