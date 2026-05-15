"""
USA Residential Floor Plan Reference Library
20 plans in AutoCAD-standard black-and-white drafting style
ReportLab canvas — all geometry drawn as linework
"""
import json
import math
from reportlab.lib.pagesizes import landscape
from reportlab.lib.units import inch
from reportlab.pdfgen import canvas as rl_canvas
from reportlab.lib import colors

PAGE = landscape((17*inch, 11*inch))
PW, PH = PAGE
MARGIN = 0.5*inch

LW_WALL       = 2.0
LW_WALL_INT   = 1.2
LW_DIM        = 0.5
LW_THIN       = 0.4
LW_SITE       = 1.0
LW_SETBACK    = 0.6

BLACK = colors.black

OX = MARGIN + 30
OY = MARGIN + 30

def fp(feet): return feet * 6.0

def dim_line(c, x1, y1, x2, y2, label, side='top', offset=12, fontsize=5):
    c.setLineWidth(LW_DIM)
    c.setStrokeColor(BLACK)
    c.setFillColor(BLACK)
    if abs(x2-x1) > abs(y2-y1):
        ey = y1 + offset if side == 'top' else y1 - offset
        c.line(x1, y1, x1, ey)
        c.line(x2, y2, x2, ey)
        c.line(x1, ey, x2, ey)
        c.line(x1-2, ey-3, x1+2, ey+3)
        c.line(x2-2, ey-3, x2+2, ey+3)
        mx = (x1+x2)/2
        c.setFont("Helvetica", fontsize)
        c.drawCentredString(mx, ey+2, label)
    else:
        ex = x1 + offset if side == 'right' else x1 - offset
        c.line(x1, y1, ex, y1)
        c.line(x2, y2, ex, y2)
        c.line(ex, y1, ex, y2)
        c.line(ex-3, y1-2, ex+3, y1+2)
        c.line(ex-3, y2-2, ex+3, y2+2)
        my = (y1+y2)/2
        c.saveState()
        c.translate(ex-2, my)
        c.rotate(90)
        c.setFont("Helvetica", fontsize)
        c.drawCentredString(0, 0, label)
        c.restoreState()

def room_label(c, cx, cy, name, area_sf=None, fontsize=6):
    c.setFillColor(BLACK)
    c.setFont("Helvetica-Bold", fontsize)
    c.drawCentredString(cx, cy + (4 if area_sf else 0), name)
    if area_sf:
        c.setFont("Helvetica", fontsize-0.5)
        c.drawCentredString(cx, cy - 4, f"({area_sf} SF)")

def door_swing(c, hinge_x, hinge_y, w, angle_deg=90, facing='right'):
    c.setLineWidth(LW_THIN)
    r = w
    if facing == 'right':
        c.line(hinge_x, hinge_y, hinge_x + w, hinge_y)
        c.arc(hinge_x, hinge_y - r, hinge_x + r, hinge_y + r*0, startAng=0, extent=angle_deg)
    elif facing == 'left':
        c.line(hinge_x, hinge_y, hinge_x - w, hinge_y)
        c.arc(hinge_x - r, hinge_y, hinge_x, hinge_y + r, startAng=270, extent=-angle_deg)
    elif facing == 'up':
        c.line(hinge_x, hinge_y, hinge_x, hinge_y + w)
        c.arc(hinge_x, hinge_y, hinge_x + r, hinge_y + r, startAng=180, extent=-angle_deg)
    elif facing == 'down':
        c.line(hinge_x, hinge_y, hinge_x, hinge_y - w)
        c.arc(hinge_x - r, hinge_y - r, hinge_x, hinge_y, startAng=0, extent=angle_deg)

def window_sym(c, x1, y1, x2, y2, horizontal=True):
    c.setLineWidth(LW_THIN)
    if horizontal:
        mid = (y1+y2)/2
        gap = 3
        c.line(x1, mid-gap, x2, mid-gap)
        c.line(x1, mid+gap, x2, mid+gap)
    else:
        mid = (x1+x2)/2
        gap = 3
        c.line(mid-gap, y1, mid-gap, y2)
        c.line(mid+gap, y1, mid+gap, y2)

def north_arrow(c, cx, cy, r=12):
    c.setLineWidth(0.5)
    c.circle(cx, cy, r, stroke=1, fill=0)
    c.line(cx, cy-r+2, cx, cy+r-2)
    p = c.beginPath()
    p.moveTo(cx, cy+r-2)
    p.lineTo(cx-4, cy)
    p.lineTo(cx, cy+2)
    p.close()
    c.drawPath(p, stroke=1, fill=1)
    c.setFont("Helvetica-Bold", 7)
    c.drawCentredString(cx, cy+r+3, "N")

def scale_bar(c, x, y, plan_scale, bar_width_ft=20, seg=4):
    seg_pt = 18
    c.setLineWidth(0.5)
    for i in range(seg):
        sx = x + i*seg_pt
        if i % 2 == 0:
            p = c.beginPath()
            p.rect(sx, y, seg_pt, 4)
            c.drawPath(p, stroke=1, fill=1)
        else:
            c.rect(sx, y, seg_pt, 4, stroke=1, fill=0)
    c.setFont("Helvetica", 4.5)
    c.drawCentredString(x, y-4, "0")
    c.drawCentredString(x + 2*seg_pt, y-4, f"{bar_width_ft//2}'")
    c.drawCentredString(x + 4*seg_pt, y-4, f"{bar_width_ft}'")
    c.drawCentredString(x + 2*seg_pt, y+8, f"SCALE: 1/4\" = 1'-0\"")

def title_block(c, plan_num, plan_name, sq_ft, plan_type, sheet_num, total_sheets):
    bx = PW - 1.8*inch
    by = MARGIN
    bw = 1.8*inch - 4
    bh = PH - 2*MARGIN
    c.setLineWidth(0.8)
    c.rect(bx, by, bw, bh, stroke=1, fill=0)
    c.line(bx, by+bh-0.5*inch, bx+bw, by+bh-0.5*inch)
    c.line(bx, by+bh-1.0*inch, bx+bw, by+bh-1.0*inch)
    c.line(bx, by+bh-1.5*inch, bx+bw, by+bh-1.5*inch)
    c.line(bx, by+bh-2.0*inch, bx+bw, by+bh-2.0*inch)
    c.line(bx, by+0.6*inch, bx+bw, by+0.6*inch)
    c.setFont("Helvetica-Bold", 7)
    c.drawCentredString(bx+bw/2, by+bh-0.28*inch, "ZHEIGHT AI")
    c.setFont("Helvetica", 5.5)
    c.drawCentredString(bx+bw/2, by+bh-0.4*inch, "ARCHITECTURE + PLANNING")
    c.setFont("Helvetica-Bold", 6)
    c.drawCentredString(bx+bw/2, by+bh-0.72*inch, plan_name.upper())
    c.setFont("Helvetica", 5.5)
    c.drawCentredString(bx+bw/2, by+bh-0.84*inch, f"{sq_ft:,} SF  |  {plan_type}")
    c.setFont("Helvetica-Bold", 6)
    c.drawCentredString(bx+bw/2, by+bh-1.22*inch, "FLOOR PLAN")
    c.setFont("Helvetica", 5)
    c.drawCentredString(bx+bw/2, by+bh-1.34*inch, f"PLAN #{plan_num:02d}")
    c.setFont("Helvetica-Bold", 5.5)
    c.drawString(bx+4, by+bh-1.62*inch, "GENERAL NOTES:")
    notes = [
        "1. ALL DIMS TO FACE OF FRAMING.",
        "2. VERIFY ALL DIMS IN FIELD.",
        "3. ALL CONST. PER LOCAL CODE.",
        "4. VERIFY EXISTING CONDITIONS.",
        "5. SEE STRUCT. FOR BEAMS/HDRS.",
        "6. HAB. ROOMS: MIN 7'-0\" CLG HT.",
        "   PER IRC R305.1",
        "7. MIN STAIR WIDTH: 36\" PER IRC.",
        "8. SMOKE DETECTORS PER IRC.",
        "9. EGRESS WINDOWS PER IRC R310.",
        "10. HANDRAIL REQD > 4 RISERS.",
        "11. ALL ELEC. PER NEC.",
        "12. ALL PLBG. PER UPC.",
    ]
    c.setFont("Helvetica", 4.2)
    for i, note in enumerate(notes):
        c.drawString(bx+4, by+bh-1.82*inch - i*7, note)
    c.setFont("Helvetica-Bold", 9)
    c.drawCentredString(bx+bw/2, by+0.22*inch, f"A{sheet_num}.0")
    c.setFont("Helvetica", 5)
    c.drawCentredString(bx+bw/2, by+0.10*inch, f"SHEET {sheet_num} OF {total_sheets}")

def border(c):
    c.setLineWidth(1.5)
    c.rect(MARGIN, MARGIN, PW-2*MARGIN-1.8*inch, PH-2*MARGIN, stroke=1, fill=0)
    c.setLineWidth(0.5)
    c.line(MARGIN, MARGIN+18, PW-2*MARGIN-1.8*inch, MARGIN+18)

def plan_header(c, title, scale_txt="1/4\" = 1'-0\""):
    c.setFont("Helvetica-Bold", 9)
    c.setFillColor(BLACK)
    tx = MARGIN + 8
    ty = PH - MARGIN - 16
    c.drawString(tx, ty, title)
    c.setFont("Helvetica", 7)
    c.drawString(tx, ty-10, f"SCALE: {scale_txt}")
    c.setLineWidth(1.0)
    c.line(tx, ty-13, tx+200, ty-13)


