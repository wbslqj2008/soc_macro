"""Detect deployed ally units on the deployment screen via their blue HP bars."""
import sys
import cv2
import numpy as np

# tile center relative to the left end of the HP bar (measured on d1.png)
FOOT_DX, FOOT_DY = 0, 118


def find_allies(img):
    b, g, r = [img[:, :, i].astype(int) for i in range(3)]
    blue = (b >= 195) & (b - r >= 60) & (g >= 95)
    blue[540:] = False  # ignore the roster strip at the bottom
    # top edge of a thin blue strip: not blue 2px above, blue for 3px, not blue 7px below
    cand = np.zeros_like(blue)
    cand[2:-7] = ~blue[:-9] & blue[2:-7] & blue[3:-6] & blue[4:-5] & ~blue[9:]
    bars = []
    ys, xs = np.nonzero(cand)
    rows = {}
    for y, x in zip(ys, xs):
        rows.setdefault(y, []).append(x)
    for y, xl in rows.items():
        xl.sort()
        start = prev = xl[0]
        for x in xl[1:] + [10**9]:
            if x != prev + 1:
                if prev - start >= 25:
                    bars.append((start, y, prev - start + 1))
                start = x
            prev = x
    # merge duplicates (same bar found on adjacent rows)
    out = []
    for x, y, w in sorted(bars, key=lambda t: (t[1], t[0])):
        if not any(abs(x - ox) < 10 and abs(y - oy) < 6 for ox, oy, _ in out):
            out.append((x, y, w))
    # icon sits left of the bar; unit's tile is below the icon
    return [{"bar": (x, y, w), "tile": (x - 20 + 33 + FOOT_DX, y + FOOT_DY)} for x, y, w in out]


if __name__ == "__main__":
    for path in sys.argv[1:]:
        img = cv2.imread(path)
        allies = find_allies(img)
        print(path, len(allies), [a["tile"] for a in allies])
        for a in allies:
            x, y, w = a["bar"]
            cv2.rectangle(img, (x, y - 1), (x + w, y + 4), (0, 0, 255), 2)
            cv2.circle(img, a["tile"], 8, (0, 0, 255), 3)
        cv2.imwrite(path.replace(".png", "_det.png"), img)
