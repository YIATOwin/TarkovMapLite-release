"""Import tarkov.dev's Reserve extents and fill missing SVG floors from tiles.

Usage: python tools/build-reserve-floors.py path/to/tarkov-dev/src/data/maps.json
Requires Pillow. Run after rasterizing SVGs or rebuilding/cropping tile images.
Existing projection/calibration values are preserved; no network is needed.
"""
import math


def project(m, x, z, width, height):
    r = math.radians(m['rotation'])
    a, b, c, d = m['transform']

    def pixel(x, z):
        return (a * (x * math.cos(r) - z * math.sin(r)) + b,
                -c * (x * math.sin(r) + z * math.cos(r)) + d)

    corners = [pixel(*p) for p in m['bounds']]
    min_x, max_x = min(p[0] for p in corners), max(p[0] for p in corners)
    min_y, max_y = min(p[1] for p in corners), max(p[1] for p in corners)
    px, py = pixel(x, z)
    return ((px - min_x) / (max_x - min_x) * width,
            (py - min_y) / (max_y - min_y) * height)


def matrix(m, width, height):
    c, f = project(m, 0, 0, width, height)
    ax, ay = project(m, 1, 0, width, height)
    bx, by = project(m, 0, 1, width, height)
    return ax-c, bx-c, c, ay-f, by-f, f


def inverse(m):
    a, b, c, d, e, f = m
    det = a*e - b*d
    return e/det, -b/det, (b*f-e*c)/det, -d/det, a/det, (d*c-a*f)/det


def compose(s, t):
    a, b, c, d, e, f = s
    g, h, i, j, k, l = t
    return (a*g+b*j, a*h+b*k, a*i+b*l+c,
            d*g+e*j, d*h+e*k, d*i+e*l+f)


def main():
    # Keep the original entry point without replacing other maps' rules.
    from importlib import import_module
    import_module('sync-map-layers').main()


if __name__ == '__main__':
    main()