PLANS = [
{
 "num":1,"name":"2BR/1BA RANCH","sf":1050,"type":"Ranch / Single Story",
 "lot_w":50,"lot_d":100,
 "setbacks":{"front":20,"rear":20,"left":5,"right":5},
 "house_w":40,"house_d":30,
 "rooms":[
   {"n":"LIVING ROOM","x":0,"y":12,"w":16,"h":12,"sf":192},
   {"n":"DINING","x":16,"y":12,"w":10,"h":12,"sf":120},
   {"n":"KITCHEN","x":26,"y":12,"w":14,"h":12,"sf":168},
   {"n":"BEDROOM 1","x":0,"y":0,"w":14,"h":12,"sf":168},
   {"n":"BEDROOM 2","x":14,"y":0,"w":12,"h":12,"sf":144},
   {"n":"BATHROOM","x":26,"y":0,"w":8,"h":12,"sf":96},
   {"n":"LAUNDRY","x":34,"y":0,"w":6,"h":6,"sf":36},
   {"n":"HALL","x":34,"y":6,"w":6,"h":6,"sf":36},
   {"n":"GARAGE","x":0,"y":24,"w":20,"h":14,"sf":280},
   {"n":"PORCH","x":20,"y":24,"w":20,"h":6,"sf":120},
 ],
 "doors":[{"x":6,"y":24,"facing":"up"},{"x":18,"y":12,"facing":"down"},{"x":6,"y":0,"facing":"down"},{"x":20,"y":0,"facing":"down"},{"x":30,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":24,"x2":8,"y2":24,"h":True},{"x1":2,"y1":0,"x2":8,"y2":0,"h":True},{"x1":16,"y1":0,"x2":22,"y2":0,"h":True},{"x1":0,"y1":14,"x2":0,"y2":20,"h":False}],
 "dim_h":[{"x1":0,"x2":16,"y":25,"lbl":"16'-0\""},{"x1":16,"x2":26,"y":25,"lbl":"10'-0\""},{"x1":26,"x2":40,"y":25,"lbl":"14'-0\""},{"x1":0,"x2":40,"y":30,"lbl":"40'-0\""}],
 "dim_v":[{"y1":0,"y2":12,"x":42,"lbl":"12'-0\""},{"y1":12,"y2":24,"x":42,"lbl":"12'-0\""},{"y1":24,"y2":38,"x":42,"lbl":"14'-0\""},{"y1":0,"y2":38,"x":46,"lbl":"38'-0\""}],
},
{
 "num":2,"name":"3BR/2BA RANCH","sf":1450,"type":"Ranch / Single Story",
 "lot_w":60,"lot_d":110,
 "setbacks":{"front":20,"rear":20,"left":5,"right":5},
 "house_w":50,"house_d":32,
 "rooms":[
   {"n":"GREAT ROOM","x":0,"y":12,"w":24,"h":14,"sf":336},
   {"n":"KITCHEN","x":24,"y":20,"w":14,"h":8,"sf":112},
   {"n":"DINING","x":24,"y":12,"w":14,"h":8,"sf":112},
   {"n":"MASTER BED","x":38,"y":16,"w":12,"h":12,"sf":144},
   {"n":"MASTER BATH","x":38,"y":12,"w":12,"h":4,"sf":48},
   {"n":"BEDROOM 2","x":0,"y":0,"w":12,"h":12,"sf":144},
   {"n":"BEDROOM 3","x":12,"y":0,"w":12,"h":12,"sf":144},
   {"n":"BATH 2","x":24,"y":0,"w":10,"h":8,"sf":80},
   {"n":"LAUNDRY","x":34,"y":0,"w":8,"h":8,"sf":64},
   {"n":"HALL","x":24,"y":8,"w":14,"h":4,"sf":56},
   {"n":"GARAGE","x":0,"y":26,"w":22,"h":14,"sf":308},
   {"n":"COVERED PORCH","x":22,"y":32,"w":28,"h":6,"sf":168},
 ],
 "doors":[{"x":8,"y":26,"facing":"up"},{"x":30,"y":32,"facing":"up"},{"x":6,"y":0,"facing":"down"},{"x":18,"y":0,"facing":"down"},{"x":40,"y":16,"facing":"down"}],
 "windows":[{"x1":4,"y1":26,"x2":10,"y2":26,"h":True},{"x1":0,"y1":14,"x2":0,"y2":22,"h":False},{"x1":2,"y1":0,"x2":8,"y2":0,"h":True},{"x1":14,"y1":0,"x2":20,"y2":0,"h":True},{"x1":42,"y1":22,"x2":48,"y2":22,"h":True}],
 "dim_h":[{"x1":0,"x2":24,"y":27,"lbl":"24'-0\""},{"x1":24,"x2":38,"y":27,"lbl":"14'-0\""},{"x1":38,"x2":50,"y":27,"lbl":"12'-0\""},{"x1":0,"x2":50,"y":32,"lbl":"50'-0\""}],
 "dim_v":[{"y1":0,"y2":12,"x":52,"lbl":"12'-0\""},{"y1":12,"y2":26,"x":52,"lbl":"14'-0\""},{"y1":26,"y2":40,"x":52,"lbl":"14'-0\""},{"y1":0,"y2":40,"x":57,"lbl":"40'-0\""}],
},
{
 "num":3,"name":"3BR/2BA CONTEMPORARY","sf":1800,"type":"Contemporary / Open Plan",
 "lot_w":60,"lot_d":120,
 "setbacks":{"front":20,"rear":15,"left":5,"right":5},
 "house_w":45,"house_d":42,
 "rooms":[
   {"n":"OPEN LIVING/DINING","x":0,"y":22,"w":30,"h":20,"sf":600},
   {"n":"KITCHEN","x":30,"y":30,"w":15,"h":12,"sf":180},
   {"n":"PANTRY","x":30,"y":22,"w":8,"h":8,"sf":64},
   {"n":"ENTRY","x":38,"y":22,"w":7,"h":8,"sf":56},
   {"n":"MASTER SUITE","x":0,"y":10,"w":18,"h":12,"sf":216},
   {"n":"M. BATH","x":18,"y":10,"w":10,"h":12,"sf":120},
   {"n":"W.I.C.","x":28,"y":10,"w":8,"h":6,"sf":48},
   {"n":"BEDROOM 2","x":0,"y":0,"w":14,"h":10,"sf":140},
   {"n":"BEDROOM 3","x":14,"y":0,"w":14,"h":10,"sf":140},
   {"n":"BATH 2","x":28,"y":0,"w":10,"h":10,"sf":100},
   {"n":"LAUNDRY","x":38,"y":0,"w":7,"h":10,"sf":70},
   {"n":"GARAGE","x":0,"y":42,"w":22,"h":14,"sf":308},
   {"n":"OUTDOOR LIVING","x":22,"y":42,"w":23,"h":8,"sf":184},
 ],
 "doors":[{"x":40,"y":30,"facing":"up"},{"x":8,"y":22,"facing":"down"},{"x":4,"y":10,"facing":"down"},{"x":4,"y":0,"facing":"down"},{"x":18,"y":0,"facing":"down"}],
 "windows":[{"x1":0,"y1":24,"x2":0,"y2":36,"h":False},{"x1":2,"y1":42,"x2":12,"y2":42,"h":True},{"x1":24,"y1":42,"x2":38,"y2":42,"h":True},{"x1":2,"y1":0,"x2":10,"y2":0,"h":True},{"x1":16,"y1":0,"x2":24,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":30,"y":44,"lbl":"30'-0\""},{"x1":30,"x2":45,"y":44,"lbl":"15'-0\""},{"x1":0,"x2":45,"y":49,"lbl":"45'-0\""}],
 "dim_v":[{"y1":0,"y2":10,"x":47,"lbl":"10'-0\""},{"y1":10,"y2":22,"x":47,"lbl":"12'-0\""},{"y1":22,"y2":42,"x":47,"lbl":"20'-0\""},{"y1":42,"y2":56,"x":47,"lbl":"14'-0\""},{"y1":0,"y2":56,"x":52,"lbl":"56'-0\""}],
},
{
 "num":4,"name":"4BR/3BA COLONIAL - FIRST FLOOR","sf":2400,"type":"Two-Story / Colonial",
 "lot_w":75,"lot_d":130,
 "setbacks":{"front":25,"rear":20,"left":7,"right":7},
 "house_w":50,"house_d":26,
 "rooms":[
   {"n":"FOYER","x":20,"y":18,"w":10,"h":8,"sf":80},
   {"n":"LIVING ROOM","x":0,"y":16,"w":18,"h":10,"sf":180},
   {"n":"DINING ROOM","x":0,"y":10,"w":18,"h":6,"sf":108},
   {"n":"KITCHEN","x":18,"y":10,"w":14,"h":12,"sf":168},
   {"n":"FAMILY ROOM","x":32,"y":10,"w":18,"h":16,"sf":288},
   {"n":"BREAKFAST","x":18,"y":18,"w":2,"h":4,"sf":24},
   {"n":"STUDY","x":32,"y":0,"w":12,"h":10,"sf":120},
   {"n":"HALF BATH","x":18,"y":6,"w":6,"h":4,"sf":24},
   {"n":"LAUNDRY","x":24,"y":0,"w":8,"h":10,"sf":80},
   {"n":"MUDROOM","x":44,"y":0,"w":6,"h":10,"sf":60},
   {"n":"GARAGE (2-CAR)","x":0,"y":26,"w":24,"h":14,"sf":336},
   {"n":"COVERED ENTRY","x":22,"y":26,"w":8,"h":6,"sf":48},
 ],
 "doors":[{"x":24,"y":26,"facing":"up"},{"x":26,"y":26,"facing":"up"},{"x":24,"y":18,"facing":"up"},{"x":34,"y":10,"facing":"down"},{"x":4,"y":10,"facing":"down"},{"x":34,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":26,"x2":14,"y2":26,"h":True},{"x1":0,"y1":18,"x2":0,"y2":24,"h":False},{"x1":0,"y1":11,"x2":0,"y2":16,"h":False},{"x1":36,"y1":26,"x2":48,"y2":26,"h":True},{"x1":36,"y1":0,"x2":44,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":18,"y":28,"lbl":"18'-0\""},{"x1":18,"x2":32,"y":28,"lbl":"14'-0\""},{"x1":32,"x2":50,"y":28,"lbl":"18'-0\""},{"x1":0,"x2":50,"y":33,"lbl":"50'-0\""}],
 "dim_v":[{"y1":0,"y2":10,"x":52,"lbl":"10'-0\""},{"y1":10,"y2":26,"x":52,"lbl":"16'-0\""},{"y1":26,"y2":40,"x":52,"lbl":"14'-0\""},{"y1":0,"y2":40,"x":57,"lbl":"40'-0\""}],
},
{
 "num":5,"name":"EICHLER ATRIUM HOME","sf":1700,"type":"Eichler / Mid-Century Modern",
 "lot_w":65,"lot_d":115,
 "setbacks":{"front":20,"rear":20,"left":5,"right":5},
 "house_w":52,"house_d":36,
 "rooms":[
   {"n":"ATRIUM","x":16,"y":12,"w":20,"h":12,"sf":240},
   {"n":"LIVING","x":0,"y":12,"w":16,"h":24,"sf":384},
   {"n":"DINING","x":0,"y":0,"w":16,"h":12,"sf":192},
   {"n":"KITCHEN","x":16,"y":0,"w":16,"h":12,"sf":192},
   {"n":"MASTER BED","x":36,"y":18,"w":16,"h":18,"sf":288},
   {"n":"MASTER BATH","x":36,"y":12,"w":16,"h":6,"sf":96},
   {"n":"BEDROOM 2","x":32,"y":0,"w":10,"h":12,"sf":120},
   {"n":"BEDROOM 3","x":42,"y":0,"w":10,"h":12,"sf":120},
   {"n":"BATH 2","x":32,"y":12,"w":4,"h":12,"sf":48},
   {"n":"GALLERY HALL","x":16,"y":24,"w":20,"h":12,"sf":240},
   {"n":"CARPORT","x":0,"y":36,"w":20,"h":10,"sf":200},
 ],
 "doors":[{"x":8,"y":36,"facing":"up"},{"x":22,"y":24,"facing":"up"},{"x":42,"y":18,"facing":"down"},{"x":34,"y":0,"facing":"down"},{"x":44,"y":0,"facing":"down"}],
 "windows":[{"x1":0,"y1":18,"x2":0,"y2":34,"h":False},{"x1":2,"y1":0,"x2":12,"y2":0,"h":True},{"x1":44,"y1":36,"x2":50,"y2":36,"h":True},{"x1":36,"y1":34,"x2":50,"y2":34,"h":True}],
 "dim_h":[{"x1":0,"x2":16,"y":38,"lbl":"16'-0\""},{"x1":16,"x2":36,"y":38,"lbl":"20'-0\""},{"x1":36,"x2":52,"y":38,"lbl":"16'-0\""},{"x1":0,"x2":52,"y":43,"lbl":"52'-0\""}],
 "dim_v":[{"y1":0,"y2":12,"x":54,"lbl":"12'-0\""},{"y1":12,"y2":24,"x":54,"lbl":"12'-0\""},{"y1":24,"y2":36,"x":54,"lbl":"12'-0\""},{"y1":36,"y2":46,"x":54,"lbl":"10'-0\""},{"y1":0,"y2":46,"x":59,"lbl":"46'-0\""}],
},
{
 "num":6,"name":"L-SHAPED RANCH","sf":2100,"type":"Ranch / L-Shape",
 "lot_w":80,"lot_d":120,
 "setbacks":{"front":20,"rear":20,"left":8,"right":8},
 "house_w":54,"house_d":42,
 "rooms":[
   {"n":"FAMILY ROOM","x":0,"y":22,"w":22,"h":20,"sf":440},
   {"n":"GREAT ROOM","x":22,"y":28,"w":18,"h":14,"sf":252},
   {"n":"KITCHEN","x":40,"y":28,"w":14,"h":14,"sf":196},
   {"n":"DINING","x":22,"y":22,"w":18,"h":6,"sf":108},
   {"n":"MASTER BED","x":0,"y":10,"w":16,"h":12,"sf":192},
   {"n":"MASTER BATH","x":16,"y":10,"w":10,"h":8,"sf":80},
   {"n":"W.I.C.","x":16,"y":18,"w":6,"h":4,"sf":24},
   {"n":"BEDROOM 2","x":0,"y":0,"w":12,"h":10,"sf":120},
   {"n":"BEDROOM 3","x":12,"y":0,"w":12,"h":10,"sf":120},
   {"n":"BEDROOM 4","x":24,"y":0,"w":12,"h":10,"sf":120},
   {"n":"BATH 2","x":36,"y":0,"w":10,"h":10,"sf":100},
   {"n":"LAUNDRY","x":46,"y":0,"w":8,"h":10,"sf":80},
   {"n":"GARAGE","x":40,"y":42,"w":22,"h":14,"sf":308},
   {"n":"BACKYARD PATIO","x":0,"y":42,"w":38,"h":8,"sf":304},
 ],
 "doors":[{"x":8,"y":42,"facing":"down"},{"x":42,"y":42,"facing":"up"},{"x":44,"y":42,"facing":"up"},{"x":6,"y":10,"facing":"down"},{"x":6,"y":0,"facing":"down"},{"x":18,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":42,"x2":14,"y2":42,"h":True},{"x1":0,"y1":28,"x2":0,"y2":40,"h":False},{"x1":24,"y1":42,"x2":36,"y2":42,"h":True},{"x1":4,"y1":0,"x2":10,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":22,"y":44,"lbl":"22'-0\""},{"x1":22,"x2":40,"y":44,"lbl":"18'-0\""},{"x1":40,"x2":54,"y":44,"lbl":"14'-0\""},{"x1":0,"x2":54,"y":49,"lbl":"54'-0\""}],
 "dim_v":[{"y1":0,"y2":10,"x":56,"lbl":"10'-0\""},{"y1":10,"y2":22,"x":56,"lbl":"12'-0\""},{"y1":22,"y2":42,"x":56,"lbl":"20'-0\""},{"y1":42,"y2":56,"x":56,"lbl":"14'-0\""},{"y1":0,"y2":56,"x":61,"lbl":"56'-0\""}],
},
{
 "num":7,"name":"COURTYARD HOME","sf":2600,"type":"Courtyard / U-Shape",
 "lot_w":90,"lot_d":130,
 "setbacks":{"front":20,"rear":20,"left":10,"right":10},
 "house_w":60,"house_d":52,
 "rooms":[
   {"n":"COURTYARD","x":16,"y":14,"w":28,"h":22,"sf":616},
   {"n":"LIVING","x":0,"y":22,"w":16,"h":14,"sf":224},
   {"n":"DINING","x":0,"y":14,"w":16,"h":8,"sf":128},
   {"n":"KITCHEN","x":44,"y":22,"w":16,"h":14,"sf":224},
   {"n":"FAMILY","x":44,"y":14,"w":16,"h":8,"sf":128},
   {"n":"MASTER SUITE","x":0,"y":0,"w":18,"h":14,"sf":252},
   {"n":"MASTER BATH","x":18,"y":0,"w":12,"h":8,"sf":96},
   {"n":"BEDROOM 2","x":30,"y":0,"w":14,"h":14,"sf":196},
   {"n":"BEDROOM 3","x":44,"y":0,"w":16,"h":14,"sf":224},
   {"n":"BATH 2","x":30,"y":8,"w":14,"h":6,"sf":84},
   {"n":"GARAGE","x":0,"y":40,"w":24,"h":14,"sf":336},
   {"n":"ENTRY COURT","x":24,"y":40,"w":36,"h":12,"sf":432},
 ],
 "doors":[{"x":28,"y":40,"facing":"up"},{"x":22,"y":22,"facing":"up"},{"x":6,"y":14,"facing":"down"},{"x":6,"y":0,"facing":"down"},{"x":46,"y":0,"facing":"down"},{"x":32,"y":0,"facing":"down"}],
 "windows":[{"x1":0,"y1":28,"x2":0,"y2":34,"h":False},{"x1":2,"y1":0,"x2":14,"y2":0,"h":True},{"x1":46,"y1":0,"x2":58,"y2":0,"h":True},{"x1":46,"y1":28,"x2":58,"y2":28,"h":True}],
 "dim_h":[{"x1":0,"x2":16,"y":42,"lbl":"16'-0\""},{"x1":16,"x2":44,"y":42,"lbl":"28'-0\""},{"x1":44,"x2":60,"y":42,"lbl":"16'-0\""},{"x1":0,"x2":60,"y":47,"lbl":"60'-0\""}],
 "dim_v":[{"y1":0,"y2":14,"x":62,"lbl":"14'-0\""},{"y1":14,"y2":36,"x":62,"lbl":"22'-0\""},{"y1":36,"y2":54,"x":62,"lbl":"18'-0\""},{"y1":0,"y2":54,"x":67,"lbl":"54'-0\""}],
},
{
 "num":8,"name":"4BR/3BA CONTEMPORARY - GROUND","sf":3000,"type":"Contemporary / Two-Story",
 "lot_w":80,"lot_d":140,
 "setbacks":{"front":25,"rear":20,"left":7,"right":7},
 "house_w":56,"house_d":30,
 "rooms":[
   {"n":"GREAT ROOM","x":0,"y":12,"w":28,"h":18,"sf":504},
   {"n":"KITCHEN","x":28,"y":18,"w":16,"h":12,"sf":192},
   {"n":"BUTLER PANTRY","x":28,"y":12,"w":8,"h":6,"sf":48},
   {"n":"DINING","x":36,"y":12,"w":20,"h":6,"sf":120},
   {"n":"STUDY","x":44,"y":18,"w":12,"h":12,"sf":144},
   {"n":"MASTER SUITE","x":0,"y":0,"w":20,"h":12,"sf":240},
   {"n":"MASTER BATH","x":20,"y":0,"w":14,"h":8,"sf":112},
   {"n":"W.I.C.","x":20,"y":8,"w":8,"h":4,"sf":32},
   {"n":"LAUNDRY","x":28,"y":0,"w":10,"h":8,"sf":80},
   {"n":"MUDROOM","x":38,"y":0,"w":8,"h":8,"sf":64},
   {"n":"POWDER","x":34,"y":8,"w":4,"h":4,"sf":16},
   {"n":"GARAGE (3-CAR)","x":0,"y":30,"w":30,"h":14,"sf":420},
   {"n":"COVERED PATIO","x":30,"y":30,"w":26,"h":8,"sf":208},
 ],
 "doors":[{"x":24,"y":30,"facing":"up"},{"x":26,"y":30,"facing":"up"},{"x":28,"y":30,"facing":"up"},{"x":32,"y":30,"facing":"up"},{"x":6,"y":12,"facing":"down"},{"x":4,"y":0,"facing":"down"},{"x":46,"y":18,"facing":"down"}],
 "windows":[{"x1":2,"y1":30,"x2":16,"y2":30,"h":True},{"x1":0,"y1":16,"x2":0,"y2":28,"h":False},{"x1":32,"y1":30,"x2":54,"y2":30,"h":True},{"x1":2,"y1":0,"x2":14,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":28,"y":32,"lbl":"28'-0\""},{"x1":28,"x2":44,"y":32,"lbl":"16'-0\""},{"x1":44,"x2":56,"y":32,"lbl":"12'-0\""},{"x1":0,"x2":56,"y":37,"lbl":"56'-0\""}],
 "dim_v":[{"y1":0,"y2":12,"x":58,"lbl":"12'-0\""},{"y1":12,"y2":30,"x":58,"lbl":"18'-0\""},{"y1":30,"y2":44,"x":58,"lbl":"14'-0\""},{"y1":0,"y2":44,"x":63,"lbl":"44'-0\""}],
},
{
 "num":9,"name":"ADU / ACCESSORY DWELLING UNIT","sf":600,"type":"ADU / Detached Guest House",
 "lot_w":50,"lot_d":100,
 "setbacks":{"front":4,"rear":4,"left":4,"right":4},
 "house_w":24,"house_d":26,
 "rooms":[
   {"n":"LIVING/DINING","x":0,"y":12,"w":14,"h":14,"sf":196},
   {"n":"KITCHEN","x":14,"y":18,"w":10,"h":8,"sf":80},
   {"n":"BEDROOM","x":0,"y":0,"w":14,"h":12,"sf":168},
   {"n":"BATHROOM","x":14,"y":0,"w":10,"h":8,"sf":80},
   {"n":"W/D CLOSET","x":14,"y":8,"w":10,"h":4,"sf":40},
   {"n":"COVERED PORCH","x":0,"y":26,"w":24,"h":6,"sf":144},
 ],
 "doors":[{"x":10,"y":26,"facing":"up"},{"x":6,"y":12,"facing":"down"},{"x":6,"y":0,"facing":"down"},{"x":16,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":26,"x2":8,"y2":26,"h":True},{"x1":0,"y1":14,"x2":0,"y2":22,"h":False},{"x1":2,"y1":0,"x2":10,"y2":0,"h":True},{"x1":16,"y1":24,"x2":22,"y2":24,"h":True}],
 "dim_h":[{"x1":0,"x2":14,"y":28,"lbl":"14'-0\""},{"x1":14,"x2":24,"y":28,"lbl":"10'-0\""},{"x1":0,"x2":24,"y":33,"lbl":"24'-0\""}],
 "dim_v":[{"y1":0,"y2":12,"x":26,"lbl":"12'-0\""},{"y1":12,"y2":26,"x":26,"lbl":"14'-0\""},{"y1":26,"y2":32,"x":26,"lbl":"6'-0\""},{"y1":0,"y2":32,"x":31,"lbl":"32'-0\""}],
},
{
 "num":10,"name":"LUXURY VILLA - GROUND FLOOR","sf":5200,"type":"Luxury / Mediterranean",
 "lot_w":120,"lot_d":180,
 "setbacks":{"front":30,"rear":30,"left":15,"right":15},
 "house_w":72,"house_d":42,
 "rooms":[
   {"n":"GRAND FOYER","x":28,"y":26,"w":16,"h":16,"sf":256},
   {"n":"GREAT ROOM","x":0,"y":26,"w":28,"h":16,"sf":448},
   {"n":"FORMAL DINING","x":44,"y":26,"w":16,"h":16,"sf":256},
   {"n":"CHEF KITCHEN","x":44,"y":12,"w":16,"h":14,"sf":224},
   {"n":"BREAKFAST RM","x":28,"y":12,"w":16,"h":14,"sf":224},
   {"n":"FAMILY ROOM","x":0,"y":12,"w":28,"h":14,"sf":392},
   {"n":"MASTER SUITE","x":60,"y":20,"w":12,"h":22,"sf":264},
   {"n":"MASTER BATH","x":60,"y":12,"w":12,"h":8,"sf":96},
   {"n":"HIS W.I.C.","x":60,"y":8,"w":6,"h":4,"sf":24},
   {"n":"HER W.I.C.","x":66,"y":8,"w":6,"h":4,"sf":24},
   {"n":"STUDY/LIBRARY","x":60,"y":0,"w":12,"h":8,"sf":96},
   {"n":"POWDER ROOM","x":44,"y":0,"w":8,"h":8,"sf":64},
   {"n":"LAUNDRY RM","x":52,"y":0,"w":8,"h":8,"sf":64},
   {"n":"WINE CELLAR","x":0,"y":0,"w":10,"h":12,"sf":120},
   {"n":"GYM/FLEX","x":10,"y":0,"w":14,"h":12,"sf":168},
   {"n":"MUDROOM","x":24,"y":0,"w":12,"h":12,"sf":144},
   {"n":"GARAGE (3C)","x":0,"y":42,"w":32,"h":14,"sf":448},
   {"n":"MOTOR COURT","x":32,"y":42,"w":40,"h":10,"sf":400},
 ],
 "doors":[{"x":34,"y":42,"facing":"up"},{"x":36,"y":42,"facing":"up"},{"x":38,"y":42,"facing":"up"},{"x":36,"y":26,"facing":"up"},{"x":62,"y":20,"facing":"down"},{"x":62,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":42,"x2":22,"y2":42,"h":True},{"x1":0,"y1":28,"x2":0,"y2":40,"h":False},{"x1":60,"y1":36,"x2":70,"y2":36,"h":True},{"x1":62,"y1":0,"x2":70,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":28,"y":44,"lbl":"28'-0\""},{"x1":28,"x2":44,"y":44,"lbl":"16'-0\""},{"x1":44,"x2":60,"y":44,"lbl":"16'-0\""},{"x1":60,"x2":72,"y":44,"lbl":"12'-0\""},{"x1":0,"x2":72,"y":49,"lbl":"72'-0\""}],
 "dim_v":[{"y1":0,"y2":12,"x":74,"lbl":"12'-0\""},{"y1":12,"y2":26,"x":74,"lbl":"14'-0\""},{"y1":26,"y2":42,"x":74,"lbl":"16'-0\""},{"y1":42,"y2":56,"x":74,"lbl":"14'-0\""},{"y1":0,"y2":56,"x":79,"lbl":"56'-0\""}],
},
{
 "num":11,"name":"CRAFTSMAN BUNGALOW","sf":1600,"type":"Craftsman / Bungalow",
 "lot_w":50,"lot_d":120,
 "setbacks":{"front":20,"rear":20,"left":5,"right":5},
 "house_w":40,"house_d":40,
 "rooms":[
   {"n":"FRONT PORCH","x":0,"y":38,"w":40,"h":8,"sf":320},
   {"n":"LIVING ROOM","x":0,"y":24,"w":18,"h":14,"sf":252},
   {"n":"DINING ROOM","x":18,"y":24,"w":14,"h":10,"sf":140},
   {"n":"KITCHEN","x":26,"y":28,"w":14,"h":10,"sf":140},
   {"n":"BREAKFAST NK","x":26,"y":24,"w":8,"h":4,"sf":32},
   {"n":"MASTER BED","x":0,"y":12,"w":16,"h":12,"sf":192},
   {"n":"MASTER BATH","x":16,"y":12,"w":10,"h":8,"sf":80},
   {"n":"BEDROOM 2","x":0,"y":0,"w":14,"h":12,"sf":168},
   {"n":"BEDROOM 3","x":14,"y":0,"w":14,"h":12,"sf":168},
   {"n":"BATH 2","x":28,"y":0,"w":12,"h":8,"sf":96},
   {"n":"W/D","x":28,"y":8,"w":12,"h":4,"sf":48},
   {"n":"BACK PORCH","x":26,"y":20,"w":14,"h":4,"sf":56},
 ],
 "doors":[{"x":16,"y":38,"facing":"down"},{"x":8,"y":24,"facing":"down"},{"x":4,"y":12,"facing":"down"},{"x":4,"y":0,"facing":"down"},{"x":18,"y":0,"facing":"down"}],
 "windows":[{"x1":4,"y1":46,"x2":12,"y2":46,"h":True},{"x1":20,"y1":46,"x2":30,"y2":46,"h":True},{"x1":0,"y1":28,"x2":0,"y2":36,"h":False},{"x1":4,"y1":0,"x2":10,"y2":0,"h":True},{"x1":16,"y1":0,"x2":24,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":18,"y":48,"lbl":"18'-0\""},{"x1":18,"x2":26,"y":48,"lbl":"8'-0\""},{"x1":26,"x2":40,"y":48,"lbl":"14'-0\""},{"x1":0,"x2":40,"y":53,"lbl":"40'-0\""}],
 "dim_v":[{"y1":0,"y2":12,"x":42,"lbl":"12'-0\""},{"y1":12,"y2":24,"x":42,"lbl":"12'-0\""},{"y1":24,"y2":38,"x":42,"lbl":"14'-0\""},{"y1":38,"y2":46,"x":42,"lbl":"8'-0\""},{"y1":0,"y2":46,"x":47,"lbl":"46'-0\""}],
},
{
 "num":12,"name":"MODERN FARMHOUSE","sf":2800,"type":"Modern Farmhouse / Two-Story",
 "lot_w":85,"lot_d":140,
 "setbacks":{"front":25,"rear":20,"left":8,"right":8},
 "house_w":56,"house_d":32,
 "rooms":[
   {"n":"COVERED PORCH","x":8,"y":32,"w":20,"h":8,"sf":160},
   {"n":"FOYER","x":16,"y":24,"w":10,"h":8,"sf":80},
   {"n":"GREAT ROOM","x":0,"y":18,"w":26,"h":14,"sf":364},
   {"n":"KITCHEN","x":26,"y":20,"w":16,"h":12,"sf":192},
   {"n":"DINING","x":26,"y":14,"w":16,"h":6,"sf":96},
   {"n":"MUD ROOM","x":42,"y":20,"w":14,"h":12,"sf":168},
   {"n":"PANTRY","x":42,"y":14,"w":8,"h":6,"sf":48},
   {"n":"MASTER BED","x":0,"y":8,"w":18,"h":10,"sf":180},
   {"n":"MASTER BATH","x":18,"y":8,"w":12,"h":6,"sf":72},
   {"n":"W.I.C.","x":18,"y":0,"w":6,"h":8,"sf":48},
   {"n":"LAUNDRY","x":24,"y":0,"w":10,"h":8,"sf":80},
   {"n":"POWDER","x":34,"y":0,"w":6,"h":6,"sf":36},
   {"n":"STUDY","x":40,"y":0,"w":16,"h":8,"sf":128},
   {"n":"GARAGE","x":0,"y":32,"w":8,"h":14,"sf":112},
   {"n":"2-CAR GARAGE","x":28,"y":32,"w":28,"h":14,"sf":392},
 ],
 "doors":[{"x":20,"y":32,"facing":"up"},{"x":30,"y":32,"facing":"up"},{"x":32,"y":32,"facing":"up"},{"x":20,"y":18,"facing":"down"},{"x":4,"y":8,"facing":"down"},{"x":4,"y":0,"facing":"down"}],
 "windows":[{"x1":10,"y1":40,"x2":24,"y2":40,"h":True},{"x1":0,"y1":22,"x2":0,"y2":30,"h":False},{"x1":28,"y1":40,"x2":54,"y2":40,"h":True},{"x1":42,"y1":0,"x2":54,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":26,"y":42,"lbl":"26'-0\""},{"x1":26,"x2":42,"y":42,"lbl":"16'-0\""},{"x1":42,"x2":56,"y":42,"lbl":"14'-0\""},{"x1":0,"x2":56,"y":47,"lbl":"56'-0\""}],
 "dim_v":[{"y1":0,"y2":8,"x":58,"lbl":"8'-0\""},{"y1":8,"y2":18,"x":58,"lbl":"10'-0\""},{"y1":18,"y2":32,"x":58,"lbl":"14'-0\""},{"y1":32,"y2":46,"x":58,"lbl":"14'-0\""},{"y1":0,"y2":46,"x":63,"lbl":"46'-0\""}],
},
{
 "num":13,"name":"SPLIT-LEVEL HILLSIDE HOME","sf":2200,"type":"Split-Level / Contemporary",
 "lot_w":70,"lot_d":120,
 "setbacks":{"front":20,"rear":20,"left":7,"right":7},
 "house_w":48,"house_d":38,
 "rooms":[
   {"n":"UPPER: LIVING","x":0,"y":22,"w":24,"h":16,"sf":384},
   {"n":"UPPER: DINING","x":24,"y":26,"w":14,"h":12,"sf":168},
   {"n":"UPPER: KITCHEN","x":38,"y":22,"w":10,"h":16,"sf":160},
   {"n":"UPPER: DECK","x":0,"y":38,"w":48,"h":8,"sf":384},
   {"n":"MID: ENTRY","x":18,"y":18,"w":12,"h":4,"sf":48},
   {"n":"LOWER: FAM RM","x":0,"y":8,"w":24,"h":10,"sf":240},
   {"n":"LOWER: BED 1","x":0,"y":0,"w":14,"h":8,"sf":112},
   {"n":"LOWER: BED 2","x":14,"y":0,"w":14,"h":8,"sf":112},
   {"n":"LOWER: BATH","x":28,"y":0,"w":10,"h":8,"sf":80},
   {"n":"LOWER: BED 3","x":38,"y":0,"w":10,"h":8,"sf":80},
   {"n":"GARAGE","x":24,"y":8,"w":24,"h":10,"sf":240},
   {"n":"STAIR UP","x":24,"y":18,"w":6,"h":4,"sf":24},
   {"n":"STAIR DOWN","x":30,"y":18,"w":6,"h":4,"sf":24},
 ],
 "doors":[{"x":20,"y":22,"facing":"up"},{"x":2,"y":8,"facing":"down"},{"x":4,"y":0,"facing":"down"},{"x":18,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":46,"x2":22,"y2":46,"h":True},{"x1":26,"y1":46,"x2":46,"y2":46,"h":True},{"x1":0,"y1":26,"x2":0,"y2":36,"h":False},{"x1":2,"y1":0,"x2":12,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":24,"y":48,"lbl":"24'-0\""},{"x1":24,"x2":38,"y":48,"lbl":"14'-0\""},{"x1":38,"x2":48,"y":48,"lbl":"10'-0\""},{"x1":0,"x2":48,"y":53,"lbl":"48'-0\""}],
 "dim_v":[{"y1":0,"y2":8,"x":50,"lbl":"8'-0\""},{"y1":8,"y2":18,"x":50,"lbl":"10'-0\""},{"y1":18,"y2":22,"x":50,"lbl":"4'-0\""},{"y1":22,"y2":38,"x":50,"lbl":"16'-0\""},{"y1":38,"y2":46,"x":50,"lbl":"8'-0\""},{"y1":0,"y2":46,"x":55,"lbl":"46'-0\""}],
},
{
 "num":14,"name":"TOWNHOUSE / ROW HOME - GROUND","sf":1900,"type":"Townhouse / Row Home",
 "lot_w":26,"lot_d":100,
 "setbacks":{"front":10,"rear":15,"left":0,"right":0},
 "house_w":24,"house_d":46,
 "rooms":[
   {"n":"ENTRY/FOYER","x":0,"y":38,"w":24,"h":8,"sf":192},
   {"n":"GARAGE","x":0,"y":24,"w":24,"h":14,"sf":336},
   {"n":"FLEX/OFFICE","x":0,"y":14,"w":24,"h":10,"sf":240},
   {"n":"POWDER RM","x":0,"y":8,"w":8,"h":6,"sf":48},
   {"n":"STAIR/HALL","x":8,"y":8,"w":16,"h":6,"sf":96},
   {"n":"STORAGE","x":0,"y":0,"w":24,"h":8,"sf":192},
 ],
 "doors":[{"x":10,"y":46,"facing":"up"},{"x":10,"y":24,"facing":"down"},{"x":12,"y":24,"facing":"down"},{"x":4,"y":14,"facing":"down"},{"x":2,"y":8,"facing":"down"}],
 "windows":[{"x1":2,"y1":46,"x2":10,"y2":46,"h":True},{"x1":14,"y1":46,"x2":22,"y2":46,"h":True},{"x1":0,"y1":18,"x2":0,"y2":24,"h":False},{"x1":24,"y1":30,"x2":24,"y2":36,"h":False}],
 "dim_h":[{"x1":0,"x2":24,"y":48,"lbl":"24'-0\""}],
 "dim_v":[{"y1":0,"y2":8,"x":26,"lbl":"8'-0\""},{"y1":8,"y2":14,"x":26,"lbl":"6'-0\""},{"y1":14,"y2":24,"x":26,"lbl":"10'-0\""},{"y1":24,"y2":38,"x":26,"lbl":"14'-0\""},{"y1":38,"y2":46,"x":26,"lbl":"8'-0\""},{"y1":0,"y2":46,"x":31,"lbl":"46'-0\""}],
},
{
 "num":15,"name":"BACKYARD-FOCUSED RANCH","sf":2300,"type":"Ranch / Backyard Lifestyle",
 "lot_w":80,"lot_d":120,
 "setbacks":{"front":20,"rear":10,"left":8,"right":8},
 "house_w":52,"house_d":36,
 "rooms":[
   {"n":"COVERED LANAI","x":4,"y":0,"w":44,"h":10,"sf":440},
   {"n":"FAMILY ROOM","x":0,"y":10,"w":22,"h":16,"sf":352},
   {"n":"KITCHEN","x":22,"y":18,"w":16,"h":8,"sf":128},
   {"n":"NOOK","x":22,"y":10,"w":8,"h":8,"sf":64},
   {"n":"DINING","x":30,"y":10,"w":8,"h":8,"sf":64},
   {"n":"LIVING ROOM","x":38,"y":10,"w":14,"h":16,"sf":224},
   {"n":"MASTER BED","x":0,"y":26,"w":18,"h":12,"sf":216},
   {"n":"MASTER BATH","x":18,"y":26,"w":12,"h":8,"sf":96},
   {"n":"W.I.C.","x":18,"y":34,"w":8,"h":4,"sf":32},
   {"n":"BEDROOM 2","x":30,"y":26,"w":12,"h":10,"sf":120},
   {"n":"BEDROOM 3","x":42,"y":26,"w":10,"h":10,"sf":100},
   {"n":"BATH 2","x":30,"y":36,"w":10,"h":4,"sf":40},
   {"n":"LAUNDRY","x":40,"y":36,"w":12,"h":4,"sf":48},
   {"n":"GARAGE","x":0,"y":38,"w":22,"h":14,"sf":308},
   {"n":"ENTRY PORCH","x":22,"y":38,"w":10,"h":6,"sf":60},
 ],
 "doors":[{"x":10,"y":10,"facing":"down"},{"x":24,"y":38,"facing":"up"},{"x":6,"y":26,"facing":"up"},{"x":32,"y":26,"facing":"up"},{"x":44,"y":26,"facing":"up"}],
 "windows":[{"x1":4,"y1":0,"x2":24,"y2":0,"h":True},{"x1":28,"y1":0,"x2":46,"y2":0,"h":True},{"x1":38,"y1":10,"x2":52,"y2":10,"h":True},{"x1":0,"y1":14,"x2":0,"y2":24,"h":False}],
 "dim_h":[{"x1":0,"x2":22,"y":40,"lbl":"22'-0\""},{"x1":22,"x2":38,"y":40,"lbl":"16'-0\""},{"x1":38,"x2":52,"y":40,"lbl":"14'-0\""},{"x1":0,"x2":52,"y":45,"lbl":"52'-0\""}],
 "dim_v":[{"y1":0,"y2":10,"x":54,"lbl":"10'-0\""},{"y1":10,"y2":26,"x":54,"lbl":"16'-0\""},{"y1":26,"y2":38,"x":54,"lbl":"12'-0\""},{"y1":38,"y2":52,"x":54,"lbl":"14'-0\""},{"y1":0,"y2":52,"x":59,"lbl":"52'-0\""}],
},
{
 "num":16,"name":"U-SHAPED HOME","sf":3400,"type":"U-Shape / Pool Courtyard",
 "lot_w":100,"lot_d":150,
 "setbacks":{"front":25,"rear":15,"left":10,"right":10},
 "house_w":64,"house_d":52,
 "rooms":[
   {"n":"POOL COURT","x":16,"y":8,"w":32,"h":24,"sf":768},
   {"n":"GREAT ROOM","x":0,"y":28,"w":16,"h":24,"sf":384},
   {"n":"KITCHEN","x":0,"y":16,"w":16,"h":12,"sf":192},
   {"n":"DINING","x":0,"y":8,"w":16,"h":8,"sf":128},
   {"n":"MASTER SUITE","x":48,"y":28,"w":16,"h":24,"sf":384},
   {"n":"MASTER BATH","x":48,"y":16,"w":16,"h":12,"sf":192},
   {"n":"BEDROOM 2","x":48,"y":8,"w":16,"h":8,"sf":128},
   {"n":"BEDROOM 3","x":0,"y":0,"w":16,"h":8,"sf":128},
   {"n":"BEDROOM 4","x":16,"y":0,"w":16,"h":8,"sf":128},
   {"n":"BATH 2","x":32,"y":0,"w":16,"h":8,"sf":128},
   {"n":"BATH 3","x":48,"y":0,"w":16,"h":8,"sf":128},
   {"n":"GARAGE","x":0,"y":52,"w":24,"h":14,"sf":336},
   {"n":"ENTRY COURT","x":24,"y":52,"w":40,"h":10,"sf":400},
   {"n":"CABANA","x":22,"y":32,"w":20,"h":8,"sf":160},
 ],
 "doors":[{"x":30,"y":52,"facing":"up"},{"x":6,"y":28,"facing":"down"},{"x":52,"y":28,"facing":"down"},{"x":4,"y":0,"facing":"down"},{"x":18,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":52,"x2":16,"y2":52,"h":True},{"x1":0,"y1":32,"x2":0,"y2":50,"h":False},{"x1":48,"y1":32,"x2":64,"y2":32,"h":True},{"x1":50,"y1":0,"x2":62,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":16,"y":54,"lbl":"16'-0\""},{"x1":16,"x2":48,"y":54,"lbl":"32'-0\""},{"x1":48,"x2":64,"y":54,"lbl":"16'-0\""},{"x1":0,"x2":64,"y":59,"lbl":"64'-0\""}],
 "dim_v":[{"y1":0,"y2":8,"x":66,"lbl":"8'-0\""},{"y1":8,"y2":28,"x":66,"lbl":"20'-0\""},{"y1":28,"y2":52,"x":66,"lbl":"24'-0\""},{"y1":52,"y2":66,"x":66,"lbl":"14'-0\""},{"y1":0,"y2":66,"x":71,"lbl":"66'-0\""}],
},
{
 "num":17,"name":"CALIFORNIA SPANISH REVIVAL","sf":2700,"type":"Spanish / Mediterranean",
 "lot_w":80,"lot_d":130,
 "setbacks":{"front":20,"rear":20,"left":8,"right":8},
 "house_w":54,"house_d":44,
 "rooms":[
   {"n":"LOGGIA/PORCH","x":12,"y":36,"w":30,"h":8,"sf":240},
   {"n":"LIVING ROOM","x":0,"y":24,"w":22,"h":12,"sf":264},
   {"n":"ARCHED FOYER","x":22,"y":28,"w":10,"h":8,"sf":80},
   {"n":"DINING ROOM","x":32,"y":24,"w":22,"h":12,"sf":264},
   {"n":"KITCHEN","x":32,"y":12,"w":22,"h":12,"sf":264},
   {"n":"FAMILY ROOM","x":0,"y":10,"w":22,"h":14,"sf":308},
   {"n":"MASTER BED","x":0,"y":0,"w":16,"h":10,"sf":160},
   {"n":"MASTER BATH","x":16,"y":0,"w":14,"h":6,"sf":84},
   {"n":"BEDROOM 2","x":22,"y":0,"w":12,"h":10,"sf":120},
   {"n":"BEDROOM 3","x":34,"y":0,"w":12,"h":10,"sf":120},
   {"n":"BATH 2","x":16,"y":6,"w":6,"h":4,"sf":24},
   {"n":"BATH 3","x":46,"y":0,"w":8,"h":10,"sf":80},
   {"n":"LAUNDRY","x":30,"y":8,"w":12,"h":4,"sf":48},
   {"n":"GARAGE","x":0,"y":44,"w":22,"h":14,"sf":308},
   {"n":"PORTE-COCHERE","x":22,"y":44,"w":32,"h":8,"sf":256},
 ],
 "doors":[{"x":24,"y":44,"facing":"up"},{"x":26,"y":44,"facing":"up"},{"x":26,"y":36,"facing":"down"},{"x":6,"y":24,"facing":"down"},{"x":36,"y":24,"facing":"down"},{"x":4,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":44,"x2":14,"y2":44,"h":True},{"x1":0,"y1":16,"x2":0,"y2":22,"h":False},{"x1":34,"y1":44,"x2":50,"y2":44,"h":True},{"x1":36,"y1":0,"x2":44,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":22,"y":46,"lbl":"22'-0\""},{"x1":22,"x2":32,"y":46,"lbl":"10'-0\""},{"x1":32,"x2":54,"y":46,"lbl":"22'-0\""},{"x1":0,"x2":54,"y":51,"lbl":"54'-0\""}],
 "dim_v":[{"y1":0,"y2":10,"x":56,"lbl":"10'-0\""},{"y1":10,"y2":24,"x":56,"lbl":"14'-0\""},{"y1":24,"y2":36,"x":56,"lbl":"12'-0\""},{"y1":36,"y2":44,"x":56,"lbl":"8'-0\""},{"y1":44,"y2":58,"x":56,"lbl":"14'-0\""},{"y1":0,"y2":58,"x":61,"lbl":"58'-0\""}],
},
{
 "num":18,"name":"5BR LUXURY ESTATE - GROUND","sf":4800,"type":"Luxury Estate / Traditional",
 "lot_w":120,"lot_d":180,
 "setbacks":{"front":30,"rear":30,"left":15,"right":15},
 "house_w":68,"house_d":46,
 "rooms":[
   {"n":"GRAND FOYER","x":26,"y":30,"w":16,"h":16,"sf":256},
   {"n":"FORMAL LIVING","x":0,"y":34,"w":26,"h":12,"sf":312},
   {"n":"FORMAL DINING","x":42,"y":34,"w":26,"h":12,"sf":312},
   {"n":"GREAT ROOM","x":0,"y":20,"w":26,"h":14,"sf":364},
   {"n":"KITCHEN","x":26,"y":20,"w":16,"h":16,"sf":256},
   {"n":"MORNING RM","x":42,"y":20,"w":14,"h":10,"sf":140},
   {"n":"KEEPING RM","x":56,"y":20,"w":12,"h":16,"sf":192},
   {"n":"BUTLER PANTRY","x":26,"y":16,"w":8,"h":4,"sf":32},
   {"n":"MASTER SUITE","x":0,"y":8,"w":20,"h":12,"sf":240},
   {"n":"MASTER BATH","x":20,"y":8,"w":14,"h":8,"sf":112},
   {"n":"HIS W.I.C.","x":34,"y":8,"w":8,"h":4,"sf":32},
   {"n":"HER W.I.C.","x":34,"y":12,"w":8,"h":4,"sf":32},
   {"n":"STUDY","x":42,"y":8,"w":16,"h":12,"sf":192},
   {"n":"GUEST SUITE","x":58,"y":8,"w":10,"h":12,"sf":120},
   {"n":"LAUNDRY","x":42,"y":0,"w":12,"h":8,"sf":96},
   {"n":"MUDROOM","x":54,"y":0,"w":14,"h":8,"sf":112},
   {"n":"POWDER RM","x":0,"y":0,"w":8,"h":8,"sf":64},
   {"n":"GARAGE (4C)","x":0,"y":46,"w":34,"h":14,"sf":476},
   {"n":"MOTOR COURT","x":34,"y":46,"w":34,"h":10,"sf":340},
 ],
 "doors":[{"x":32,"y":46,"facing":"up"},{"x":36,"y":46,"facing":"up"},{"x":34,"y":30,"facing":"up"},{"x":6,"y":34,"facing":"down"},{"x":44,"y":34,"facing":"down"},{"x":4,"y":8,"facing":"down"}],
 "windows":[{"x1":2,"y1":46,"x2":24,"y2":46,"h":True},{"x1":0,"y1":24,"x2":0,"y2":34,"h":False},{"x1":58,"y1":8,"x2":66,"y2":8,"h":True}],
 "dim_h":[{"x1":0,"x2":26,"y":48,"lbl":"26'-0\""},{"x1":26,"x2":42,"y":48,"lbl":"16'-0\""},{"x1":42,"x2":56,"y":48,"lbl":"14'-0\""},{"x1":56,"x2":68,"y":48,"lbl":"12'-0\""},{"x1":0,"x2":68,"y":53,"lbl":"68'-0\""}],
 "dim_v":[{"y1":0,"y2":8,"x":70,"lbl":"8'-0\""},{"y1":8,"y2":20,"x":70,"lbl":"12'-0\""},{"y1":20,"y2":34,"x":70,"lbl":"14'-0\""},{"y1":34,"y2":46,"x":70,"lbl":"12'-0\""},{"y1":46,"y2":60,"x":70,"lbl":"14'-0\""},{"y1":0,"y2":60,"x":75,"lbl":"60'-0\""}],
},
{
 "num":19,"name":"ZERO-LOT-LINE HOME","sf":1350,"type":"Zero-Lot-Line / Urban Infill",
 "lot_w":35,"lot_d":100,
 "setbacks":{"front":15,"rear":15,"left":0,"right":5},
 "house_w":30,"house_d":46,
 "rooms":[
   {"n":"ENTRY/FOYER","x":0,"y":38,"w":30,"h":8,"sf":240},
   {"n":"GREAT ROOM","x":0,"y":22,"w":20,"h":16,"sf":320},
   {"n":"KITCHEN","x":20,"y":26,"w":10,"h":12,"sf":120},
   {"n":"DINING","x":20,"y":22,"w":10,"h":4,"sf":40},
   {"n":"MASTER BED","x":0,"y":10,"w":16,"h":12,"sf":192},
   {"n":"MASTER BATH","x":16,"y":10,"w":14,"h":6,"sf":84},
   {"n":"W.I.C.","x":16,"y":16,"w":8,"h":6,"sf":48},
   {"n":"BEDROOM 2","x":0,"y":0,"w":14,"h":10,"sf":140},
   {"n":"BATH 2","x":14,"y":0,"w":10,"h":10,"sf":100},
   {"n":"LAUNDRY","x":24,"y":0,"w":6,"h":10,"sf":60},
   {"n":"GARAGE","x":0,"y":46,"w":20,"h":14,"sf":280},
 ],
 "doors":[{"x":10,"y":46,"facing":"up"},{"x":8,"y":38,"facing":"down"},{"x":4,"y":22,"facing":"down"},{"x":4,"y":10,"facing":"down"},{"x":4,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":46,"x2":12,"y2":46,"h":True},{"x1":0,"y1":26,"x2":0,"y2":36,"h":False},{"x1":2,"y1":0,"x2":10,"y2":0,"h":True},{"x1":22,"y1":38,"x2":28,"y2":38,"h":True}],
 "dim_h":[{"x1":0,"x2":20,"y":48,"lbl":"20'-0\""},{"x1":20,"x2":30,"y":48,"lbl":"10'-0\""},{"x1":0,"x2":30,"y":53,"lbl":"30'-0\""}],
 "dim_v":[{"y1":0,"y2":10,"x":32,"lbl":"10'-0\""},{"y1":10,"y2":22,"x":32,"lbl":"12'-0\""},{"y1":22,"y2":38,"x":32,"lbl":"16'-0\""},{"y1":38,"y2":46,"x":32,"lbl":"8'-0\""},{"y1":46,"y2":60,"x":32,"lbl":"14'-0\""},{"y1":0,"y2":60,"x":37,"lbl":"60'-0\""}],
},
{
 "num":20,"name":"NET-ZERO PASSIVE SOLAR HOME","sf":2500,"type":"Contemporary / Passive Solar",
 "lot_w":75,"lot_d":130,
 "setbacks":{"front":20,"rear":20,"left":8,"right":8},
 "house_w":50,"house_d":44,
 "rooms":[
   {"n":"SOLAR TERRACE","x":0,"y":36,"w":50,"h":8,"sf":400},
   {"n":"GREAT ROOM","x":0,"y":22,"w":30,"h":14,"sf":420},
   {"n":"SUNROOM","x":30,"y":26,"w":20,"h":10,"sf":200},
   {"n":"KITCHEN","x":30,"y":22,"w":14,"h":4,"sf":56},
   {"n":"DINING","x":0,"y":18,"w":30,"h":4,"sf":120},
   {"n":"UTILITY/MECH","x":44,"y":22,"w":6,"h":14,"sf":84},
   {"n":"MASTER BED","x":0,"y":8,"w":18,"h":14,"sf":252},
   {"n":"MASTER BATH","x":18,"y":8,"w":12,"h":8,"sf":96},
   {"n":"W.I.C.","x":18,"y":16,"w":8,"h":6,"sf":48},
   {"n":"BEDROOM 2","x":30,"y":8,"w":14,"h":14,"sf":196},
   {"n":"BEDROOM 3","x":0,"y":0,"w":14,"h":8,"sf":112},
   {"n":"BATH 2","x":14,"y":0,"w":10,"h":8,"sf":80},
   {"n":"LAUNDRY/SOLAR","x":24,"y":0,"w":10,"h":8,"sf":80},
   {"n":"STUDY/FLEX","x":34,"y":0,"w":16,"h":8,"sf":128},
   {"n":"GARAGE (EV)","x":0,"y":44,"w":22,"h":14,"sf":308},
   {"n":"BIKE/STORAGE","x":22,"y":44,"w":10,"h":8,"sf":80},
   {"n":"ENTRY","x":32,"y":44,"w":18,"h":6,"sf":108},
 ],
 "doors":[{"x":16,"y":44,"facing":"up"},{"x":18,"y":44,"facing":"up"},{"x":34,"y":44,"facing":"up"},{"x":8,"y":36,"facing":"down"},{"x":6,"y":8,"facing":"down"},{"x":4,"y":0,"facing":"down"}],
 "windows":[{"x1":2,"y1":44,"x2":14,"y2":44,"h":True},{"x1":0,"y1":26,"x2":0,"y2":36,"h":False},{"x1":0,"y1":36,"x2":28,"y2":36,"h":True},{"x1":30,"y1":36,"x2":42,"y2":36,"h":True},{"x1":32,"y1":22,"x2":42,"y2":22,"h":True},{"x1":4,"y1":0,"x2":12,"y2":0,"h":True}],
 "dim_h":[{"x1":0,"x2":30,"y":46,"lbl":"30'-0\""},{"x1":30,"x2":44,"y":46,"lbl":"14'-0\""},{"x1":44,"x2":50,"y":46,"lbl":"6'-0\""},{"x1":0,"x2":50,"y":51,"lbl":"50'-0\""}],
 "dim_v":[{"y1":0,"y2":8,"x":52,"lbl":"8'-0\""},{"y1":8,"y2":22,"x":52,"lbl":"14'-0\""},{"y1":22,"y2":36,"x":52,"lbl":"14'-0\""},{"y1":36,"y2":44,"x":52,"lbl":"8'-0\""},{"y1":44,"y2":58,"x":52,"lbl":"14'-0\""},{"y1":0,"y2":58,"x":57,"lbl":"58'-0\""}],
},
]


def draw_furniture(c, plan):
    S = fp(1)
    def safe_rect(rx, ry, rw, rh, ox=0, oy=0, fw=1, fh=1):
        c.setLineWidth(LW_THIN)
        fx = OX + fp(rx) + ox
        fy = OY + fp(ry) + oy
        c.rect(fx, fy, fw*S, fh*S, stroke=1, fill=0)
    for room in plan["rooms"]:
        n = room["n"]; rx = room["x"]; ry = room["y"]; rw = room["w"]; rh = room["h"]
        c.setLineWidth(LW_THIN)
        if "LIVING" in n or "GREAT ROOM" in n or "FAMILY" in n:
            safe_rect(rx,ry,rw,rh, S*0.5, S*0.5, 7, 3)
            safe_rect(rx,ry,rw,rh, S*2, S*4, 4, 2)
            safe_rect(rx,ry,rw,rh, S*0.5, S*4.5, 2.5, 2.5)
        elif "KITCHEN" in n:
            safe_rect(rx,ry,rw,rh, 0, 0, rw, 1.5)
            if rw > 10: safe_rect(rx,ry,rw,rh, S*1.5, S*2, rw-3, 3.5)
        elif "DINING" in n:
            tw = min(rw-2, 5); td = min(rh-2, 3)
            safe_rect(rx,ry,rw,rh, S*(rw-tw)/2, S*(rh-td)/2, tw, td)
        elif "MASTER BED" in n or ("BEDROOM" in n and rw >= 12):
            safe_rect(rx,ry,rw,rh, S*(rw/2-3), S*(rh/2-1), 6, 7)
        elif "BEDROOM" in n:
            safe_rect(rx,ry,rw,rh, S*(rw/2-2.5), S*(rh/2-1), 5, 6.5)
        elif "BATH" in n:
            if rw >= 6 and rh >= 6:
                safe_rect(rx,ry,rw,rh, 0, S*(rh-2.5), rw*0.5, 2.5)
                safe_rect(rx,ry,rw,rh, S*(rw-2), S*(rh-4), 2, 2)
        elif "GARAGE" in n:
            if rw >= 20:
                safe_rect(rx,ry,rw,rh, S*0.5, S*0.5, 8, 18)
                safe_rect(rx,ry,rw,rh, S*10, S*0.5, 8, 18)
            elif rw >= 10:
                safe_rect(rx,ry,rw,rh, S*0.5, S*0.5, 8, 18)
        elif "STUDY" in n or "OFFICE" in n or "LIBRARY" in n:
            safe_rect(rx,ry,rw,rh, S*0.5, S*(rh-3), rw-1, 3)


def draw_site_plan(c, plan):
    sx = OX + fp(plan["house_w"]) + 80
    sy = OY + fp(plan["house_d"]) + 20
    sw = 120; sh = 100
    lw = plan["lot_w"]; ld = plan["lot_d"]
    sb = plan["setbacks"]; hw = plan["house_w"]; hd = plan["house_d"]
    scale_x = sw / lw; scale_y = sh / ld
    c.setLineWidth(LW_SITE)
    c.rect(sx, sy, sw, sh, stroke=1, fill=0)
    c.setLineWidth(LW_SETBACK)
    c.setDash(4, 3)
    off_l = sb["left"]*scale_x; off_r = sb["right"]*scale_x
    off_f = sb["front"]*scale_y; off_re = sb["rear"]*scale_y
    c.rect(sx+off_l, sy+off_f, sw-off_l-off_r, sh-off_f-off_re, stroke=1, fill=0)
    c.setDash()
    hx = sx + (lw/2 - hw/2) * scale_x
    hy = sy + sb["front"] * scale_y + 5
    c.setLineWidth(LW_WALL)
    c.rect(hx, hy, hw*scale_x, hd*scale_y, stroke=1, fill=0)
    north_arrow(c, sx+sw-14, sy+sh-18, r=8)
    c.setFont("Helvetica-Bold", 5); c.setFillColor(BLACK)
    c.drawString(sx, sy+sh+4, "SITE PLAN")
    c.setFont("Helvetica", 4.5)
    c.drawString(sx, sy+sh+10, f"LOT: {lw}'x{ld}' | SETBACKS F:{sb['front']}' R:{sb['rear']}' L:{sb['left']}' R:{sb['right']}'")
    c.setFont("Helvetica", 4)
    c.drawCentredString(sx+sw/2, sy-6, f"{lw}'-0\"")
    c.saveState(); c.translate(sx-8, sy+sh/2); c.rotate(90)
    c.drawCentredString(0, 0, f"{ld}'-0\""); c.restoreState()


def draw_json_block(c, plan):
    jx = MARGIN + 4; jy = MARGIN + 2
    c.setFont("Helvetica-Bold", 4.5); c.setFillColor(BLACK)
    c.drawString(jx, jy + 12, "AutoCAD JSON Reference:")
    data = {
        "plan": plan["num"], "scale": "1/4\"=1'-0\"", "units": "feet",
        "rooms": [{"id": i+1, "name": r["n"], "x": r["x"], "y": r["y"], "w": r["w"], "h": r["h"], "sf": r["sf"]} for i, r in enumerate(plan["rooms"][:6])],
        "house_dims": {"w": plan["house_w"], "d": plan["house_d"]},
        "layer_standard": "AIA_CAD",
        "layers": ["A-WALL-EXTR","A-WALL-INTR","A-DOOR","A-GLAZ","A-ANNO-TEXT","A-ANNO-DIMS","A-FLOR-HRAL","C-PROP"]
    }
    json_str = json.dumps(data, separators=(',',':'))
    chars_per_line = 160
    c.setFont("Courier", 3.5)
    for i in range(0, len(json_str), chars_per_line):
        c.drawString(jx, jy + 8 - (i//chars_per_line)*4.5, json_str[i:i+chars_per_line])


def draw_plan(c, plan):
    border(c)
    title_block(c, plan["num"], plan["name"], plan["sf"], plan["type"], plan["num"], len(PLANS))
    plan_header(c, f"PLAN {plan['num']:02d}  --  {plan['name']}")
    for room in plan["rooms"]:
        rx = OX + fp(room["x"]); ry = OY + fp(room["y"])
        rw = fp(room["w"]); rh = fp(room["h"])
        is_service = any(k in room["n"] for k in ["BATH","TOILET","LAUNDRY","MECH","UTILITY","CLOSET","W.I.C","PANTRY","MUDROOM","POWDER","W/D","STAIR","HALL"])
        c.setLineWidth(LW_WALL_INT if is_service else LW_WALL)
        c.setStrokeColor(BLACK)
        c.rect(rx, ry, rw, rh, stroke=1, fill=0)
        room_label(c, rx+rw/2, ry+rh/2, room["n"], room.get("sf"), fontsize=5.5)
    for d in plan.get("doors", []):
        door_swing(c, OX+fp(d["x"]), OY+fp(d["y"]), fp(3), facing=d["facing"])
    for w in plan.get("windows", []):
        window_sym(c, OX+fp(w["x1"]), OY+fp(w["y1"]), OX+fp(w["x2"]), OY+fp(w["y2"]), horizontal=w["h"])
    for d in plan.get("dim_h", []):
        dim_line(c, OX+fp(d["x1"]), OY+fp(d["y"]), OX+fp(d["x2"]), OY+fp(d["y"]), d["lbl"], side='top', offset=10)
    for d in plan.get("dim_v", []):
        dim_line(c, OX+fp(d["x"]), OY+fp(d["y1"]), OX+fp(d["x"]), OY+fp(d["y2"]), d["lbl"], side='right', offset=10)
    draw_furniture(c, plan)
    na_x = OX + fp(plan["house_w"]) + 30
    na_y = OY + fp(plan["house_d"]) / 2
    north_arrow(c, na_x, na_y, r=12)
    scale_bar(c, na_x - 36, na_y - 30, "1/4\"=1'-0\"", bar_width_ft=20)
    draw_site_plan(c, plan)
    draw_json_block(c, plan)
    c.setFont("Helvetica", 5); c.setFillColor(BLACK)
    c.drawString(MARGIN+4, MARGIN+6, f"PLAN {plan['num']:02d}  |  {plan['name']}  |  {plan['sf']:,} SF  |  {plan['type'].upper()}  |  SCALE: 1/4\" = 1'-0\"  |  zHEIGHT AI ARCHITECTURE KNOWLEDGE BASE")


def draw_cover(c):
    border(c)
    cx = (PW - 1.8*inch) / 2 + MARGIN; cy = PH / 2
    c.setFont("Helvetica-Bold", 22); c.setFillColor(BLACK)
    c.drawCentredString(cx, cy + 80, "USA RESIDENTIAL FLOOR PLAN")
    c.drawCentredString(cx, cy + 55, "REFERENCE LIBRARY")
    c.setLineWidth(1.5)
    c.line(cx-150, cy+45, cx+150, cy+45)
    c.line(cx-150, cy-5,  cx+150, cy-5)
    c.setFont("Helvetica", 11)
    c.drawCentredString(cx, cy+30, "20 STANDARD RESIDENTIAL PLAN TYPES")
    c.drawCentredString(cx, cy+16, "AutoCAD-Standard Black & White Drafting Format")
    c.drawCentredString(cx, cy+2,  "AIA Layer Standards  |  1/4\" = 1'-0\" Scale  |  Imperial Dimensions")
    c.setFont("Helvetica-Bold", 8)
    c.drawCentredString(cx, cy-24, "PLAN INDEX")
    c.setFont("Helvetica", 7)
    cols = [PLANS[:10], PLANS[10:]]
    for col_idx, col in enumerate(cols):
        for i, p in enumerate(col):
            px = cx - 200 + col_idx * 260; py = cy - 38 - i*13
            c.drawString(px, py, f"A{p['num']:02d}.0")
            c.drawString(px+30, py, f"PLAN {p['num']:02d}  --  {p['name']}")
            c.drawRightString(px+255, py, f"{p['sf']:,} SF  |  {p['type']}")
    c.setFont("Helvetica", 7)
    c.drawCentredString(cx, cy-180, "NOTES: ALL PLANS ARE FOR REFERENCE AND AI TRAINING PURPOSES.")
    c.drawCentredString(cx, cy-191, "DIMENSIONS ARE NOMINAL. VERIFY ALL CONDITIONS WITH LOCAL CODE.")
    c.drawCentredString(cx, cy-202, "PLANS FOLLOW AIA CAD LAYER GUIDELINES, IRC 2021, AND US STANDARD PRACTICE.")
    c.setFont("Helvetica-Bold", 9)
    c.drawCentredString(cx, MARGIN+25, "zHEIGHT AI  --  ARCHITECTURE + PLANNING  --  KNOWLEDGE BASE SERIES")
    c.setFont("Helvetica", 7)
    c.drawCentredString(cx, MARGIN+14, "AI TRAINING REFERENCE DOCUMENT  |  FOR USE WITH AUTOCAD LAYOUT GENERATION SYSTEM")
    title_block(c, 0, "COVER / INDEX", 0, "All Plan Types", 0, len(PLANS))


OUT = "/mnt/user-data/outputs/USA_Residential_FloorPlan_Library.pdf"
c = rl_canvas.Canvas(OUT, pagesize=PAGE)
c.setTitle("USA Residential Floor Plan Reference Library - zHeight AI")
c.setAuthor("zHeight AI Architecture + Planning")
c.setSubject("20 Standard Residential Floor Plans - AutoCAD Reference")
draw_cover(c)
c.showPage()
for plan in PLANS:
    draw_plan(c, plan)
    c.showPage()
c.save()
print(f"PDF saved: {OUT}")
print(f"Pages: {len(PLANS)+1} (cover + 20 plans)")