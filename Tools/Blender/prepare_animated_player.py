"""
Step 1 for a player that comes rigged and animated, on a Mixamo skeleton as Tripo exports it: scales it to real size,
writes its textures, and keeps its skin weights and clips for the rigging step.

    blender -b --factory-startup --python prepare_animated_player.py -- <player.glb> <work_dir> <art_dir>

Writes <work_dir>/player_animated.blend with the mesh "Player" at real size, its vertex groups still named after the
source bones, and the source armature "Source" with every clip, scaled the same. rig_and_animate.py builds the game's
skeleton on the source's joints, moves the weights onto it and carries the clips over.
"""
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import prepare_meshes as meshes  # noqa: E402


def log(message):
    print(f"[animated] {message}", flush=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    source, work_dir, art_dir = argv[:3]
    meshes.reset_scene()
    bpy.ops.import_scene.gltf(filepath=source)

    armature = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
    body = next(o for o in bpy.context.scene.objects if o.type == "MESH" and o.parent == armature)
    for obj in list(bpy.context.scene.objects):
        if obj not in (armature, body):
            bpy.data.objects.remove(obj)

    # Measured in the rest pose: the clips would otherwise move the mesh.
    armature.animation_data.action = None
    for bone in armature.pose.bones:
        bone.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    low, high = meshes.world_bounds(body)
    scale = meshes.PLAYER_HEIGHT_M / float(high[2] - low[2])
    # On the armature object, so every clip's bone translations scale with it.
    armature.scale = [s * scale for s in armature.scale]
    bpy.context.view_layer.update()
    log(f"player scaled by {scale:.3f} to {meshes.PLAYER_HEIGHT_M} m")

    # The mesh at real size in its own right, unbound, with the source's weights kept as vertex groups.
    world = body.matrix_world.copy()
    body.parent = None
    body.data.transform(world)
    body.matrix_world = Matrix.Identity(4)
    for modifier in list(body.modifiers):
        body.modifiers.remove(modifier)
    body.name = "Player"
    body.data.name = "Player"
    armature.name = "Source"

    images = meshes.find_images(body.data.materials[0])
    log("source textures: " + ", ".join(f"{k}={v.size[0]}x{v.size[1]}" for k, v in images.items()))
    size = meshes.PLAYER_TEXTURE_SIZE
    os.makedirs(art_dir, exist_ok=True)
    base = meshes.image_rgba(images["base"], size)
    base[..., 3] = 1.0
    meshes.save_rgba(base, os.path.join(art_dir, "Player_BaseColor.png"))
    meshes.save_rgba(meshes.recolor_clothes(base, (1.0, 0.42, 0.28)), os.path.join(art_dir, "Player_BaseColor_Red.png"))
    meshes.save_unity_mask(meshes.image_rgba(images["orm"], size), os.path.join(art_dir, "Player_Mask.png"))
    normal = meshes.image_rgba(images["normal"], size)
    normal[..., 3] = 1.0
    meshes.save_rgba(normal, os.path.join(art_dir, "Player_Normal.png"), "Non-Color")

    for action in bpy.data.actions:
        action.use_fake_user = True
        log(f"clip {action.name}: frames {action.frame_range[0]:.0f}-{action.frame_range[1]:.0f}")
    bpy.context.scene.render.fps = 24
    bpy.ops.outliner.orphans_purge(do_recursive=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work_dir, "player_animated.blend"))
    log("done")


main()
