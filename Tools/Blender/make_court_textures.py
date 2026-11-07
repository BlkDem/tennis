"""
Generates the hard court's textures: procedural noise is baked from Blender shader nodes,
then composed with the official line markings and plausible wear.

    blender -b --factory-startup --python make_court_textures.py -- <art_dir>

The base colour covers the whole surface including the run-off, so lines and wear are painted
into it. A small tiling detail set adds the acrylic's grit up close.
Dimensions must match CourtGeometry.cs and ProjectBuilder.cs.
"""
import os
import sys

import bpy
import numpy as np

# Surface covered by the texture, metres. The game's surround mesh must use the same size.
SURFACE_WIDTH = 18.29
SURFACE_LENGTH = 36.58
BASE_WIDTH_PX = 2048
BASE_LENGTH_PX = 4096

# Official court, metres.
HALF_LENGTH = 23.77 / 2
HALF_SINGLES = 8.23 / 2
HALF_DOUBLES = 10.97 / 2
SERVICE_LINE = 6.40
BASELINE_WIDTH = 0.10
LINE_WIDTH = 0.05
CENTRE_MARK_LENGTH = 0.10

# Linear colours of a two-tone acrylic court: green inside the lines, terracotta run-off.
PLAY_COLOUR = np.array([0.055, 0.19, 0.085])
SURROUND_COLOUR = np.array([0.30, 0.085, 0.05])
LINE_COLOUR = np.array([0.86, 0.87, 0.86])

DETAIL_SIZE = 1024
DETAIL_TILE_METRES = 1.5
DETAIL_NORMAL_STRENGTH = 2.5


def log(message):
    print(f"[court] {message}", flush=True)


def setup_bake_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 1
    scene.cycles.use_denoising = False

    bpy.ops.mesh.primitive_plane_add(size=1.0)
    plane = bpy.context.active_object
    plane.scale = (SURFACE_WIDTH, SURFACE_LENGTH, 1.0)
    bpy.ops.object.transform_apply(scale=True)
    plane.data.materials.append(bpy.data.materials.new("bake"))
    plane.data.materials[0].use_nodes = True
    return plane


def bake_noise(plane, width, height, scale, detail, roughness, stretch=(1.0, 1.0, 1.0), offset=0.0):
    """Bakes one layer of Blender's Perlin noise, measured in metres on the court, and normalises it to 0..1."""
    material = plane.data.materials[0]
    tree = material.node_tree
    tree.nodes.clear()

    coordinates = tree.nodes.new("ShaderNodeTexCoord")
    mapping = tree.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = stretch
    mapping.inputs["Location"].default_value = (offset, offset * 1.7, offset * 0.3)
    noise = tree.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = scale
    noise.inputs["Detail"].default_value = detail
    noise.inputs["Roughness"].default_value = roughness
    emission = tree.nodes.new("ShaderNodeEmission")
    output = tree.nodes.new("ShaderNodeOutputMaterial")

    tree.links.new(coordinates.outputs["Object"], mapping.inputs["Vector"])
    tree.links.new(mapping.outputs["Vector"], noise.inputs["Vector"])
    tree.links.new(noise.outputs["Fac"], emission.inputs["Color"])
    tree.links.new(emission.outputs["Emission"], output.inputs["Surface"])

    image = bpy.data.images.new("bake_target", width, height, alpha=False, float_buffer=True)
    image.colorspace_settings.name = "Non-Color"
    target = tree.nodes.new("ShaderNodeTexImage")
    target.image = image
    tree.nodes.active = target

    bpy.ops.object.select_all(action="DESELECT")
    plane.select_set(True)
    bpy.context.view_layer.objects.active = plane
    bpy.ops.object.bake(type="EMIT", margin=0)

    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    bpy.data.images.remove(image)
    layer = pixels.reshape(height, width, 4)[..., 0]

    low, high = np.percentile(layer, [1.0, 99.0])
    if high - low < 1e-5:
        raise RuntimeError("Noise bake came out flat")
    return np.clip((layer - low) / (high - low), 0.0, 1.0)


def rect(xs, zs, cx, cz, half_x, half_z, px_x, px_z):
    """Antialiased coverage of an axis-aligned rectangle, for crisp lines at any resolution."""
    cover_x = np.clip((half_x - np.abs(xs - cx)) / px_x + 0.5, 0.0, 1.0)
    cover_z = np.clip((half_z - np.abs(zs - cz)) / px_z + 0.5, 0.0, 1.0)
    return np.outer(cover_z, cover_x)


