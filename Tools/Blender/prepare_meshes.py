"""
Step 1 of the art pipeline: turn the raw generated scans into game-ready meshes.

The source GLBs are 1.5M triangles at arbitrary scale, and the racket is fused with a ball.
This produces low-poly meshes at real-world size with detail baked into normal maps.

    blender -b --factory-startup --python prepare_meshes.py -- <player.glb> <racket.glb> <work_dir> <art_dir> [all|player|racket]

Writes <work_dir>/player_low.blend and racket_low.blend, plus textures into <art_dir>.
It is slow, so it runs once; the rigging step reads its output.
"""
import math
import os
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Vector

PLAYER_HEIGHT_M = 1.85
PLAYER_TRIANGLES = 30000
PLAYER_TEXTURE_SIZE = 2048

RACKET_LENGTH_M = 0.685
RACKET_TRIANGLES = 3000
RACKET_TEXTURE_SIZE = 1024

RACKET_FRAME_WIDTH_SOURCE = 0.032
RACKET_GRIP_FROM_BUTT_SOURCE = 0.12
RACKET_HEAD_STARTS_SOURCE_Y = -0.13
STRING_SPACING_M = 0.016
STRING_WIDTH_M = 0.002
STRING_TEXTURE_SIZE = 64

BALL_VISUAL_DIAMETER_M = 0.067
BALL_TRIANGLES = 800

def log(message):
    print(f"[prepare] {message}", flush=True)


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_single_mesh(path):
    bpy.ops.import_scene.gltf(filepath=path)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise RuntimeError(f"Expected one mesh in {path}, found {len(meshes)}")
    obj = meshes[0]
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return obj


def world_bounds(obj):
    coords = np.empty(len(obj.data.vertices) * 3, dtype=np.float32)
    obj.data.vertices.foreach_get("co", coords)
    coords = coords.reshape(-1, 3)
    return coords.min(axis=0), coords.max(axis=0)


def duplicate(obj, name):
    copy = obj.copy()
    copy.data = obj.data.copy()
    copy.name = name
    bpy.context.scene.collection.objects.link(copy)
    return copy


