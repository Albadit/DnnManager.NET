"""Generates the Avalonia app's theme from the WPF app's: the palette (LightTheme/DarkTheme.xaml), the sizes
(Tokens.xaml) and the icons (Icons.xaml) - the same keys and values, so both apps look alike."""
import re, sys, os

# Run from anywhere: python .github/scripts/port-theme.py
root = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
wpf = os.path.join(root, 'src', 'DnnManager.Presentation', 'Themes')
out = os.path.join(root, 'src', 'DnnManager.Desktop', 'Themes')
os.makedirs(out, exist_ok=True)
NL = '\r\n'

def read(name):
    return open(os.path.join(wpf, name), encoding='utf-8').read()

def brushes(xaml):
    lines = []
    for line in xaml.splitlines():
        s = line.strip()
        if s.startswith('<SolidColorBrush'):
            lines.append('      ' + s)
        elif s.startswith('<!--') and lines is not None:
            lines.append('      ' + s)
    # Comments that only open (multi-line) are kept whole by re-reading blocks.
    return lines

def block_lines(xaml, keep):
    """The body of a ResourceDictionary: elements accepted by keep(), with the comments around them."""
    body = xaml[xaml.index('>', xaml.index('<ResourceDictionary')) + 1: xaml.rindex('</ResourceDictionary>')]
    items = re.findall(r'(<!--.*?-->|<[A-Za-z:]+ [^>]*?/>|<[A-Za-z:]+ x:Key="[^"]+"[^>]*>.*?</[A-Za-z:]+>)', body, re.S)
    result = []
    for item in items:
        if item.startswith('<!--'):
            result.append(item)
        else:
            converted = keep(item)
            if converted:
                result.append(converted)
    # Drop comments that end up with nothing after them before the next comment run.
    return result

def palette_item(item):
    return item if item.startswith('<SolidColorBrush') else None

def token_item(item):
    if item.startswith('<CornerRadius') or item.startswith('<Thickness'):
        return item
    if item.startswith('<sys:Double'):
        return item.replace('sys:Double', 'x:Double')
    return None  # the ScaleTransform (UI scale) - not in this app yet

def icon_item(item):
    if item.startswith('<FontFamily'):
        return item
    if item.startswith('<sys:String'):
        return item.replace('sys:String', 'x:String')
    if item.startswith('<Geometry'):
        return item.replace('<Geometry', '<StreamGeometry').replace('</Geometry>', '</StreamGeometry>')
    return None

def indent(items, spaces):
    pad = ' ' * spaces
    out_lines = []
    for item in items:
        for i, l in enumerate(item.splitlines()):
            out_lines.append(pad + l.strip() if i == 0 else pad + '     ' + l.strip())
    return NL.join(out_lines)

header = ('<!-- GENERATED from the Windows app\'s theme (src/DnnManager.Presentation/Themes/{src}) - the same keys and values,' + NL +
          '     so both apps look alike. Change the WPF file, then run .github/scripts/port-theme.py; don\'t edit this one. -->' + NL)

light = block_lines(read('LightTheme.xaml'), palette_item)
dark = block_lines(read('DarkTheme.xaml'), palette_item)
palette = (header.format(src='LightTheme.xaml, DarkTheme.xaml') +
           '<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + NL +
           '  <ResourceDictionary.ThemeDictionaries>' + NL +
           '    <ResourceDictionary x:Key="Light">' + NL + indent(light, 6) + NL + '    </ResourceDictionary>' + NL +
           '    <ResourceDictionary x:Key="Dark">' + NL + indent(dark, 6) + NL + '    </ResourceDictionary>' + NL +
           '  </ResourceDictionary.ThemeDictionaries>' + NL + '</ResourceDictionary>' + NL)
open(os.path.join(out, 'Palette.axaml'), 'w', encoding='utf-8', newline='').write(palette)

tokens = block_lines(read('Tokens.xaml'), token_item)
open(os.path.join(out, 'Tokens.axaml'), 'w', encoding='utf-8', newline='').write(
    header.format(src='Tokens.xaml') +
    '<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + NL +
    indent(tokens, 2) + NL + '</ResourceDictionary>' + NL)

icons = block_lines(read('Icons.xaml'), icon_item)
open(os.path.join(out, 'Icons.axaml'), 'w', encoding='utf-8', newline='').write(
    header.format(src='Icons.xaml') +
    '<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + NL +
    indent(icons, 2) + NL + '</ResourceDictionary>' + NL)

for name in 'Palette.axaml', 'Tokens.axaml', 'Icons.axaml':
    text = open(os.path.join(out, name), encoding='utf-8').read()
    keys = re.findall(r'x:Key="([^"]+)"', text)
    print(name, len(keys), 'keys')