def line_coverage(xs, zs, px_x, px_z):
    coverage = np.zeros((zs.size, xs.size), dtype=np.float32)

    def add(cx, cz, half_x, half_z):
        nonlocal coverage
        coverage = np.maximum(coverage, rect(xs, zs, cx, cz, half_x, half_z, px_x, px_z))

    for sign in (-1.0, 1.0):
        # Lines lie inside the court, so their outer edges are the official dimensions.
        add(0.0, sign * (HALF_LENGTH - BASELINE_WIDTH / 2), HALF_DOUBLES, BASELINE_WIDTH / 2)
        add(0.0, sign * SERVICE_LINE, HALF_SINGLES, LINE_WIDTH / 2)
        add(0.0, sign * (HALF_LENGTH - BASELINE_WIDTH - CENTRE_MARK_LENGTH / 2), LINE_WIDTH / 2, CENTRE_MARK_LENGTH / 2)
        add(sign * (HALF_SINGLES - LINE_WIDTH / 2), 0.0, LINE_WIDTH / 2, HALF_LENGTH)
        add(sign * (HALF_DOUBLES - LINE_WIDTH / 2), 0.0, LINE_WIDTH / 2, HALF_LENGTH)
    add(0.0, 0.0, LINE_WIDTH / 2, SERVICE_LINE)
    return coverage


def traffic_wear(xs, zs):
    """Where feet actually go: behind each baseline and, less so, around the service line."""
    grid_x, grid_z = np.meshgrid(xs, zs)
    wear = np.zeros_like(grid_x, dtype=np.float32)
    for sign in (-1.0, 1.0):
        wear += np.exp(-((grid_z - sign * (HALF_LENGTH + 0.5)) ** 2) / (2 * 1.6 ** 2)) * np.exp(-(grid_x ** 2) / (2 * 3.2 ** 2))
        wear += 0.35 * np.exp(-((grid_z - sign * SERVICE_LINE) ** 2) / (2 * 1.8 ** 2)) * np.exp(-(grid_x ** 2) / (2 * 2.2 ** 2))
    return np.clip(wear, 0.0, 1.0)


def linear_to_srgb(colour):
    """An 8-bit image stores exactly these values, and Unity decodes them from sRGB on import."""
    colour = np.clip(colour, 0.0, 1.0)
    return np.where(colour <= 0.0031308, colour * 12.92, 1.055 * np.power(colour, 1.0 / 2.4) - 0.055)


def save(pixels, path, colorspace):
    height, width = pixels.shape[:2]
    image = bpy.data.images.new(os.path.basename(path), width, height, alpha=True)
    image.colorspace_settings.name = colorspace
    image.pixels.foreach_set(pixels.astype(np.float32).ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)
    log(f"wrote {path}")


