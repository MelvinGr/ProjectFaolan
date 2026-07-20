#!/usr/bin/env python3
"""Decode the Tradepost (auction house) + mail RPC traffic from a capture JSON.

Framing (cracked 2026-06-12): every GameServer RPC packet is
  [len:u32][callId:u32][innerLen:u32][ RpcHeader protobuf ][ method:u16 (0x20xx) ][ body ]
RpcHeader = protobuf { field1 = Receiver endpoint, field2 = Sender endpoint },
each endpoint = ProtoEndpoint { 0x0d fixed32 classId (LITTLE-endian on wire), 0x10 varint instance }.

Tradepost classes: TradepostAgent 0x3D744981 (client->server), TradepostInterface 0xF2C2D5B5 (server->client).
"""
import json, sys, struct

AGENT = 0x3D744981   # TradepostAgent  (client -> server)
IFACE = 0xF2C2D5B5   # TradepostInterface (server -> client)
# The .tshark.json dump already exposes the RPC endpoint class ids + opcode + de-framed body, so the packet
# is selected by its SenderClass/ReceiverClass instead of scanning the raw frame for the little-endian tag.
TP_CLASSES = {f'0x{AGENT:08x}', f'0x{IFACE:08x}'}

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

SALE_PREFIX = bytes.fromhex('20d82b')   # session-constant high bytes of every saleId in these captures

def parse_result_row(b):
    """One DispatchTradepostSearchResults row (the item ProtoAcgItem trails, variable-length)."""
    saleId = struct.unpack('>Q', b[0:8])[0]
    handle = struct.unpack('>I', b[8:12])[0]
    nl = struct.unpack('>H', b[12:14])[0]
    seller = b[14:14+nl].decode('latin1', 'replace'); i = 14+nl
    price = struct.unpack('>Q', b[i:i+8])[0]; i += 8
    qty = struct.unpack('>I', b[i:i+4])[0]; i += 4
    itemId = struct.unpack('>I', b[i:i+4])[0]; i += 4
    return dict(saleId=saleId, handle=handle, seller=seller, price=price, qty=qty, itemId=itemId, protoOff=i)

def parse_results(b):
    """[head:u32][row]* — split rows on the shared saleId prefix."""
    head = struct.unpack('>I', b[0:4])[0]
    starts = [k for k in range(4, len(b)-8) if b[k:k+3] == SALE_PREFIX]
    starts.append(len(b))
    rows = []
    for k in range(len(starts)-1):
        seg = b[starts[k]:starts[k+1]]
        try: rows.append(parse_result_row(seg))
        except Exception: pass
    return head, rows

def parse_first_result_row(b):
    head, rows = parse_results(b)
    if not rows: return f'head=0x{head:08X} <no rows>'
    r = rows[0]
    extra = '' if len(rows) == 1 else f' (+{len(rows)-1} more rows)'
    return (f'head=0x{head:08X} rows={len(rows)} | row0: seller="{r["seller"]}" '
            f'price={r["price"]} qty={r["qty"]} itemId=0x{r["itemId"]:04X} saleId=0x{r["saleId"]:016X}{extra}')

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

def main(path):
    d = json.load(open(path))
    P = d['Packets']
    rows = []
    for idx, pk in enumerate(P):
        sc = (pk.get('SenderClass') or '').lower()
        rc = (pk.get('ReceiverClass') or '').lower()
        if sc not in TP_CLASSES and rc not in TP_CLASSES: continue
        if not pk.get('Opcode'): continue
        rows.append(dict(idx=idx, dl=pk['IsDownload'],
                         method=int(pk['Opcode'], 16),
                         body=bytes.fromhex(pk.get('BodyHex') or '')))
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
    import os
    default = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           'NecroL80_mage-necromancer_login_sell_items_to_trader-intract-with-auctionhouse.tshark.json')
    main(sys.argv[1] if len(sys.argv) > 1 else default)