def weld(obj, distance=1e-5):
    """glTF stores seams as split vertices, which would tear open when decimated."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    before = len(bm.verts)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=distance)
    log(f"welded {before} -> {len(bm.verts)} vertices")
    bm.to_mesh(obj.data)
    bm.free()


def decimate(obj, target_triangles):
    triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    modifier = obj.modifiers.new("decimate", "DECIMATE")
    modifier.decimate_type = "COLLAPSE"
    modifier.ratio = min(1.0, target_triangles / triangles)
    modifier.use_collapse_triangulate = True
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    log(f"decimated {triangles} -> {len(obj.data.polygons)} triangles")


def shade_smooth(obj):
    obj.data.shade_smooth()
    if hasattr(obj.data, "has_custom_normals") and obj.data.has_custom_normals:
        bpy.context.view_layer.objects.active = obj
        bpy.ops.mesh.customdata_custom_splitnormals_clear()


def find_images(material):
    """Pulls the glTF PBR images out of the imported node tree by their role."""
    found = {}
    nodes = material.node_tree.nodes
    principled = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")

    def upstream_image(socket, depth=0):
        if depth > 6 or not socket.is_linked:
            return None
        node = socket.links[0].from_node
        if node.type == "TEX_IMAGE":
            return node.image
        for inner in node.inputs:
            image = upstream_image(inner, depth + 1)
            if image is not None:
                return image
        return None

    found["base"] = upstream_image(principled.inputs["Base Color"])
    found["normal"] = upstream_image(principled.inputs["Normal"])
    found["orm"] = upstream_image(principled.inputs["Roughness"])
    return found


def image_rgba(image, size):
    """Returns the image as a float array, top row last, resampled to size x size."""
    copy = image.copy()
    copy.scale(size, size)
    pixels = np.empty(size * size * 4, dtype=np.float32)
    copy.pixels.foreach_get(pixels)
    bpy.data.images.remove(copy)
    return pixels.reshape(size, size, 4)


def save_rgba(pixels, path, colorspace="sRGB"):
    height, width = pixels.shape[:2]
    image = bpy.data.images.new(os.path.basename(path), width, height, alpha=True)
    image.colorspace_settings.name = colorspace
    image.pixels.foreach_set(pixels.astype(np.float32).ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)
    log(f"wrote {path}")


def save_unity_mask(orm, path):
    """glTF packs roughness in G and metallic in B; URP wants metallic in R and smoothness in A."""
    mask = np.zeros_like(orm)
    mask[..., 0] = orm[..., 2]
    mask[..., 1] = 1.0
    mask[..., 2] = 0.0
    mask[..., 3] = 1.0 - orm[..., 1]
    save_rgba(mask, path, "Non-Color")


def bake_normals(high, low, size, path):
    """Bakes the scan's full surface detail, including its normal map, into the low-poly mesh's UVs."""
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 1
    scene.cycles.use_denoising = False
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        prefs.get_devices()
        for device_type in ("OPTIX", "CUDA", "HIP", "ONEAPI"):
            try:
                prefs.compute_device_type = device_type
            except TypeError:
                continue
            if any(d.type != "CPU" for d in prefs.devices):
                for d in prefs.devices:
                    d.use = True
                scene.cycles.device = "GPU"
                log(f"baking on {device_type}")
                break
    except Exception as error:  # A CPU bake is slower but gives the same result.
        log(f"GPU setup skipped: {error}")

    baked = bpy.data.images.new("baked_normal", size, size, alpha=False, float_buffer=False)
    baked.colorspace_settings.name = "Non-Color"

    # Baking writes to whichever image node is active on the low-poly material.
    low.data.materials[0] = low.data.materials[0].copy()
    tree = low.data.materials[0].node_tree
    node = tree.nodes.new("ShaderNodeTexImage")
    node.image = baked
    tree.nodes.active = node

    bpy.ops.object.select_all(action="DESELECT")
    high.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low

    bpy.ops.object.bake(
        type="NORMAL",
        use_selected_to_active=True,
        cage_extrusion=0.015,
        max_ray_distance=0.03,
        normal_space="TANGENT",
        margin=16,
        margin_type="EXTEND",
    )

    pixels = np.empty(size * size * 4, dtype=np.float32)
    baked.pixels.foreach_get(pixels)
    pixels = pixels.reshape(size, size, 4)
    pixels[..., 3] = 1.0
    save_rgba(pixels, path, "Non-Color")
    tree.nodes.remove(node)


def recolor_clothes(base, tint):
    """Second player's kit: grey cloth takes a colour, while saturated skin, hair and shoes' accents are left alone."""
    rgb = base[..., :3]
    maximum = rgb.max(axis=-1)
    minimum = rgb.min(axis=-1)
    saturation = (maximum - minimum) / np.maximum(maximum, 1e-4)
    # sRGB greys of the kit, with a soft edge so seams do not show a hard outline.
    cloth = np.clip((0.16 - saturation) / 0.08, 0.0, 1.0) * np.clip((maximum - 0.25) / 0.1, 0.0, 1.0)
    result = base.copy()
    tinted = rgb * np.array(tint, dtype=np.float32)
    result[..., :3] = rgb * (1.0 - cloth[..., None]) + tinted * cloth[..., None]
    return result


def prepare_player(source, work_dir, art_dir):
    reset_scene()
    high = import_single_mesh(source)
    low_bound, high_bound = world_bounds(high)
    height = float(high_bound[2] - low_bound[2])
    scale = PLAYER_HEIGHT_M / height
    high.scale = (scale, scale, scale)
    bpy.ops.object.select_all(action="DESELECT")
    high.select_set(True)
    bpy.context.view_layer.objects.active = high
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    log(f"player scaled by {scale:.3f} to {PLAYER_HEIGHT_M} m")

    images = find_images(high.data.materials[0])
    log("source textures: " + ", ".join(f"{k}={v.size[0]}x{v.size[1]}" for k, v in images.items()))

    low = duplicate(high, "Player")
    weld(low)
    decimate(low, PLAYER_TRIANGLES)
    shade_smooth(low)

    os.makedirs(art_dir, exist_ok=True)
    base = image_rgba(images["base"], PLAYER_TEXTURE_SIZE)
    base[..., 3] = 1.0
    save_rgba(base, os.path.join(art_dir, "Player_BaseColor.png"))
    save_rgba(recolor_clothes(base, (1.0, 0.42, 0.28)), os.path.join(art_dir, "Player_BaseColor_Red.png"))
    save_unity_mask(image_rgba(images["orm"], PLAYER_TEXTURE_SIZE), os.path.join(art_dir, "Player_Mask.png"))

    bake_normals(high, low, PLAYER_TEXTURE_SIZE, os.path.join(art_dir, "Player_Normal.png"))

    bpy.data.objects.remove(high)
    bpy.ops.outliner.orphans_purge(do_recursive=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work_dir, "player_low.blend"))
    log("player done")


def split_shells(obj):
    """Separates loose parts into their own objects, largest first. The scan's ball is its own shell."""
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.separate(type="LOOSE")
    bpy.ops.object.mode_set(mode="OBJECT")
    parts = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    parts.sort(key=lambda o: len(o.data.vertices), reverse=True)
    log("shells: " + ", ".join(str(len(o.data.vertices)) for o in parts))
    return parts


