# Wallpapers

Two pages, one per screen. `make_wallpaper.py` draws them as SVG; `rsvg-convert` rasterises.

- **left** (3440x1440): the core: the pipeline from `.treb` sources through the checker and
  roots to the three targets, what the checker enforces, syntax at a glance, samples, tooling.
- **right** (1920x1080): the targets: the .NET path (C# emitter, `Trebuchet.Runtime`, ASP.NET
  host), the C++ path (emitter, `trebuchet.hpp`, native driver), and the interop boundary.

```
python3 make_wallpaper.py left  3440 1440 trebuchet-left-3440x1440.svg  && rsvg-convert -w 3440 -h 1440 trebuchet-left-3440x1440.svg  -o trebuchet-left-3440x1440.png
python3 make_wallpaper.py right 1920 1080 trebuchet-right-1920x1080.svg && rsvg-convert -w 1920 -h 1080 trebuchet-right-1920x1080.svg -o trebuchet-right-1920x1080.png
```

Apply per screen on KDE Plasma (`plasma-apply-wallpaperimage` sets every screen to one image):

```
qdbus6 org.kde.plasmashell /PlasmaShell org.kde.PlasmaShell.evaluateScript '
var ds = desktops();
for (var i = 0; i < ds.length; i++) {
  var d = ds[i]; var g = screenGeometry(d.screen);
  d.wallpaperPlugin = "org.kde.image";
  d.currentConfigGroup = ["Wallpaper", "org.kde.image", "General"];
  d.writeConfig("Image", (g.width == 1920) ? "file:///home/jason/Pictures/wallpapers/trebuchet-right-1920x1080.png" : "file:///home/jason/Pictures/wallpapers/trebuchet-left-3440x1440.png");
}'
```
