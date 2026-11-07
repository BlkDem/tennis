"""
Renders the crowd: the rigged player in spectator poses, seen from the front and from behind, packed
into one atlas plus a mask (R shirt, G shorts, B hair) that lets the game give every spectator their own colours.

    blender -b --factory-startup --python make_crowd_atlas.py -- <work_dir> <player_art_dir> <stadium_art_dir>

Needs work_dir/player_rigged.blend from rig_and_animate.py. Poses are authored with its solver,
in the same frame: r = right, f = forward, u = up, metres from the feet.
Atlas layout (must match Crowd.shader): columns are poses, the top row is the front view, the bottom row the back.
"""
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rig_and_animate as rig_tools  # noqa: E402

CELL_WIDTH = 192
CELL_HEIGHT = 384
# A cell covers this much of the world: the in-game quad has the same size, with its bottom on the row's floor.
CELL_METRES_HIGH = 2.4

# Seated: hip joint at seat height, shins down to the row floor.
SEATED_BASE = dict(
    pelvis_pos=(0.0, -0.02, -0.40),
    pelvis_rot=(-4.0, 0.0, 0.0),
    spine_rot=(4.0, 0.0, 0.0),
    chest_rot=(2.0, 0.0, 0.0),
    head_rot=(-2.0, 0.0, 0.0),
    foot_l=(-0.17, 0.30, 0.19),
    foot_r=(0.17, 0.30, 0.19),
    foot_l_rot=(0.0, -10.0),
    foot_r_rot=(0.0, 10.0),
    two_hand=(0.0,),
)


def seated(**overrides):
    pose = dict(SEATED_BASE)
    pose.update(overrides)
    return rig_tools.make_pose(**pose)


POSES = [
    # 0: sitting, hands on the thighs.
    seated(
        grip=(0.17, 0.2, 0.56), racket=(0.0, 1.0, -0.1), face=(0.0, 0.1, 1.0),
        hand_l=(-0.17, 0.2, 0.56), elbow_r=(0.6, -0.4, -0.5), elbow_l=(-0.6, -0.4, -0.5),
    ),
    # 1: leaning forward, forearms on the knees.
    seated(
        pelvis_rot=(4.0, 0.0, 0.0), spine_rot=(14.0, 0.0, 0.0), chest_rot=(10.0, 0.0, 0.0), head_rot=(-20.0, 0.0, 0.0),
        grip=(0.05, 0.36, 0.66), racket=(-1.0, 0.2, 0.0), face=(0.0, 0.0, 1.0),
        hand_l=(-0.05, 0.36, 0.66), elbow_r=(0.6, 0.2, -0.6), elbow_l=(-0.6, 0.2, -0.6),
    ),
    # 2: clapping, hands apart.
    seated(
        grip=(0.15, 0.3, 0.98), racket=(0.0, 0.2, 1.0), face=(-1.0, 0.0, 0.0),
        hand_l=(-0.15, 0.3, 0.98), elbow_r=(0.7, -0.3, -0.6), elbow_l=(-0.7, -0.3, -0.6),
        fingers_r=(10.0,), fingers_l=(10.0,),
    ),
    # 3: clapping, hands together.
    seated(
        grip=(0.025, 0.32, 1.0), racket=(0.0, 0.2, 1.0), face=(-1.0, 0.0, 0.0),
        hand_l=(-0.025, 0.32, 1.0), elbow_r=(0.7, -0.3, -0.6), elbow_l=(-0.7, -0.3, -0.6),
        fingers_r=(10.0,), fingers_l=(10.0,),
    ),
    # 4: on their feet, arms up.
    rig_tools.make_pose(
        pelvis_pos=(0.0, 0.0, -0.02), pelvis_rot=(1.0, 0.0, 0.0), spine_rot=(-3.0, 0.0, 0.0), chest_rot=(-4.0, 0.0, 0.0),
        head_rot=(-12.0, 0.0, 0.0), foot_l=(-0.18, 0.0, 0.225), foot_r=(0.18, 0.0, 0.225),
        grip=(0.36, 0.1, 1.98), racket=(0.15, 0.1, 1.0), face=(-1.0, 0.0, 0.0),
        hand_l=(-0.36, 0.1, 1.98), two_hand=(0.0,), elbow_r=(0.9, 0.0, -0.2), elbow_l=(-0.9, 0.0, -0.2),
        fingers_r=(40.0,), fingers_l=(40.0,),
    ),
]


def log(message):
    print(f"[crowd] {message}", flush=True)


