"""Build the Windows multi-resolution icon from the shared FlowKey SVG geometry.

Requires Pillow. Run from any directory with: python assets/generate_icon.py
"""
from pathlib import Path
import xml.etree.ElementTree as ET
import base64
import re
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parent
svg = ET.parse(root / 'flowkey.svg').getroot()
scale = 16
image = Image.new('RGBA', (64 * scale, 64 * scale))
draw = ImageDraw.Draw(image)
for element in svg:
    tag = element.tag.rsplit('}', 1)[-1]
    if tag == 'rect':
        x, y = float(element.get('x', 0)), float(element.get('y', 0))
        width, height = float(element.get('width')), float(element.get('height'))
        fill = element.get('fill')
        stroke_width = float(element.get('stroke-width', 0))
        # SVG strokes straddle the path; Pillow outlines are drawn inward.
        box = tuple(round(v * scale) for v in (x-stroke_width/2, y-stroke_width/2, x+width+stroke_width/2, y+height+stroke_width/2))
        draw.rounded_rectangle(box, radius=round((float(element.get('rx', 0))+stroke_width/2)*scale),
                               fill=None if fill == 'none' else fill,
                               outline=element.get('stroke'), width=round(stroke_width*scale))
    elif tag == 'path':
        # The logo's one filled triangle uses an M ... Z polygon.
        values = [float(v) for v in element.get('d').replace('M', '').replace('Z', '').split()]
        draw.polygon([(round(values[i]*scale), round(values[i+1]*scale)) for i in range(0,len(values),2)], fill=element.get('fill'))
    else:
        raise ValueError(f'Unsupported logo shape: {tag}')
image.resize((256, 256), Image.Resampling.LANCZOS).save(root / 'flowkey.png')
image.save(root / 'flowkey.ico', sizes=[(s,s) for s in [16,20,24,32,40,48,64,128,256]])
# Keep the HTML mark independent of preview-server routes or adjacent image files.
uri = 'data:image/svg+xml;base64,' + base64.b64encode((root / 'flowkey.svg').read_text(encoding='utf-8').encode()).decode()
for name in ['index.html', 'FlowKey-UI-Demo.html']:
    page = root.parent / name
    html = page.read_text(encoding='utf-8')
    html, count = re.subn(r'src="(?:assets/flowkey.svg|data:image/svg\+xml;base64,[^"]+)"', 'src="' + uri + '"', html)
    if count != 1:
        raise ValueError(f'Expected one brand image in {name}, found {count}')
    page.write_text(html, encoding='utf-8')