def make_base_textures(plane, art_dir):
    width, height = BASE_WIDTH_PX, BASE_LENGTH_PX
    px_x = SURFACE_WIDTH / width
    px_z = SURFACE_LENGTH / height
    xs = (np.arange(width) + 0.5) * px_x - SURFACE_WIDTH / 2
    zs = (np.arange(height) + 0.5) * px_z - SURFACE_LENGTH / 2

    log("baking noise layers")
    blotches = bake_noise(plane, width, height, 0.35, 6.0, 0.55, offset=1.3)
    mottling = bake_noise(plane, width, height, 3.0, 8.0, 0.6, offset=7.1)
    # Stretched across the court: the long overlapping strokes left by the application squeegee.
    strokes = bake_noise(plane, width, height, 1.2, 4.0, 0.5, stretch=(0.12, 1.0, 1.0), offset=4.9)
    scuffs = bake_noise(plane, width, height, 9.0, 8.0, 0.65, offset=2.2)

    lines = line_coverage(xs, zs, px_x, px_z)
    wear = traffic_wear(xs, zs)

    in_play = rect(xs, zs, 0.0, 0.0, HALF_DOUBLES, HALF_LENGTH, px_x, px_z)[..., None]
    colour = SURROUND_COLOUR * (1.0 - in_play) + PLAY_COLOUR * in_play

    variation = 1.0 + 0.16 * (blotches - 0.5) + 0.12 * (mottling - 0.5) + 0.09 * (strokes - 0.5)
    colour = colour * variation[..., None]

    # Footwork grinds the paint paler and greyer where the scuff noise lets it through.
    scuffed = (wear * np.clip((scuffs - 0.3) / 0.5, 0.0, 1.0))[..., None]
    grey = colour.mean(axis=-1, keepdims=True)
    colour = colour * (1.0 - 0.45 * scuffed) + (grey * 1.25 + 0.012) * 0.45 * scuffed

    # Paint is not perfectly opaque, and traffic dulls it too.
    paint = lines * (0.97 - 0.14 * wear * scuffs - 0.05 * mottling)
    colour = colour * (1.0 - paint[..., None]) + LINE_COLOUR * variation[..., None] * paint[..., None]

    alpha = np.ones((height, width, 1), dtype=np.float32)
    save(np.concatenate([linear_to_srgb(colour), alpha], axis=-1), os.path.join(art_dir, "Court_BaseColor.png"), "sRGB")

    # Smoother where traffic has polished the grit, and on the line paint.
    smoothness = 0.26 + 0.05 * (mottling - 0.5) + 0.10 * wear * scuffs + 0.08 * lines
    mask = np.zeros((height, width, 4), dtype=np.float32)
    mask[..., 1] = 1.0
    mask[..., 3] = np.clip(smoothness, 0.0, 1.0)
    save(mask, os.path.join(art_dir, "Court_Mask.png"), "Non-Color")


def make_tileable(layer, blend_px):
    """Crossfades the layer's edges into each other so the texture repeats without a seam."""
    size = layer.shape[0] - blend_px
    ramp = np.linspace(0.0, 1.0, blend_px, dtype=np.float32)
    result = layer[:size, :size].copy()
    result[:blend_px, :] = result[:blend_px, :] * ramp[:, None] + layer[size:, :size] * (1 - ramp)[:, None]
    result[:, :blend_px] = result[:, :blend_px] * ramp[None, :] + layer[:size, size:] * (1 - ramp)[None, :]
    return result


def make_detail_textures(plane, art_dir):
    log("baking detail")
    # A larger bake is cropped back to DETAIL_SIZE after the crossfade uses up its margin.
    margin = 128
    bake_size = DETAIL_SIZE + margin
    metres_per_px = DETAIL_TILE_METRES / DETAIL_SIZE
    # The bake plane spans the whole court, so scale the noise down to this tile's size.
    grit = bake_noise(plane, bake_size, bake_size, 900.0, 3.0, 0.7, stretch=(1.0, 2.0, 1.0), offset=11.0)
    grain = bake_noise(plane, bake_size, bake_size, 240.0, 4.0, 0.6, stretch=(1.0, 2.0, 1.0), offset=3.3)

    height_map = make_tileable(0.7 * grit + 0.3 * grain, margin)

    gradient_x = (np.roll(height_map, -1, axis=1) - np.roll(height_map, 1, axis=1)) * 0.5
    gradient_y = (np.roll(height_map, -1, axis=0) - np.roll(height_map, 1, axis=0)) * 0.5
    nx = -gradient_x * DETAIL_NORMAL_STRENGTH
    ny = -gradient_y * DETAIL_NORMAL_STRENGTH
    length = np.sqrt(nx * nx + ny * ny + 1.0)
    normal = np.stack([nx / length * 0.5 + 0.5, ny / length * 0.5 + 0.5, 1.0 / length * 0.5 + 0.5, np.ones_like(nx)], axis=-1)
    save(normal, os.path.join(art_dir, "Court_Detail_Normal.png"), "Non-Color")

    # URP multiplies by 2 x detail albedo, so 0.5 is neutral.
    grey = 0.5 + 0.10 * (height_map - height_map.mean())
    albedo = np.stack([grey, grey, grey, np.ones_like(grey)], axis=-1)
    save(albedo, os.path.join(art_dir, "Court_Detail_Albedo.png"), "Non-Color")
    log(f"detail tile: {DETAIL_TILE_METRES} m, {metres_per_px * 1000:.1f} mm/px")


def main():
    art_dir = sys.argv[sys.argv.index("--") + 1]
    os.makedirs(art_dir, exist_ok=True)
    plane = setup_bake_scene()
    make_base_textures(plane, art_dir)
    make_detail_textures(plane, art_dir)
    log("done")


main()