def clothing_mask(player, base_color_path):
    """
    Per corner: red marks the shirt, green the shorts. The kit is white, so whiteness in the texture
    separates cloth from skin and hair, and the height in the rest pose separates shirt from shorts.
    """
    image = bpy.data.images.load(base_color_path, check_existing=True)
    width, height = image.size
    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    pixels = pixels.reshape(height, width, 4)[..., :3]

    mesh = player.data
    loops = len(mesh.loops)
    uvs = np.empty(loops * 2, dtype=np.float32)
    mesh.uv_layers.active.data.foreach_get("uv", uvs)
    uvs = uvs.reshape(loops, 2)
    px = np.clip((uvs[:, 0] % 1.0) * width, 0, width - 1).astype(np.int64)
    py = np.clip((uvs[:, 1] % 1.0) * height, 0, height - 1).astype(np.int64)
    colour = pixels[py, px]

    brightest = colour.max(axis=1)
    darkest = colour.min(axis=1)
    saturation = (brightest - darkest) / np.maximum(brightest, 1e-4)
    whiteness = np.clip((0.3 - saturation) / 0.15, 0.0, 1.0) * np.clip((brightest - 0.45) / 0.2, 0.0, 1.0)

    vertex_index = np.empty(loops, dtype=np.int64)
    mesh.loops.foreach_get("vertex_index", vertex_index)
    positions = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
    mesh.vertices.foreach_get("co", positions)
    positions = positions.reshape(-1, 3)[vertex_index]
    up = positions[:, 2]
    across = np.abs(positions[:, 0])

    shirt = whiteness * (up > 0.97) * (up < 1.58) * ~((across > 0.3) & (up < 1.3))
    shorts = whiteness * (up > 0.55) * (up <= 0.97)
    # Hair: dark and brownish, on the head.
    hair = np.clip((0.42 - brightest) / 0.12, 0.0, 1.0) * (colour[:, 0] >= colour[:, 2] - 0.02) * (up > 1.6)

    attribute = mesh.color_attributes.new("ClothMask", "FLOAT_COLOR", "CORNER")
    data = np.zeros((loops, 4), dtype=np.float32)
    data[:, 0] = shirt
    data[:, 1] = shorts
    data[:, 2] = hair
    data[:, 3] = 1.0
    attribute.data.foreach_set("color", data.ravel())
    mesh.color_attributes.active_color = attribute
    log(f"mask: shirt {np.mean(shirt > 0.5):.1%} of corners, shorts {np.mean(shorts > 0.5):.1%}, hair {np.mean(hair > 0.5):.1%}")


def setup_render():
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = CELL_WIDTH
    scene.render.resolution_y = CELL_HEIGHT
    scene.render.film_transparent = True
    scene.display.render_aa = "16"
    scene.view_settings.view_transform = "Standard"
    scene.display.shading.show_specular_highlight = False

    data = bpy.data.cameras.new("crowd")
    data.type = "ORTHO"
    data.ortho_scale = CELL_METRES_HIGH
    camera = bpy.data.objects.new("crowd", data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    return scene, camera


def aim(camera, front):
    # The model faces -Y in Blender. Level views: the stands are seen from about their own height.
    y = -10.0 if front else 10.0
    camera.location = Vector((0.0, y, CELL_METRES_HIGH * 0.5))
    camera.rotation_euler = (Vector((0.0, 0.0, CELL_METRES_HIGH * 0.5)) - camera.location).to_track_quat("-Z", "Y").to_euler()


def render_cell(scene, path):
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(path, check_existing=False)
    pixels = np.empty(CELL_WIDTH * CELL_HEIGHT * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    bpy.data.images.remove(image)
    os.remove(path)
    return pixels.reshape(CELL_HEIGHT, CELL_WIDTH, 4)


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


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    work_dir, player_art_dir, art_dir = argv[0], argv[1], argv[2]
    os.makedirs(art_dir, exist_ok=True)

    bpy.ops.wm.open_mainfile(filepath=os.path.join(work_dir, "player_rigged.blend"))
    rig = bpy.data.objects["Rig"]
    player = bpy.data.objects["Player"]
    for name in ("Racket", "Strings"):
        bpy.data.objects[name].hide_render = True

    clothing_mask(player, os.path.join(player_art_dir, "Player_BaseColor.png"))

    solver = rig_tools.Solver(rig)
    rig_tools.begin_action(rig, "CrowdPoses")
    for frame, pose in enumerate(POSES):
        solver.apply(solver.solve(pose), frame)

    scene, camera = setup_render()
    columns = len(POSES)
    atlas = np.zeros((CELL_HEIGHT * 2, CELL_WIDTH * columns, 4), dtype=np.float32)
    mask = np.zeros_like(atlas)
    temp = os.path.join(art_dir, "_crowd_cell.png")

    for column in range(columns):
        scene.frame_set(column)
        for row, front in ((1, True), (0, False)):
            aim(camera, front)
            y0, x0 = row * CELL_HEIGHT, column * CELL_WIDTH

            scene.view_settings.view_transform = "Standard"
            scene.display.shading.light = "STUDIO"
            scene.display.shading.color_type = "TEXTURE"
            atlas[y0:y0 + CELL_HEIGHT, x0:x0 + CELL_WIDTH] = render_cell(scene, temp)

            # Raw output keeps the mask as plain 0..1 numbers rather than display-encoded colour.
            scene.view_settings.view_transform = "Raw"
            scene.display.shading.light = "FLAT"
            scene.display.shading.color_type = "VERTEX"
            mask[y0:y0 + CELL_HEIGHT, x0:x0 + CELL_WIDTH] = render_cell(scene, temp)

    save(atlas, os.path.join(art_dir, "Crowd_Atlas.png"), "sRGB")
    save(mask, os.path.join(art_dir, "Crowd_Mask.png"), "Non-Color")
    log(f"{columns} poses x 2 views, cell {CELL_WIDTH}x{CELL_HEIGHT} px = {CELL_METRES_HIGH / 2:.2f} x {CELL_METRES_HIGH:.2f} m")


main()