def vertex_array(obj):
    coords = np.empty(len(obj.data.vertices) * 3, dtype=np.float64)
    obj.data.vertices.foreach_get("co", coords)
    return coords.reshape(-1, 3)


def head_silhouette(coords, row_size=0.004):
    """For each row across the racket head, the X range the frame occupies: the head's real outline."""
    head = coords[coords[:, 1] >= RACKET_HEAD_STARTS_SOURCE_Y]
    y_low, y_high = head[:, 1].min(), head[:, 1].max()
    rows = int((y_high - y_low) / row_size) + 1
    x_min = np.full(rows, np.inf)
    x_max = np.full(rows, -np.inf)
    indices = ((head[:, 1] - y_low) / row_size).astype(np.int64)
    np.minimum.at(x_min, indices, head[:, 0])
    np.maximum.at(x_max, indices, head[:, 0])
    return y_low, row_size, x_min, x_max


def inside_silhouette(xs, ys, silhouette):
    y_low, row_size, x_min, x_max = silhouette
    rows = ((ys - y_low) / row_size).astype(np.int64)
    valid = (rows >= 0) & (rows < x_min.size)
    result = np.zeros(xs.shape, dtype=bool)
    result[valid] = (xs[valid] >= x_min[rows[valid]]) & (xs[valid] <= x_max[rows[valid]])
    return result


def string_vertices(coords, silhouette):
    """A vertex is string, not frame, if it stays inside the head outline when moved one frame width in every direction."""
    width = RACKET_FRAME_WIDTH_SOURCE
    inner = coords[:, 1] >= RACKET_HEAD_STARTS_SOURCE_Y
    for angle in np.linspace(0.0, 2.0 * math.pi, 16, endpoint=False):
        inner &= inside_silhouette(coords[:, 0] + width * math.cos(angle), coords[:, 1] + width * math.sin(angle), silhouette)
    return inner


def delete_flagged_vertices(obj, flags):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    doomed = [v for v in bm.verts if flags[v.index]]
    bmesh.ops.delete(bm, geom=doomed, context="VERTS")
    log(f"deleted {len(doomed)} string vertices, {len(bm.verts)} remain")
    bm.to_mesh(obj.data)
    bm.free()


def inner_outline(silhouette, samples=64):
    """Walks round the opening's edge: the head outline pulled in by the frame width."""
    y_low, row_size, x_min, x_max = silhouette
    finite = np.isfinite(x_min)
    width = RACKET_FRAME_WIDTH_SOURCE * 0.95
    y_values = y_low + (np.arange(x_min.size) + 0.5) * row_size
    centre_y = (y_values[finite][0] + y_values[finite][-1]) * 0.5
    centre_x = float(np.mean((x_min[finite] + x_max[finite]) * 0.5))

    outline = []
    for angle in np.linspace(0.0, 2.0 * math.pi, samples, endpoint=False):
        direction = np.array([math.cos(angle), math.sin(angle)])
        radius = 0.0
        while radius < 0.6:
            probe = np.array([centre_x, centre_y]) + direction * (radius + 0.002)
            hit = False
            for ring in np.linspace(0.0, 2.0 * math.pi, 12, endpoint=False):
                test = probe + width * np.array([math.cos(ring), math.sin(ring)])
                if not inside_silhouette(np.array([test[0]]), np.array([test[1]]), silhouette)[0]:
                    hit = True
                    break
            if hit:
                break
            radius += 0.002
        outline.append((centre_x + direction[0] * radius, centre_y + direction[1] * radius))
    return outline, (centre_x, centre_y)


