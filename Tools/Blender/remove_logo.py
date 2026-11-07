"""
Paints the scanned kit's brand logos out of the player's textures.

    blender -b --factory-startup --python remove_logo.py -- <work_dir> <player_art_dir>

The logos sit on the left of the chest and on the left leg of the shorts. Faces in those parts of the
body are found in 3D on the rigged model, their UV triangles mark where to look in the texture, and
inside them the logo's dark blue is filled from the surrounding cloth by diffusion. The same pixels are
filled in both kits' base colours, the normal map and the mask. Run it again after prepare_meshes.py.
"""
import os
import sys

import bpy
import numpy as np

# Regions of the rest pose that carry a logo: Blender +X is the player's left, -Y their front, Z up, metres.
REGIONS = [
    ("chest", (0.02, 0.27), (-0.40, 0.02), (1.26, 1.54)),
    ("shorts", (0.04, 0.28), (-0.40, 0.08), (0.50, 0.84)),
    ("back hem", (-0.32, -0.02), (-0.02, 0.40), (0.88, 1.14)),
    ("socks", (-0.34, 0.34), (-0.40, 0.10), (0.08, 0.34)),
    ("left wristband", (0.34, 0.58), (-0.30, 0.30), (0.93, 1.20)),
    ("right wristband", (-0.58, -0.34), (-0.30, 0.30), (0.93, 1.20)),
]
TEXTURES = ["Player_BaseColor.png", "Player_BaseColor_Red.png", "Player_Normal.png", "Player_Mask.png"]
DILATE_PX = 6
FILL_ITERATIONS = 150
TILE = 256
TILE_MARGIN = 48


def log(message):
    print(f"[logo] {message}", flush=True)


def load(path):
    image = bpy.data.images.load(path, check_existing=False)
    width, height = image.size
    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    return image, pixels.reshape(height, width, 4)


def save(image, pixels):
    image.pixels.foreach_set(pixels.ravel())
    image.save()
    log(f"wrote {image.filepath}")


def region_mask(mesh, width, height):
    """Texture pixels covered by faces inside the logo regions."""
    mask = np.zeros((height, width), dtype=bool)
    uv = mesh.uv_layers.active.data
    count = 0
    for poly in mesh.polygons:
        c = poly.center
        if not any(x0 <= c.x <= x1 and y0 <= c.y <= y1 and z0 <= c.z <= z1 for _, (x0, x1), (y0, y1), (z0, z1) in REGIONS):
            continue
        count += 1
        corners = [np.array(uv[i].uv) * (width, height) for i in poly.loop_indices]
        for k in range(1, len(corners) - 1):
            fill_triangle(mask, corners[0], corners[k], corners[k + 1])
    log(f"{count} faces in the logo regions, {mask.sum()} texture pixels")
    return mask


def fill_triangle(mask, a, b, c):
    lo = np.floor(np.minimum(np.minimum(a, b), c)).astype(int)
    hi = np.ceil(np.maximum(np.maximum(a, b), c)).astype(int)
    lo = np.maximum(lo, 0)
    hi = np.minimum(hi, (mask.shape[1] - 1, mask.shape[0] - 1))
    if (hi < lo).any():
        return
    xs, ys = np.meshgrid(np.arange(lo[0], hi[0] + 1) + 0.5, np.arange(lo[1], hi[1] + 1) + 0.5)
    v0, v1 = b - a, c - a
    denom = v0[0] * v1[1] - v1[0] * v0[1]
    if abs(denom) < 1e-9:
        return
    px, py = xs - a[0], ys - a[1]
    s = (px * v1[1] - v1[0] * py) / denom
    t = (v0[0] * py - px * v0[1]) / denom
    inside = (s >= -0.02) & (t >= -0.02) & (s + t <= 1.02)
    mask[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1] |= inside


def dilate(mask, radius):
    result = mask.copy()
    for dy in range(-radius, radius + 1):
        for dx in range(-radius, radius + 1):
            if dx * dx + dy * dy <= radius * radius:
                result |= np.roll(np.roll(mask, dy, axis=0), dx, axis=1)
    return result


def logo_pixels(base, region):
    """The logo is a dark, blue ink on the white cloth; its soft edges are paler, but still bluer than the cloth."""
    rgb = base[..., :3]
    value = rgb.max(axis=-1)
    bluish = rgb[..., 2] - rgb[..., 0]
    ink = ((value < 0.6) & (bluish > 0.04)) | ((value < 0.78) & (bluish > 0.09))
    return dilate(ink & region, DILATE_PX) & dilate(region, 2)


def diffuse_fill(pixels, hole):
    """Fills each tile that has part of the hole, with a margin of known cloth around it to fill from."""
    result = pixels.copy()
    height, width = hole.shape
    for y in range(0, height, TILE):
        for x in range(0, width, TILE):
            if not hole[y:y + TILE, x:x + TILE].any():
                continue
            y0, y1 = max(0, y - TILE_MARGIN), min(height, y + TILE + TILE_MARGIN)
            x0, x1 = max(0, x - TILE_MARGIN), min(width, x + TILE + TILE_MARGIN)
            local_hole = np.zeros_like(hole[y0:y1, x0:x1])
            local_hole[y - y0:y - y0 + TILE, x - x0:x - x0 + TILE] = hole[y:y + TILE, x:x + TILE]
            filled = fill_tile(result[y0:y1, x0:x1], local_hole)
            result[y0:y1, x0:x1][local_hole] = filled[local_hole]
    return result


def fill_tile(pixels, hole):
    """Replaces the hole with an average of its surroundings that grows inwards, like paint spread from the edges."""
    result = pixels.copy()
    known = ~hole
    weight = known.astype(np.float32)
    colour = result * weight[..., None]
    for _ in range(FILL_ITERATIONS):
        neighbours = np.zeros_like(colour)
        count = np.zeros_like(weight)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            neighbours += np.roll(np.roll(colour, dy, axis=0), dx, axis=1)
            count += np.roll(np.roll(weight, dy, axis=0), dx, axis=1)
        filled = count > 0
        update = hole & filled
        colour[update] = neighbours[update] / count[update][..., None]
        weight[update] = 1.0
    result[hole] = colour[hole]
    return result


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    work_dir, art_dir = argv[0], argv[1]
    bpy.ops.wm.open_mainfile(filepath=os.path.join(work_dir, "player_rigged.blend"))
    mesh = bpy.data.objects["Player"].data

    base_image, base = load(os.path.join(art_dir, TEXTURES[0]))
    height, width = base.shape[:2]
    region = region_mask(mesh, width, height)
    hole = logo_pixels(base, region)
    log(f"logo: {hole.sum()} pixels")
    if hole.sum() == 0:
        raise RuntimeError("No logo found in the regions: check REGIONS against the model")

    preview = base.copy()
    preview[hole] = (1.0, 0.0, 1.0, 1.0)
    preview_image = bpy.data.images.new("logo_preview", width, height, alpha=True)
    preview_image.pixels.foreach_set(preview.ravel())
    preview_image.filepath_raw = os.path.join(work_dir, "logo_found.png")
    preview_image.file_format = "PNG"
    preview_image.save()

    save(base_image, diffuse_fill(base, hole))
    for name in TEXTURES[1:]:
        image, pixels = load(os.path.join(art_dir, name))
        if pixels.shape[:2] != hole.shape:
            raise RuntimeError(f"{name} is not the base colour's size")
        save(image, diffuse_fill(pixels, hole))
    log("done")


main()
