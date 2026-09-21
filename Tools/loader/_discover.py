OUT = r'C:\Users\Gaming\AppData\Local\Temp\probe_out.txt'
f = open(OUT, 'w')
def w(s):
    f.write(str(s) + '\n'); f.flush()
import unreal_engine as ue
bp = ue.find_asset('/Game/MCDReborn/UMG_MCDRebornLoader') or ue.load_object(ue.find_class('WidgetBlueprint'), '/Game/MCDReborn/UMG_MCDRebornLoader')
w('widget: %s' % bp)
page = bp.UberGraphPages[0]
for i, n in enumerate(page.Nodes):
    try:
        title = n.node_get_title()
    except Exception as e:
        title = '<no title: %s>' % e
    try:
        cls = n.get_class().get_name()
    except Exception:
        cls = '?'
    pins = []
    try:
        for p in n.node_pins():
            links = len(p.get_linked_to())
            pins.append('%s(%s,%d)' % (p.name, p.category, links))
    except Exception as e:
        pins = ['<pins failed %s>' % e]
    w('[%02d] %-28s | %-34s | %s' % (i, cls, title.replace('\n',' '), ', '.join(pins)))
w('done')
f.close()