def add_string_plane(outline, centre, z, scale):
    """A flat fan over the opening with UVs in units of string spacing, so the string texture tiles at true size."""
    bm = bmesh.new()
    uv_layer = bm.loops.layers.uv.new("UVMap")
    hub = bm.verts.new((centre[0], centre[1], z))
    rim = [bm.verts.new((x, y, z)) for x, y in outline]
    for i in range(len(rim)):
        face = bm.faces.new((hub, rim[i], rim[(i + 1) % len(rim)]))
        for loop in face.loops:
            co = loop.vert.co
            loop[uv_layer].uv = (co.x * scale / STRING_SPACING_M, co.y * scale / STRING_SPACING_M)
    mesh = bpy.data.meshes.new("Strings")
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new("Strings", mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def save_string_texture(path):
    """One cell of the string bed: stored white, with alpha carrying the strings."""
    size = STRING_TEXTURE_SIZE
    half = STRING_WIDTH_M / STRING_SPACING_M * size * 0.5
    distance = np.abs(np.arange(size) + 0.5 - size * 0.5)
    profile = np.clip(half + 0.5 - distance, 0.0, 1.0)
    alpha = np.maximum(profile[None, :], profile[:, None]).astype(np.float32)
    pixels = np.ones((size, size, 4), dtype=np.float32)
    pixels[..., 3] = alpha
    save_rgba(pixels, path)


def spherify(obj, radius):
    """The scanned ball is lumpy; pushing every vertex onto a true sphere keeps its felt texture and UVs."""
    coords = vertex_array(obj)
    centre = (coords.min(axis=0) + coords.max(axis=0)) * 0.5
    offsets = coords - centre
    offsets *= radius / np.linalg.norm(offsets, axis=1, keepdims=True)
    obj.data.vertices.foreach_set("co", offsets.astype(np.float32).ravel())
    obj.data.update()


def apply_transform_of(obj):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=True)


def prepare_racket(source, work_dir, art_dir):
    reset_scene()
    obj = import_single_mesh(source)
    images = find_images(obj.data.materials[0])
    log("source textures: " + ", ".join(f"{k}={v.size[0]}x{v.size[1]}" for k, v in images.items()))

    weld(obj)
    parts = split_shells(obj)
    racket, ball = parts[0], parts[1]
    racket.name = "Racket"
    ball.name = "Ball"
    for extra in parts[2:]:
        bpy.data.objects.remove(extra)

    coords = vertex_array(racket)
    y_butt, y_tip = coords[:, 1].min(), coords[:, 1].max()
    scale = RACKET_LENGTH_M / float(y_tip - y_butt)
    log(f"racket spans y {y_butt:.3f}..{y_tip:.3f}, scale {scale:.3f}")

    silhouette = head_silhouette(coords)
    delete_flagged_vertices(racket, string_vertices(coords, silhouette))
    outline, centre = inner_outline(silhouette)

    frame = vertex_array(racket)
    z_centre = float((frame[:, 2].min() + frame[:, 2].max()) * 0.5)
    grip_end = frame[frame[:, 1] < y_butt + 0.05]
    x_axis = float((grip_end[:, 0].min() + grip_end[:, 0].max()) * 0.5)

    # Origin at the point the hand grips, +Y towards the head, Z through the string bed.
    origin = Vector((x_axis, float(y_butt) + RACKET_GRIP_FROM_BUTT_SOURCE, z_centre))
    strings = add_string_plane(outline, centre, z_centre, scale)
    for part in (racket, strings):
        part.location = -origin * scale
        part.scale = (scale, scale, scale)
        apply_transform_of(part)

    decimate(racket, RACKET_TRIANGLES)
    shade_smooth(racket)
    log(f"racket dimensions {tuple(round(d, 3) for d in racket.dimensions)}")

    weld(ball)
    decimate(ball, BALL_TRIANGLES)
    spherify(ball, BALL_VISUAL_DIAMETER_M * 0.5)
    ball.location = (0.0, 0.0, 0.0)
    shade_smooth(ball)
    log(f"ball dimensions {tuple(round(d, 3) for d in ball.dimensions)}")

    base = image_rgba(images["base"], RACKET_TEXTURE_SIZE)
    base[..., 3] = 1.0
    save_rgba(base, os.path.join(art_dir, "Racket_BaseColor.png"))
    save_unity_mask(image_rgba(images["orm"], RACKET_TEXTURE_SIZE), os.path.join(art_dir, "Racket_Mask.png"))
    normal = image_rgba(images["normal"], RACKET_TEXTURE_SIZE)
    normal[..., 3] = 1.0
    save_rgba(normal, os.path.join(art_dir, "Racket_Normal.png"), "Non-Color")
    save_string_texture(os.path.join(art_dir, "Racket_Strings.png"))

    bpy.ops.outliner.orphans_purge(do_recursive=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work_dir, "racket_low.blend"))
    log("racket done")

def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    player_source, racket_source, work_dir, art_dir = argv[:4]
    only = argv[4] if len(argv) > 4 else "all"
    os.makedirs(work_dir, exist_ok=True)
    if only in ("all", "racket"):
        prepare_racket(racket_source, work_dir, art_dir)
    if only in ("all", "player"):
        prepare_player(player_source, work_dir, art_dir)


main()
