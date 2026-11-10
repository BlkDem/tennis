"""
Step 2 of the art pipeline: rigs the prepared player, skins the racket to its hand and authors every clip.

    blender -b --factory-startup --python rig_and_animate.py -- <work_dir> <art_dir> [contact_sheet.png]

Poses are authored in the game's frame, relative to the player's feet: r = to the player's right,
f = forward towards the opponent, u = up, in metres. Two-bone IK solves the limbs for every sampled
frame, so the racket's contact position can match the game's strike zone (StrikeZone in ContactPlanner.cs)
and feet in running clips really do plant at the game's movement speed (PlayerMovementTuning).
"""
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Euler, Matrix, Quaternion, Vector

FPS = 60

# Must match PlayerMovementTuning and ShotTuning in the game.
RUN_SPEED = 6.2
WALK_SPEED = 1.4
JOG_SPEED = 3.5
SWING_LATENCY = 0.14

# The gaits, slowest first; ProjectBuilder blends them by these speeds, which must match PlayerAnimationController.Gaits.
#   speed: m/s; cycle: seconds for two steps; stance: share of the cycle a foot is on the ground
#   front: share of the planted foot's travel ahead of the hip, the rest behind: a foot lands nearly under the body
#          and pushes off well behind it
#   lift: swing foot height; heel: how far the heel has peeled up as the foot pushes off; land_pitch: toes up at landing
#   width: half the gap between the feet; crouch, bounce: the hips' height in a run, lowest in mid-stance (m). A walk
#          has no crouch of its own: the hips ride over a nearly straight leg, highest in mid-stance, and drop only as
#          far as the leg needs to reach its foot (see reach_limited_hips)
#   lean, hip: degrees forward and of hip swing; arm: arm swing (m); side_turn: how far the hips turn into a sideways stride
# The cycles are short because this model's legs are: hip joint to ankle is 0.58 m, and a planted foot cannot travel
# further than that leg reaches either side of the hip.
GAITS = (
    dict(name="Walk", speed=WALK_SPEED, cycle=0.78, stance=0.60, front=0.40, lift=0.06, heel=0.10, land_pitch=10.0,
         width=0.10, crouch=0.0, bounce=0.0, lean=3.0, arm=0.12, hip=6.0, side_turn=55.0),
    dict(name="Jog", speed=JOG_SPEED, cycle=0.66, stance=0.27, front=0.40, lift=0.13, heel=0.10, land_pitch=4.0,
         width=0.11, crouch=0.035, bounce=0.02, lean=8.0, arm=0.16, hip=9.0, side_turn=65.0),
    dict(name="Run", speed=RUN_SPEED, cycle=0.56, stance=0.185, front=0.37, lift=0.20, heel=0.12, land_pitch=0.0,
         width=0.11, crouch=0.05, bounce=0.03, lean=14.0, arm=0.22, hip=12.0, side_turn=70.0),
)
# The least the knee bends with the foot planted, degrees: a leg locked straight reads as a stilt.
MIN_KNEE_BEND = 10.0
# The retreats travel this close to straight back, degrees clockwise from straight ahead, with the hips turned this far;
# must match PlayerAnimationController.RetreatSideAngle.
RETREAT_TRAVEL = 175.0
RETREAT_TURN = 140.0
# However far the hips turn, the chest and head turn back towards the court by at most this much, degrees: a spine
# twists only so far, and the head looks over the shoulder.
MAX_COUNTER_TURN = 85.0

MAX_BONES_PER_VERTEX = 4
# Nothing on the torso or legs lies further out than this, while the arm in the rest pose does.
BODY_HALF_WIDTH = 0.33


def log(message):
    print(f"[rig] {message}", flush=True)


def to_blender(v):
    """Game frame to armature space: the model faces -Y, and its right side is at -X."""
    return Vector((-v[0], -v[1], v[2]))


def body_rotation(lean_forward, yaw_right, roll_right):
    """Rotation in the character's frame, in degrees. Order: roll, then lean, then yaw."""
    yaw = Matrix.Rotation(math.radians(-yaw_right), 3, "Z")
    lean = Matrix.Rotation(math.radians(lean_forward), 3, "X")
    roll = Matrix.Rotation(math.radians(-roll_right), 3, "Y")
    return yaw @ lean @ roll


# ---------------------------------------------------------------- Skeleton


def side_joints(s):
    return {
        "clavicle": Vector((s * 0.03, 0.02, 1.44)),
        "shoulder": Vector((s * 0.20, 0.03, 1.425)),
        "elbow": Vector((s * 0.334, 0.04, 1.239)),
        "wrist": Vector((s * 0.447, 0.0, 1.061)),
        "knuckle": Vector((s * 0.475, -0.005, 1.0)),
        "fingertip": Vector((s * 0.505, -0.01, 0.925)),
        "hip": Vector((s * 0.115, 0.015, 0.80)),
        "knee": Vector((s * 0.145, 0.0, 0.555)),
        "ankle": Vector((s * 0.185, 0.05, 0.225)),
        "toe": Vector((s * 0.205, -0.09, 0.055)),
        "toetip": Vector((s * 0.215, -0.165, 0.05)),
    }


# name: (head, tail, parent, skin radius). Skin radius is how thick the body part is around the bone.
def bone_definitions():
    bones = {
        "Hips": (Vector((0, 0.015, 0.90)), Vector((0, 0.015, 1.02)), None, 0.17),
        "Spine": (Vector((0, 0.015, 1.02)), Vector((0, 0.015, 1.20)), "Hips", 0.17),
        "Chest": (Vector((0, 0.015, 1.20)), Vector((0, 0.025, 1.50)), "Spine", 0.20),
        "Neck": (Vector((0, 0.025, 1.50)), Vector((0, 0.0, 1.59)), "Chest", 0.06),
        "Head": (Vector((0, 0.0, 1.59)), Vector((0, -0.01, 1.85)), "Neck", 0.12),
    }
    for prefix, s in (("L_", 1.0), ("R_", -1.0)):
        j = side_joints(s)
        bones[prefix + "Clavicle"] = (j["clavicle"], j["shoulder"], "Chest", 0.07)
        bones[prefix + "UpperArm"] = (j["shoulder"], j["elbow"], prefix + "Clavicle", 0.065)
        bones[prefix + "LowerArm"] = (j["elbow"], j["wrist"], prefix + "UpperArm", 0.05)
        bones[prefix + "Hand"] = (j["wrist"], j["knuckle"], prefix + "LowerArm", 0.06)
        bones[prefix + "Fingers"] = (j["knuckle"], j["fingertip"], prefix + "Hand", 0.05)
        bones[prefix + "UpperLeg"] = (j["hip"], j["knee"], "Hips", 0.10)
        bones[prefix + "LowerLeg"] = (j["knee"], j["ankle"], prefix + "UpperLeg", 0.075)
        bones[prefix + "Foot"] = (j["ankle"], j["toe"], prefix + "LowerLeg", 0.06)
        bones[prefix + "Toes"] = (j["toe"], j["toetip"], prefix + "Foot", 0.05)
    return bones


SKIN_BONES = bone_definitions()
LIMB_PREFIXES = ("L_", "R_")


def racket_rest_frame():
    """The racket's rest pose in the open right hand: the handle crosses the palm towards the thumb."""
    j = side_joints(-1.0)
    along_hand = (j["fingertip"] - j["wrist"]).normalized()
    thumb_side = Vector((0.0, -1.0, 0.0))
    racket_y = (along_hand * 0.45 + thumb_side).normalized()
    racket_z = along_hand.cross(racket_y).normalized()
    racket_x = racket_y.cross(racket_z).normalized()
    origin = j["wrist"] + (j["knuckle"] - j["wrist"]) * 0.75
    return origin, Matrix((racket_x, racket_y, racket_z)).transposed()


def build_armature():
    data = bpy.data.armatures.new("Rig")
    rig = bpy.data.objects.new("Rig", data)
    bpy.context.scene.collection.objects.link(rig)
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")

    for name, (head, tail, parent, _) in SKIN_BONES.items():
        bone = data.edit_bones.new(name)
        bone.head = head
        bone.tail = tail
        bone.use_connect = False
    for name, (_, _, parent, _) in SKIN_BONES.items():
        if parent:
            data.edit_bones[name].parent = data.edit_bones[parent]

    origin, frame = racket_rest_frame()
    racket = data.edit_bones.new("Racket")
    matrix = frame.to_4x4()
    matrix.translation = origin
    racket.length = 0.3
    racket.matrix = matrix
    racket.parent = data.edit_bones["R_Hand"]

    bpy.ops.object.mode_set(mode="OBJECT")
    return rig


def segment_distances(points, head, tail):
    a = np.array(head, dtype=np.float64)
    ab = np.array(tail, dtype=np.float64) - a
    t = np.clip(((points - a) @ ab) / (ab @ ab), 0.0, 1.0)
    return np.linalg.norm(points - (a + t[:, None] * ab), axis=1)


def smooth_along_edges(mesh, weights, iterations):
    """Averages each vertex's weights with its neighbours', which softens the joints between body parts."""
    edge_count = len(mesh.data.edges)
    edges = np.empty(edge_count * 2, dtype=np.int64)
    mesh.data.edges.foreach_get("vertices", edges)
    edges = edges.reshape(-1, 2)
    neighbour_counts = np.zeros(weights.shape[0], dtype=np.float64)
    np.add.at(neighbour_counts, edges[:, 0], 1.0)
    np.add.at(neighbour_counts, edges[:, 1], 1.0)

    for _ in range(iterations):
        totals = np.zeros_like(weights)
        np.add.at(totals, edges[:, 0], weights[edges[:, 1]])
        np.add.at(totals, edges[:, 1], weights[edges[:, 0]])
        averages = totals / np.maximum(neighbour_counts, 1.0)[:, None]
        has_neighbours = neighbour_counts[:, None] > 0.0
        weights = np.where(has_neighbours, weights * 0.5 + averages * 0.5, weights)
    return weights / np.maximum(weights.sum(axis=1, keepdims=True), 1e-9)


def skin_player(mesh, rig):
    """
    The scan's clothes are layered open shells, on which Blender's heat weighting often fails.
    Instead each vertex is weighted by its distance to a bone relative to that body part's thickness.
    """
    count = len(mesh.data.vertices)
    points = np.empty(count * 3, dtype=np.float64)
    mesh.data.vertices.foreach_get("co", points)
    points = points.reshape(-1, 3)

    names = list(SKIN_BONES)
    weights = np.zeros((count, len(names)), dtype=np.float64)
    x, z = points[:, 0], points[:, 2]

    for index, name in enumerate(names):
        head, tail, _, radius = SKIN_BONES[name]
        distance = segment_distances(points, head, tail)
        weight = 1.0 / np.maximum(distance / radius, 0.05) ** 5

        allowed = np.ones(count, dtype=bool)
        if name.startswith(LIMB_PREFIXES):
            side = 1.0 if name.startswith("L_") else -1.0
            allowed &= x * side > -0.02
        if "Leg" in name or "Foot" in name or "Toes" in name:
            allowed &= z < 0.97
            allowed &= np.abs(x) < BODY_HALF_WIDTH
        if name in ("Hips", "Spine", "Chest"):
            # Thick bones would otherwise out-vote the thin ones and pin the hands to the rest pose.
            allowed &= np.abs(x) < BODY_HALF_WIDTH
        if "Arm" in name or "Hand" in name or "Fingers" in name:
            allowed &= np.abs(x) > 0.13
            allowed &= z > 0.85
        if name in ("Neck", "Head"):
            allowed &= z > 1.38
        if name == "Hips":
            allowed &= z > 0.62
        weights[:, index] = weight * allowed

    weights /= np.maximum(weights.sum(axis=1, keepdims=True), 1e-9)
    weights = smooth_along_edges(mesh, weights, iterations=3)

    groups = [mesh.vertex_groups.new(name=name) for name in names]
    for vertex in range(count):
        row = weights[vertex]
        strongest = np.argsort(row)[-MAX_BONES_PER_VERTEX:]
        total = row[strongest].sum()
        for index in strongest:
            if row[index] > 0.005:
                groups[index].add([vertex], float(row[index] / total), "REPLACE")

    bind(mesh, rig)


def bind(mesh, rig):
    modifier = mesh.modifiers.new("Armature", "ARMATURE")
    modifier.object = rig
    mesh.parent = rig


def attach_racket(rig, racket, strings):
    """Moves the racket into the rest grip and skins it rigidly to its own bone."""
    origin, frame = racket_rest_frame()
    transform = frame.to_4x4()
    transform.translation = origin
    for part in (racket, strings):
        part.data.transform(transform)
        part.data.update()
        group = part.vertex_groups.new(name="Racket")
        group.add(list(range(len(part.data.vertices))), 1.0, "REPLACE")
        bind(part, rig)


# ---------------------------------------------------------------- Pose solving


class Solver:
    def __init__(self, rig):
        self.rig = rig
        bones = rig.data.bones
        self.rest = {b.name: b.matrix_local.copy() for b in bones}
        self.rest_rot = {n: m.to_3x3() for n, m in self.rest.items()}
        self.parent = {b.name: b.parent.name if b.parent else None for b in bones}
        self.local_rest = {}
        for bone in bones:
            parent = self.rest[bone.parent.name] if bone.parent else Matrix.Identity(4)
            self.local_rest[bone.name] = parent.inverted() @ bone.matrix_local
        self.length = {b.name: b.length for b in bones}

        racket_rest = self.rest_rot["Racket"]
        self.hand_from_racket = racket_rest.inverted() @ self.rest_rot["R_Hand"]
        self.grip_from_wrist_rest = racket_rest.inverted() @ (self.rest["R_Hand"].translation - self.rest["Racket"].translation)

    def child_head(self, name, parent_world):
        return (parent_world @ self.local_rest[name]).translation

    def world(self, rotation, position):
        matrix = rotation.to_4x4()
        matrix.translation = position
        return matrix

    def aimed(self, name, parent_world, direction):
        """The rotation that carries the bone from where its parent leaves it to point along direction, with no extra twist."""
        carried = (parent_world @ self.local_rest[name]).to_3x3()
        current = carried @ Vector((0, 1, 0))
        return current.rotation_difference(direction.normalized()).to_matrix() @ carried

    def two_bone(self, root, target, upper, lower, pole):
        """Returns the middle joint for a limb reaching from root to target, bending towards pole."""
        offset = target - root
        reach = upper + lower
        distance = min(offset.length, reach * 0.999)
        direction = offset.normalized()
        cos_angle = (upper * upper + distance * distance - lower * lower) / (2.0 * upper * distance)
        cos_angle = max(-1.0, min(1.0, cos_angle))
        side = (pole - direction * pole.dot(direction)).normalized()
        return root + direction * (upper * cos_angle) + side * (upper * math.sqrt(1.0 - cos_angle * cos_angle))

    def solve(self, p):
        world = {}

        def set_bone(name, rotation, position=None):
            parent = self.parent[name]
            if position is None:
                position = self.child_head(name, world[parent])
            world[name] = self.world(rotation, position)

        def stack(name, rotation):
            set_bone(name, rotation @ self.rest_rot[name])

        hip_rotation = body_rotation(*p["pelvis_rot"])
        hips_rest = self.rest["Hips"].translation
        set_bone("Hips", hip_rotation @ self.rest_rot["Hips"], hips_rest + to_blender(p["pelvis_pos"]))
        spine = hip_rotation @ body_rotation(*p["spine_rot"])
        stack("Spine", spine)
        chest = spine @ body_rotation(*p["chest_rot"])
        stack("Chest", chest)
        stack("Neck", chest)
        stack("Head", chest @ body_rotation(*p["head_rot"]))
        stack("L_Clavicle", chest)
        stack("R_Clavicle", chest)

        self.solve_legs(p, world, set_bone)
        self.solve_right_arm(p, world, set_bone)
        self.solve_left_arm(p, world, set_bone, chest)
        return world

    def solve_legs(self, p, world, set_bone):
        for prefix, foot_key, rot_key, outward in (("L_", "foot_l", "foot_l_rot", 1.0), ("R_", "foot_r", "foot_r_rot", -1.0)):
            ankle = to_blender(p[foot_key])
            hip = self.child_head(prefix + "UpperLeg", world["Hips"])
            upper = self.length[prefix + "UpperLeg"]
            lower = self.length[prefix + "LowerLeg"]
            pitch, yaw = p[rot_key]
            # Knees bend forward and, by the pose's knee_out, outward: over the toes in a wide stance, straight ahead in a
            # stride. A foot turned further takes its knee with it: in a serve's sideways stance the knees bend over the
            # toes, not towards the net.
            turn = Matrix.Rotation(math.radians(-(yaw - DEFAULT_FOOT_YAW[prefix]) * p["knee_follow"][0]), 3, "Z")
            pole = turn @ Vector((outward * p["knee_out"][0], -1.0, 0.0))
            knee = self.two_bone(hip, ankle, upper, lower, pole)

            set_bone(prefix + "UpperLeg", self.aimed(prefix + "UpperLeg", world["Hips"], knee - hip))
            set_bone(prefix + "LowerLeg", self.aimed(prefix + "LowerLeg", world[prefix + "UpperLeg"], ankle - knee))

            foot_rotation = Matrix.Rotation(math.radians(-yaw), 3, "Z") @ Matrix.Rotation(math.radians(pitch), 3, "X")
            set_bone(prefix + "Foot", foot_rotation @ self.rest_rot[prefix + "Foot"], ankle)
            set_bone(prefix + "Toes", foot_rotation @ self.rest_rot[prefix + "Toes"])

    def solve_right_arm(self, p, world, set_bone):
        racket_y = to_blender(p["racket"]).normalized()
        face = to_blender(p["face"])
        racket_z = (face - racket_y * face.dot(racket_y)).normalized()
        racket_x = racket_y.cross(racket_z).normalized()
        racket_rotation = Matrix((racket_x, racket_y, racket_z)).transposed()
        hand_rotation = racket_rotation @ self.hand_from_racket

        grip = to_blender(p["grip"])
        wrist = grip + racket_rotation @ self.grip_from_wrist_rest

        shoulder = self.child_head("R_UpperArm", world["R_Clavicle"])
        upper = self.length["R_UpperArm"]
        lower = self.length["R_LowerArm"]
        elbow = self.two_bone(shoulder, wrist, upper, lower, to_blender(p["elbow_r"]))
        # The racket dictates the hand's roll. Upper arm and forearm take that roll up between them, as a real
        # arm does, or the skin at the wrist would wring itself into spikes.
        upper_rotation = self.aimed("R_UpperArm", world["R_Clavicle"], elbow - shoulder)
        set_bone("R_UpperArm", self.share_twist(upper_rotation, "R_UpperArm", "R_Hand", hand_rotation, 0.35))
        lower_rotation = self.aimed("R_LowerArm", world["R_UpperArm"], wrist - elbow)
        set_bone("R_LowerArm", self.share_twist(lower_rotation, "R_LowerArm", "R_Hand", hand_rotation, 0.85))
        set_bone("R_Hand", hand_rotation, self.child_head("R_Hand", world["R_LowerArm"]))
        set_bone("Racket", racket_rotation)
        self.curl(world, set_bone, "R_Fingers", "R_Hand", p["fingers_r"], -1.0)

    def solve_left_arm(self, p, world, set_bone, chest):
        hold = p["two_hand"][0]
        free = to_blender(p["hand_l"])
        racket_y = to_blender(p["racket"]).normalized()
        on_racket = to_blender(p["grip"]) + racket_y * 0.13 + Vector((0, 0, 0.02))
        wrist = free * (1.0 - hold) + on_racket * hold

        shoulder = self.child_head("L_UpperArm", world["L_Clavicle"])
        upper = self.length["L_UpperArm"]
        lower = self.length["L_LowerArm"]
        elbow = self.two_bone(shoulder, wrist, upper, lower, to_blender(p["elbow_l"]))
        set_bone("L_UpperArm", self.aimed("L_UpperArm", world["L_Clavicle"], elbow - shoulder))
        set_bone("L_LowerArm", self.aimed("L_LowerArm", world["L_UpperArm"], wrist - elbow))
        set_bone("L_Hand", self.aimed("L_Hand", world["L_LowerArm"], wrist - elbow))
        self.curl(world, set_bone, "L_Fingers", "L_Hand", p["fingers_l"], 1.0)

    def share_twist(self, rotation, bone, hand, hand_rotation, amount):
        """Rolls a bone about its own axis to take up a share of the difference between it and the hand's roll."""
        rest_to_hand = self.rest_rot[bone].inverted() @ self.rest_rot[hand]
        difference = (hand_rotation @ (rotation @ rest_to_hand).inverted()).to_quaternion()
        axis = (rotation @ Vector((0, 1, 0))).normalized()
        twist = 2.0 * math.atan2(Vector((difference.x, difference.y, difference.z)).dot(axis), difference.w)
        if twist > math.pi:
            twist -= 2.0 * math.pi
        elif twist < -math.pi:
            twist += 2.0 * math.pi
        return Matrix.Rotation(twist * amount, 3, axis) @ rotation

    def curl(self, world, set_bone, name, parent, amount, sign):
        """Closes the fingers about the knuckle line. In the rest pose that line runs front to back and the palms face in."""
        carried = (world[parent] @ self.local_rest[name]).to_3x3()
        hand_to_world = world[parent].to_3x3()
        rest_axis = self.rest_rot[parent].inverted() @ Vector((0, sign, 0))
        knuckles = (hand_to_world @ rest_axis).normalized()
        set_bone(name, Matrix.Rotation(math.radians(amount[0]), 3, knuckles) @ carried)

    def apply(self, world, frame):
        """Converts world rotations back into the pose channels and keys them."""
        pose = self.rig.pose.bones
        for name in pose.keys():
            parent = self.parent[name]
            parent_world = world[parent] if parent else Matrix.Identity(4)
            local = (parent_world @ self.local_rest[name]).inverted() @ world[name]
            bone = pose[name]
            bone.rotation_mode = "QUATERNION"
            bone.rotation_quaternion = local.to_quaternion()
            bone.keyframe_insert("rotation_quaternion", frame=frame)
            if name == "Hips":
                bone.location = local.translation
                bone.keyframe_insert("location", frame=frame)


# ---------------------------------------------------------------- Pose library


# How far each foot normally turns out, degrees: the yaw of make_pose's feet. Knees follow any turn beyond it.
DEFAULT_FOOT_YAW = {"L_": -12.0, "R_": 12.0}


def make_pose(**overrides):
    pose = {
        "pelvis_pos": (0.0, 0.0, -0.10),
        "pelvis_rot": (6.0, 0.0, 0.0),
        "spine_rot": (5.0, 0.0, 0.0),
        "chest_rot": (6.0, 0.0, 0.0),
        "head_rot": (-12.0, 0.0, 0.0),
        "foot_l": (-0.23, 0.0, 0.225),
        "foot_r": (0.23, 0.0, 0.225),
        "foot_l_rot": (0.0, -12.0),
        "foot_r_rot": (0.0, 12.0),
        # How far the knees bend out sideways for every unit they bend forward.
        "knee_out": (0.4,),
        # How much of a foot's turn beyond the usual its knee follows: all of it in a stride, less in a stance where the
        # knees drive towards the net while the back foot points along the baseline.
        "knee_follow": (1.0,),
        "grip": (0.13, 0.38, 1.0),
        "racket": (-0.28, 0.72, 0.62),
        "face": (0.9, 0.35, 0.1),
        "hand_l": (-0.15, 0.3, 1.05),
        "two_hand": (1.0,),
        "elbow_r": (0.6, -0.3, -0.75),
        "elbow_l": (-0.6, -0.3, -0.75),
        "fingers_r": (70.0,),
        "fingers_l": (55.0,),
    }
    pose.update(overrides)
    return {key: np.array(value, dtype=np.float64) for key, value in pose.items()}


READY = make_pose()

# Standing at ease the knees are all but straight.
STANDING = make_pose(
    pelvis_pos=(0.0, 0.0, -0.006),
    pelvis_rot=(1.0, 0.0, 0.0),
    spine_rot=(1.0, 0.0, 0.0),
    chest_rot=(1.0, 0.0, 0.0),
    head_rot=(-2.0, 0.0, 0.0),
    foot_l=(-0.2, 0.0, 0.225),
    foot_r=(0.2, 0.0, 0.225),
    grip=(0.43, 0.14, 0.92),
    racket=(0.2, 0.75, 0.25),
    face=(1.0, -0.2, 0.0),
    hand_l=(-0.42, 0.04, 0.92),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.5, -0.3),
    elbow_l=(-0.8, -0.5, -0.3),
    fingers_l=(25.0,),
)

FOREHAND_BACK = make_pose(
    spine_rot=(5.0, 25.0, 0.0),
    chest_rot=(6.0, 30.0, 0.0),
    grip=(0.62, -0.28, 1.28),
    racket=(0.22, -0.62, 0.75),
    face=(0.9, 0.3, 0.2),
    hand_l=(0.12, 0.42, 1.2),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.5, -0.4),
    elbow_l=(-0.7, 0.2, -0.7),
)
FOREHAND_WHIP = make_pose(
    spine_rot=(5.0, 12.0, 0.0),
    chest_rot=(6.0, 10.0, 0.0),
    grip=(0.68, -0.02, 0.95),
    racket=(0.9, -0.25, -0.15),
    face=(0.2, 0.95, 0.1),
    hand_l=(-0.05, 0.38, 1.15),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.4, -0.5),
    elbow_l=(-0.7, 0.0, -0.7),
)
# Contact poses put the middle of the strings at the game's strike zone (ShotTuning): report_contact_heads prints where they land.
FOREHAND_CONTACT = make_pose(
    spine_rot=(5.0, -4.0, 0.0),
    chest_rot=(6.0, -6.0, -8.0),
    grip=(0.56, 0.11, 0.93),
    racket=(0.82, 0.56, 0.08),
    face=(-0.55, 0.82, 0.08),
    hand_l=(-0.28, 0.28, 1.08),
    two_hand=(0.0,),
    elbow_r=(0.7, -0.3, -0.65),
    elbow_l=(-0.8, -0.1, -0.6),
)
FOREHAND_THROUGH = make_pose(
    spine_rot=(5.0, -20.0, 0.0),
    chest_rot=(6.0, -22.0, 0.0),
    grip=(0.28, 0.52, 1.3),
    racket=(-0.1, 0.72, 0.68),
    face=(-0.85, -0.2, 0.5),
    hand_l=(-0.36, 0.14, 1.1),
    two_hand=(0.0,),
    elbow_r=(0.6, -0.1, -0.8),
    elbow_l=(-0.8, -0.3, -0.5),
)
FOREHAND_FINISH = make_pose(
    spine_rot=(5.0, -28.0, 0.0),
    chest_rot=(6.0, -28.0, 0.0),
    grip=(-0.34, 0.26, 1.52),
    racket=(-0.5, -0.58, 0.64),
    face=(-0.6, 0.7, 0.2),
    hand_l=(-0.42, 0.02, 1.06),
    two_hand=(0.0,),
    elbow_r=(0.2, 0.7, -0.6),
    elbow_l=(-0.8, -0.4, -0.5),
)

BACKHAND_BACK = make_pose(
    spine_rot=(5.0, -26.0, 0.0),
    chest_rot=(6.0, -32.0, 0.0),
    grip=(-0.5, -0.16, 1.2),
    racket=(-0.3, -0.55, 0.78),
    face=(-0.2, 0.6, 0.4),
    elbow_r=(0.5, 0.2, -0.8),
    elbow_l=(-0.8, -0.5, -0.4),
)
BACKHAND_WHIP = make_pose(
    spine_rot=(5.0, -14.0, 0.0),
    chest_rot=(6.0, -12.0, 0.0),
    grip=(-0.56, 0.02, 0.98),
    racket=(-0.92, -0.2, -0.12),
    face=(-0.15, 0.95, 0.1),
    elbow_r=(0.4, 0.4, -0.8),
    elbow_l=(-0.8, -0.4, -0.5),
)
BACKHAND_CONTACT = make_pose(
    pelvis_pos=(0.0, 0.0, -0.19),
    pelvis_rot=(9.0, 0.0, 0.0),
    spine_rot=(12.0, 2.0, 0.0),
    chest_rot=(8.0, 2.0, 8.0),
    grip=(-0.5, 0.3, 0.86),
    racket=(-0.84, 0.52, 0.06),
    face=(0.52, 0.85, 0.08),
    elbow_r=(0.3, 0.2, -0.9),
    elbow_l=(-0.7, -0.3, -0.6),
)
BACKHAND_THROUGH = make_pose(
    spine_rot=(5.0, 18.0, 0.0),
    chest_rot=(6.0, 20.0, 0.0),
    grip=(-0.12, 0.55, 1.3),
    racket=(0.2, 0.7, 0.68),
    face=(0.8, -0.3, 0.5),
    elbow_r=(0.7, -0.2, -0.7),
    elbow_l=(-0.5, -0.1, -0.8),
)
BACKHAND_FINISH = make_pose(
    spine_rot=(5.0, 28.0, 0.0),
    chest_rot=(6.0, 28.0, 0.0),
    grip=(0.38, 0.22, 1.5),
    racket=(0.5, -0.58, 0.64),
    face=(0.6, 0.7, 0.2),
    elbow_r=(0.8, -0.3, -0.5),
    elbow_l=(0.1, 0.7, -0.6),
)

# Slices swing high to low with an open face. The backhand slice is one-handed, the free arm opening behind.
FOREHAND_SLICE_BACK = make_pose(
    spine_rot=(5.0, 22.0, 0.0),
    chest_rot=(6.0, 28.0, 0.0),
    grip=(0.55, -0.15, 1.42),
    racket=(0.2, -0.5, 0.84),
    face=(-0.1, 0.7, 0.7),
    hand_l=(0.1, 0.4, 1.22),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.5, -0.3),
    elbow_l=(-0.7, 0.2, -0.7),
)
FOREHAND_SLICE_CONTACT = make_pose(
    spine_rot=(5.0, -2.0, 0.0),
    chest_rot=(6.0, -4.0, -5.0),
    grip=(0.51, 0.14, 0.88),
    racket=(0.85, 0.45, 0.25),
    face=(-0.45, 0.75, 0.48),
    hand_l=(-0.3, 0.25, 1.05),
    two_hand=(0.0,),
    elbow_r=(0.7, -0.3, -0.6),
    elbow_l=(-0.8, -0.1, -0.6),
)
FOREHAND_SLICE_FINISH = make_pose(
    spine_rot=(5.0, -14.0, 0.0),
    chest_rot=(6.0, -16.0, 0.0),
    grip=(0.15, 0.62, 1.02),
    racket=(-0.1, 0.8, 0.3),
    face=(-0.3, 0.4, 0.85),
    hand_l=(-0.4, 0.1, 1.05),
    two_hand=(0.0,),
    elbow_r=(0.6, -0.2, -0.75),
    elbow_l=(-0.8, -0.3, -0.5),
)
BACKHAND_SLICE_BACK = make_pose(
    spine_rot=(5.0, -28.0, 0.0),
    chest_rot=(6.0, -36.0, 0.0),
    grip=(-0.42, -0.08, 1.45),
    racket=(-0.2, -0.5, 0.84),
    face=(0.1, 0.7, 0.7),
    hand_l=(-0.3, -0.02, 1.5),
    two_hand=(0.0,),
    elbow_r=(0.4, 0.3, -0.85),
    elbow_l=(-0.8, -0.3, -0.5),
)
BACKHAND_SLICE_CONTACT = make_pose(
    pelvis_pos=(0.0, 0.0, -0.2),
    pelvis_rot=(9.0, 0.0, 0.0),
    spine_rot=(13.0, 0.0, 0.0),
    chest_rot=(8.0, 0.0, 6.0),
    grip=(-0.5, 0.33, 0.8),
    racket=(-0.85, 0.45, 0.25),
    face=(0.45, 0.75, 0.48),
    hand_l=(-0.55, -0.3, 1.12),
    two_hand=(0.0,),
    elbow_r=(0.3, 0.3, -0.9),
    elbow_l=(-0.7, -0.6, -0.3),
)
BACKHAND_SLICE_FINISH = make_pose(
    spine_rot=(5.0, 10.0, 0.0),
    chest_rot=(6.0, 12.0, 0.0),
    grip=(-0.2, 0.65, 1.05),
    racket=(0.1, 0.8, 0.3),
    face=(0.3, 0.4, 0.85),
    hand_l=(-0.68, -0.35, 1.15),
    two_hand=(0.0,),
    elbow_r=(0.6, -0.1, -0.8),
    elbow_l=(-0.7, -0.6, -0.3),
)

VOLLEY_BACK = make_pose(
    spine_rot=(5.0, 12.0, 0.0),
    chest_rot=(6.0, 14.0, 0.0),
    grip=(0.45, 0.14, 1.2),
    racket=(0.6, 0.15, 0.78),
    face=(-0.2, 0.95, 0.1),
    hand_l=(-0.1, 0.42, 1.18),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.4, -0.5),
)
VOLLEY_CONTACT = make_pose(
    spine_rot=(5.0, 2.0, 0.0),
    chest_rot=(6.0, 2.0, 0.0),
    grip=(0.52, 0.12, 1.05),
    racket=(0.72, 0.58, 0.36),
    face=(-0.45, 0.85, 0.18),
    hand_l=(-0.22, 0.34, 1.12),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.3, -0.5),
)
VOLLEY_FINISH = make_pose(
    spine_rot=(5.0, -3.0, 0.0),
    chest_rot=(6.0, -3.0, 0.0),
    grip=(0.42, 0.55, 1.06),
    racket=(0.6, 0.72, 0.25),
    face=(-0.6, 0.72, 0.22),
    hand_l=(-0.26, 0.28, 1.1),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.3, -0.5),
)

SMASH_BACK = make_pose(
    spine_rot=(-4.0, 16.0, 0.0),
    chest_rot=(-6.0, 22.0, 0.0),
    head_rot=(-25.0, 0.0, 0.0),
    grip=(0.42, -0.14, 1.72),
    racket=(0.1, -0.72, 0.12),
    face=(0.9, 0.0, 0.42),
    hand_l=(-0.05, 0.42, 1.95),
    two_hand=(0.0,),
    elbow_r=(0.9, -0.1, 0.1),
    elbow_l=(-0.7, 0.3, -0.5),
)
SMASH_CONTACT = make_pose(
    pelvis_pos=(0.0, 0.0, -0.04),
    spine_rot=(6.0, -2.0, 0.0),
    chest_rot=(10.0, -8.0, 0.0),
    head_rot=(-25.0, 0.0, 0.0),
    grip=(0.17, 0.03, 1.9),
    racket=(0.12, 0.42, 0.9),
    face=(-0.1, 0.92, -0.4),
    hand_l=(-0.24, 0.32, 1.25),
    two_hand=(0.0,),
    elbow_r=(0.9, -0.2, -0.3),
)
SMASH_FINISH = make_pose(
    spine_rot=(16.0, -18.0, 0.0),
    chest_rot=(18.0, -22.0, 0.0),
    grip=(-0.25, 0.32, 0.92),
    racket=(-0.48, 0.2, -0.85),
    face=(-0.3, 0.9, 0.2),
    hand_l=(-0.42, 0.06, 1.05),
    two_hand=(0.0,),
    elbow_r=(0.6, -0.1, -0.7),
)

# The serve is played side-on, like a real right-hander's platform stance: the front (left) foot just behind the
# baseline, pointing at the right net post; the back foot behind it, nearly along the baseline; hips and shoulders
# turned towards the right sideline, the head turned back to the box.
SERVE_FOOT_L = (-0.08, 0.17, 0.225)
SERVE_FOOT_R = (0.17, -0.17, 0.225)
SERVE_FOOT_L_ROT = (0.0, 35.0)
SERVE_FOOT_R_ROT = (0.0, 60.0)
# The knees bend over the toes, as knees do: a knee pointing anywhere else than its foot twists the shin. The bend is
# kept moderate, so from behind the legs read as loaded springs rather than folding sideways.
SERVE_KNEE_FOLLOW = (1.0,)
SERVE_KNEE_OUT = (0.1,)
# Off the ground the legs hang straight below the body, knees forward and together, whatever way the feet turn.
SERVE_AIR_KNEES = dict(knee_follow=(0.3,), knee_out=(0.0,))

SERVE_START = make_pose(
    pelvis_pos=(0.04, 0.0, -0.012),
    pelvis_rot=(2.0, 45.0, 0.0),
    spine_rot=(2.0, 10.0, 0.0),
    chest_rot=(3.0, 8.0, 0.0),
    head_rot=(-6.0, -48.0, 0.0),
    foot_l=SERVE_FOOT_L,
    foot_r=SERVE_FOOT_R,
    foot_l_rot=SERVE_FOOT_L_ROT,
    foot_r_rot=SERVE_FOOT_R_ROT,
    knee_follow=SERVE_KNEE_FOLLOW,
    knee_out=SERVE_KNEE_OUT,
    grip=(0.08, 0.28, 1.08),
    racket=(-0.15, 0.8, 0.55),
    face=(0.9, 0.2, 0.1),
    hand_l=(-0.06, 0.4, 1.12),
    two_hand=(0.0,),
    elbow_r=(0.6, -0.6, -0.5),
    elbow_l=(-0.6, 0.2, -0.7),
)
# The trophy position: the tossing arm straight up, the racket up behind the head, the knees bent towards the court
# and the hips pushed forward over the front foot, the shoulders turned past the hips and arched back.
SERVE_TOSS = make_pose(
    pelvis_pos=(0.0, 0.12, -0.075),
    pelvis_rot=(-4.0, 45.0, 0.0),
    spine_rot=(-10.0, 14.0, 0.0),
    chest_rot=(-12.0, 22.0, 0.0),
    head_rot=(-30.0, -50.0, 0.0),
    foot_l=SERVE_FOOT_L,
    foot_r=(0.17, -0.17, 0.24),
    foot_l_rot=SERVE_FOOT_L_ROT,
    foot_r_rot=(-10.0, 60.0),
    knee_follow=SERVE_KNEE_FOLLOW,
    knee_out=SERVE_KNEE_OUT,
    grip=(0.3, -0.3, 1.62),
    racket=(0.1, -0.45, 0.88),
    face=(0.9, 0.0, 0.42),
    hand_l=(0.1, 0.4, 2.05),
    two_hand=(0.0,),
    elbow_r=(0.4, -0.9, 0.1),
    elbow_l=(-0.7, 0.3, -0.5),
)
# The legs drive up as the racket drops behind the back: onto the toes, the hips starting to turn back.
SERVE_DROP = make_pose(
    pelvis_pos=(0.0, 0.16, -0.035),
    pelvis_rot=(-2.0, 30.0, 0.0),
    spine_rot=(-9.0, 8.0, 0.0),
    chest_rot=(-13.0, 4.0, 0.0),
    head_rot=(-32.0, -28.0, 0.0),
    foot_l=(-0.08, 0.17, 0.27),
    foot_r=(0.15, -0.15, 0.3),
    foot_l_rot=(-18.0, 30.0),
    foot_r_rot=(-30.0, 50.0),
    knee_follow=SERVE_KNEE_FOLLOW,
    knee_out=SERVE_KNEE_OUT,
    grip=(0.33, -0.08, 1.6),
    racket=(0.05, -0.35, -0.94),
    face=(0.95, 0.0, 0.1),
    hand_l=(-0.12, 0.34, 1.6),
    two_hand=(0.0,),
    elbow_r=(0.9, 0.1, 0.4),
    elbow_l=(-0.7, 0.3, -0.5),
)
# The racket head meets the ball at ShotTuning.ServeContact: (0.28, 0.36, 2.45) from the feet. Off the ground, legs
# straight and toes pointing down, the back foot drawn up to the front one; the body faces the net again.
SERVE_CONTACT = make_pose(
    pelvis_pos=(0.0, 0.1, 0.05),
    pelvis_rot=(5.0, -8.0, 0.0),
    spine_rot=(6.0, -6.0, 0.0),
    chest_rot=(10.0, -10.0, 0.0),
    head_rot=(-30.0, 0.0, 0.0),
    # The front leg straight below, the back one trailing a little behind and higher, knees together.
    foot_l=(-0.05, 0.2, 0.30),
    foot_r=(0.05, -0.1, 0.40),
    foot_l_rot=(-40.0, 12.0),
    foot_r_rot=(-55.0, 10.0),
    **SERVE_AIR_KNEES,
    grip=(0.27, 0.25, 1.99),
    racket=(0.02, 0.25, 0.97),
    face=(0.0, 0.97, -0.25),
    hand_l=(-0.28, 0.32, 1.25),
    two_hand=(0.0,),
    elbow_r=(0.9, -0.2, -0.3),
)
# Landing on the front foot inside the court, the back leg swung up behind for balance, the racket across the body.
SERVE_LAND = make_pose(
    pelvis_pos=(-0.05, 0.3, -0.1),
    pelvis_rot=(10.0, -18.0, 0.0),
    spine_rot=(12.0, -16.0, 0.0),
    chest_rot=(14.0, -20.0, 0.0),
    head_rot=(-10.0, 10.0, 0.0),
    # The back leg kicks up behind, the knee pointing down, not out.
    foot_l=(-0.1, 0.38, 0.225),
    foot_r=(0.02, -0.1, 0.52),
    foot_l_rot=(0.0, 5.0),
    foot_r_rot=(-70.0, 0.0),
    **SERVE_AIR_KNEES,
    grip=(-0.25, 0.5, 0.95),
    racket=(-0.48, 0.2, -0.85),
    face=(-0.3, 0.9, 0.2),
    hand_l=(-0.42, 0.25, 1.05),
    two_hand=(0.0,),
)
# The back foot comes through and down beside the front one, ready to move.
SERVE_FINISH = make_pose(
    pelvis_pos=(0.0, 0.33, -0.1),
    pelvis_rot=(6.0, -6.0, 0.0),
    spine_rot=(8.0, -8.0, 0.0),
    chest_rot=(8.0, -10.0, 0.0),
    foot_l=(-0.2, 0.38, 0.225),
    foot_r=(0.25, 0.4, 0.225),
    grip=(0.0, 0.62, 0.98),
    racket=(-0.3, 0.75, 0.2),
    face=(0.2, 0.2, 0.95),
    hand_l=(-0.3, 0.4, 1.05),
    two_hand=(0.0,),
)

CELEBRATE_FIST = make_pose(
    pelvis_pos=(0.0, 0.0, -0.13),
    pelvis_rot=(4.0, 0.0, 0.0),
    spine_rot=(6.0, 0.0, 0.0),
    chest_rot=(12.0, 0.0, 0.0),
    head_rot=(5.0, 0.0, 0.0),
    foot_l=(-0.3, 0.05, 0.225),
    foot_r=(0.3, 0.0, 0.225),
    grip=(0.46, 0.05, 0.98),
    racket=(0.55, 0.3, -0.75),
    face=(0.0, 1.0, 0.0),
    hand_l=(-0.22, 0.38, 1.3),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.3, -0.4),
    elbow_l=(-0.7, -0.1, -0.8),
    fingers_l=(95.0,),
)
CELEBRATE_PUMP = make_pose(
    pelvis_pos=(0.0, 0.0, -0.09),
    pelvis_rot=(0.0, 0.0, 0.0),
    spine_rot=(-2.0, 0.0, 0.0),
    chest_rot=(-4.0, 0.0, 0.0),
    head_rot=(-15.0, 0.0, 0.0),
    foot_l=(-0.3, 0.05, 0.225),
    foot_r=(0.3, 0.0, 0.225),
    grip=(0.46, 0.05, 0.98),
    racket=(0.55, 0.3, -0.75),
    face=(0.0, 1.0, 0.0),
    hand_l=(-0.3, 0.3, 1.75),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.3, -0.4),
    elbow_l=(-0.8, 0.0, -0.3),
    fingers_l=(95.0,),
)
POINT_LOST_SLUMP = make_pose(
    pelvis_pos=(0.0, -0.03, -0.04),
    pelvis_rot=(2.0, 0.0, 0.0),
    spine_rot=(6.0, 0.0, 0.0),
    chest_rot=(10.0, 0.0, 0.0),
    head_rot=(28.0, 8.0, 0.0),
    foot_l=(-0.2, 0.0, 0.225),
    foot_r=(0.2, 0.0, 0.225),
    grip=(0.4, 0.14, 0.85),
    racket=(0.3, 0.55, -0.75),
    face=(1.0, 0.0, 0.3),
    hand_l=(-0.4, 0.06, 0.88),
    two_hand=(0.0,),
    elbow_r=(0.8, -0.5, -0.3),
    elbow_l=(-0.8, -0.5, -0.3),
    fingers_l=(25.0,),
)


# ---------------------------------------------------------------- Clip authoring


def interpolate(keys, t):
    """Catmull-Rom through the key poses, so swings accelerate smoothly through their keys."""
    times = [k[0] for k in keys]
    if t <= times[0]:
        return dict(keys[0][1])
    if t >= times[-1]:
        return dict(keys[-1][1])
    i = 0
    while t > times[i + 1]:
        i += 1
    p0 = keys[max(i - 1, 0)][1]
    p1 = keys[i][1]
    p2 = keys[i + 1][1]
    p3 = keys[min(i + 2, len(keys) - 1)][1]
    u = (t - times[i]) / (times[i + 1] - times[i])
    result = {}
    for name in p1:
        a, b, c, d = p0[name], p1[name], p2[name], p3[name]
        result[name] = 0.5 * (2 * b + (c - a) * u + (2 * a - 5 * b + 4 * c - d) * u * u + (3 * b - a - 3 * c + d) * u * u * u)
    return result


def begin_action(rig, name):
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    rig.animation_data.action = action
    return action


def author_keys(solver, name, keys):
    begin_action(solver.rig, name)
    duration = keys[-1][0]
    frames = max(1, int(round(duration * FPS)))
    for frame in range(frames + 1):
        solver.apply(solver.solve(interpolate(keys, frame / FPS)), frame)
    log(f"{name}: {duration:.2f} s")


def author_function(solver, name, duration, pose_at):
    """A looping clip: frame 0 and the last frame come from the same phase."""
    begin_action(solver.rig, name)
    frames = max(1, int(round(duration * FPS)))
    for frame in range(frames + 1):
        solver.apply(solver.solve(pose_at(frame / frames)), frame)
    log(f"{name}: {duration:.2f} s")


def blend_pose(a, b, amount):
    return {key: a[key] * (1.0 - amount) + b[key] * amount for key in a}


def smoothstep(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3.0 - 2.0 * x)


# Ankle to the ball of the foot, metres: the heel peels up about the ball.
FOOT_LEVER = 0.22
# Over this last share of the stance the heel peels off the ground.
HEEL_PEEL = 0.45


def foot_cycle(phase, front, back, stance_fraction, lift, heel, land_pitch):
    """
    Along the travel, height and pitch (negative: toes down) of an ankle for phase in 0..1. Planted, the foot runs
    at an even pace from front of the hip to behind it, the heel peeling up at the end as it pushes off; then it
    swings through, the toes coming up to land.
    """
    phase %= 1.0
    stride = front + back
    if phase < stance_fraction:
        u = phase / stance_fraction
        rise = heel * smoothstep((u - (1.0 - HEEL_PEEL)) / HEEL_PEEL)
        # Landing toes up, the foot rolls flat over the first part of the stance.
        pitch = land_pitch * (1.0 - smoothstep(u / 0.2)) - math.degrees(math.asin(rise / FOOT_LEVER))
        return front - stride * u, rise, pitch
    swing = (phase - stance_fraction) / (1.0 - stance_fraction)
    s = smoothstep(swing)
    off = -math.degrees(math.asin(heel / FOOT_LEVER))
    return -back + stride * s, lift * math.sin(math.pi * swing) + heel * (1.0 - s), off + (land_pitch - off) * s


def leg_reach():
    """The farthest from hip joint to ankle with the knee bent by MIN_KNEE_BEND."""
    hip, knee, _, _ = SKIN_BONES["L_UpperLeg"]
    upper = (knee - hip).length
    lower = (SKIN_BONES["L_LowerLeg"][1] - knee).length
    return math.sqrt(upper * upper + lower * lower + 2.0 * upper * lower * math.cos(math.radians(MIN_KNEE_BEND)))


def hip_joint(prefix, pelvis_pos, pelvis_rot):
    """Where the solver puts a hip joint for this pelvis, in armature space."""
    hips_head = SKIN_BONES["Hips"][0]
    joint = SKIN_BONES[prefix + "UpperLeg"][0]
    return hips_head + to_blender(pelvis_pos) + body_rotation(*pelvis_rot) @ (joint - hips_head)


LEGS = (("L_", "foot_l"), ("R_", "foot_r"))


def reach_limited_hips(pose, planted):
    """
    Lowers the hips just enough that each planted leg reaches its foot: a walk's hips then rise over the planted leg
    and dip as the weight passes from one foot to the other, as a real walk's do. A swinging foot out of reach is drawn
    in towards its hip instead, the knee folding as it leaves the ground.
    """
    reach = leg_reach()
    height = pose["pelvis_pos"][2]
    for (prefix, key), down in zip(LEGS, planted):
        if not down:
            continue
        hip = hip_joint(prefix, pose["pelvis_pos"], pose["pelvis_rot"])
        ankle = to_blender(pose[key])
        across = (Vector((hip.x, hip.y, 0.0)) - Vector((ankle.x, ankle.y, 0.0))).length
        if across >= reach:
            raise ValueError(f"a planted foot {across:.2f} m from its hip across the ground is out of the leg's reach")
        height = min(height, pose["pelvis_pos"][2] + ankle.z + math.sqrt(reach * reach - across * across) - hip.z)
    pose["pelvis_pos"] = np.array([pose["pelvis_pos"][0], pose["pelvis_pos"][1], height])
    for (prefix, key), down in zip(LEGS, planted):
        hip = hip_joint(prefix, pose["pelvis_pos"], pose["pelvis_rot"])
        offset = to_blender(pose[key]) - hip
        if not down and offset.length > reach:
            pose[key] = np.array(to_blender(hip + offset * (reach / offset.length)))
    return pose


def gait_pose(base, phase, direction, gait, roll=0.0, racket_low=0.0):
    """A travelling cycle. The planted foot moves at exactly the player's speed, so nothing slides at runtime."""
    speed, stance_fraction, lean = gait["speed"], gait["stance"], gait["lean"]
    stride = speed * gait["cycle"] * stance_fraction
    front, back = stride * gait["front"], stride * (1.0 - gait["front"])
    pose = dict(base)
    planted = [phase % 1.0 < stance_fraction, (phase + 0.5) % 1.0 < stance_fraction]

    for key, foot_phase, side in (("foot_l", phase, -1.0), ("foot_r", phase + 0.5, 1.0)):
        along, height, pitch = foot_cycle(foot_phase, front, back, stance_fraction, gait["lift"], gait["heel"], gait["land_pitch"])
        pose[key] = np.array([side * gait["width"] + direction[0] * along, direction[1] * along, 0.225 + height])
        # Feet point almost straight along a stride; a sideways shuffle keeps them a little more open.
        pose[key + "_rot"] = np.array([pitch if abs(direction[1]) > 0.5 else 0.0, side * (5.0 if abs(direction[0]) < 0.5 else 8.0)])

    # The knees swing straight ahead over the feet, in a stride and in a shuffle alike.
    pose["knee_out"] = np.array([0.05 if abs(direction[1]) > 0.5 else 0.15])

    # A run is lowest in mid-stance, as the leg takes the landing, and highest in flight.
    mid_stance = 4.0 * math.pi * (phase - stance_fraction * 0.5)
    # The hips turn furthest as a foot lands, its own side forward; the shoulders and arms swing against them.
    swing = math.cos(2.0 * math.pi * phase)
    hip_turn, arm_swing = gait["hip"], gait["arm"]
    pose["pelvis_pos"] = np.array([direction[0] * lean * 0.004, direction[1] * lean * 0.005, -gait["crouch"] - gait["bounce"] * math.cos(mid_stance)])
    pose["pelvis_rot"] = np.array([lean * direction[1], swing * hip_turn, roll * direction[0]])
    reach_limited_hips(pose, planted)
    crouch = -pose["pelvis_pos"][2]
    pose["spine_rot"] = np.array([lean * 0.4 * direction[1] + 3.0, -swing * hip_turn * 0.7, 0.0])
    pose["chest_rot"] = np.array([lean * 0.3 * direction[1] + 3.0, -swing * hip_turn * 0.7, 0.0])
    pose["head_rot"] = np.array([-(lean * 0.7 * direction[1] + 4.0), 0.0, 0.0])

    # The racket stays low in the right hand, swinging against the left arm: each arm forward with the opposite foot.
    pose["grip"] = np.array([0.44, 0.2 + swing * arm_swing, 1.0 - crouch * 0.4 + racket_low])
    pose["racket"] = np.array([0.1, 0.8, 0.3 - swing * arm_swing * 0.4])
    pose["face"] = np.array([1.0, -0.1, 0.0])
    pose["hand_l"] = np.array([-0.42, 0.14 - swing * arm_swing, 1.08 - crouch * 0.4 + abs(swing) * arm_swing * 0.25])
    pose["two_hand"] = np.array([0.0])
    pose["elbow_r"] = np.array([0.5, -0.8, -0.3])
    pose["elbow_l"] = np.array([-0.5, -0.8, -0.3])
    return pose


def turned(pose, angle):
    """
    The same stride heading off to the side: the feet, their path and the hips turn by angle (positive to the right),
    and the spine, chest and head turn most of the way back, up to MAX_COUNTER_TURN, so the player keeps the court in view.
    """
    counter = math.copysign(min(abs(angle) * 0.95, MAX_COUNTER_TURN), angle)
    pose = dict(pose)
    a = math.radians(angle)
    c, s = math.cos(a), math.sin(a)

    def rotate(v):
        x, y = v[0], v[1]
        return np.array([x * c + y * s, -x * s + y * c] + list(v[2:]))

    for key in ("foot_l", "foot_r", "pelvis_pos"):
        pose[key] = rotate(pose[key])
    for key in ("foot_l_rot", "foot_r_rot"):
        pose[key] = pose[key] + np.array([0.0, angle])
    pose["pelvis_rot"] = pose["pelvis_rot"] + np.array([0.0, angle, 0.0])
    for key, share in (("spine_rot", 0.45), ("chest_rot", 0.3), ("head_rot", 0.2)):
        pose[key] = pose[key] - np.array([0.0, counter * share / 0.95, 0.0])
    return pose


def gait_heading(gait, phase, travel, turn):
    """
    A gait's stride heading along travel with the hips turned by turn, both degrees clockwise from straight ahead. The
    stride runs a little across the turned hips, so that once turned it heads exactly along the travel.
    """
    a = math.radians(travel - turn)
    return turned(gait_pose(STANDING, phase, (math.sin(a), math.cos(a)), gait), turn)


def author_locomotion(solver):
    """
    Locomotion in three gaits, a walk, a jog and a run, each in six directions. Within a gait every direction shares the
    speed, the cycle and the share of it a foot is on the ground, so the game can blend directions without one clip's
    planted foot cancelling another's swinging one; the game blends the gaits by speed.

    Every direction is a forward stride with the hips turned towards the travel, the chest and head turning back towards
    the court: to the side the hips turn most of the way, in a retreat further still. Neighbouring clips then swing the
    legs the same way and blend cleanly, where a backpedal next to a turned stride would swing them against each other
    and cancel the step. A retreat turns one way or the other, so its two clips sit close to straight back and the
    game picks one side rather than blend hips turned opposite ways.
    """
    for gait in GAITS:
        name, cycle = gait["name"], gait["cycle"]
        author_function(solver, name, cycle, lambda phase, g=gait: gait_heading(g, phase, 0.0, 0.0))
        author_function(solver, name + "Right", cycle, lambda phase, g=gait: gait_heading(g, phase, 90.0, g["side_turn"]))
        author_function(solver, name + "Left", cycle, lambda phase, g=gait: gait_heading(g, phase, -90.0, -g["side_turn"]))
        author_function(solver, name + "BackRight", cycle, lambda phase, g=gait: gait_heading(g, phase, RETREAT_TRAVEL, RETREAT_TURN))
        author_function(solver, name + "BackLeft", cycle, lambda phase, g=gait: gait_heading(g, phase, -RETREAT_TRAVEL, -RETREAT_TURN))

    def breathing_idle(phase):
        pose = dict(STANDING)
        wave = math.sin(2.0 * math.pi * phase)
        pose["pelvis_pos"] = STANDING["pelvis_pos"] + np.array([0.0, 0.0, 0.006 * wave])
        pose["chest_rot"] = STANDING["chest_rot"] + np.array([1.0 * wave, 0.0, 0.0])
        pose["head_rot"] = STANDING["head_rot"] + np.array([0.0, 3.0 * math.sin(2.0 * math.pi * phase), 0.0])
        return pose

    def ready_bounce(phase):
        pose = dict(READY)
        wave = math.sin(4.0 * math.pi * phase)
        pose["pelvis_pos"] = READY["pelvis_pos"] + np.array([0.0, 0.0, 0.012 * wave])
        pose["pelvis_rot"] = READY["pelvis_rot"] + np.array([0.0, 0.0, 1.5 * math.sin(2.0 * math.pi * phase)])
        return pose

    author_function(solver, "Idle", 3.0, breathing_idle)
    author_function(solver, "Ready", 1.2, ready_bounce)


def hold(solver, name, pose, wobble=0.012):
    """A shot's preparation pose as a short loop: the swing starts from wherever the player releases."""
    def at(phase):
        result = dict(pose)
        result["grip"] = pose["grip"] + np.array([0.0, 0.0, wobble * math.sin(2.0 * math.pi * phase)])
        return result

    author_function(solver, name, 1.0, at)


def author_shots(solver):
    # Every swing passes its contact pose exactly SWING_LATENCY after it starts, to match the game's timing.
    contact = SWING_LATENCY

    hold(solver, "Forehand_Prep", FOREHAND_BACK)
    author_keys(solver, "Forehand_Swing", [
        (0.0, FOREHAND_BACK),
        (contact * 0.55, FOREHAND_WHIP),
        (contact, FOREHAND_CONTACT),
        (contact + 0.07, FOREHAND_THROUGH),
        (contact + 0.22, FOREHAND_FINISH),
        (contact + 0.40, FOREHAND_FINISH),
        (contact + 0.62, READY),
    ])
    hold(solver, "Backhand_Prep", BACKHAND_BACK)
    author_keys(solver, "Backhand_Swing", [
        (0.0, BACKHAND_BACK),
        (contact * 0.55, BACKHAND_WHIP),
        (contact, BACKHAND_CONTACT),
        (contact + 0.07, BACKHAND_THROUGH),
        (contact + 0.22, BACKHAND_FINISH),
        (contact + 0.40, BACKHAND_FINISH),
        (contact + 0.62, READY),
    ])
    hold(solver, "ForehandSlice_Prep", FOREHAND_SLICE_BACK)
    author_keys(solver, "ForehandSlice_Swing", [
        (0.0, FOREHAND_SLICE_BACK),
        (contact, FOREHAND_SLICE_CONTACT),
        (contact + 0.16, FOREHAND_SLICE_FINISH),
        (contact + 0.36, FOREHAND_SLICE_FINISH),
        (contact + 0.62, READY),
    ])
    hold(solver, "BackhandSlice_Prep", BACKHAND_SLICE_BACK)
    author_keys(solver, "BackhandSlice_Swing", [
        (0.0, BACKHAND_SLICE_BACK),
        (contact, BACKHAND_SLICE_CONTACT),
        (contact + 0.16, BACKHAND_SLICE_FINISH),
        (contact + 0.36, BACKHAND_SLICE_FINISH),
        (contact + 0.62, READY),
    ])
    hold(solver, "Volley_Prep", VOLLEY_BACK)
    author_keys(solver, "Volley_Swing", [
        (0.0, VOLLEY_BACK),
        (contact, VOLLEY_CONTACT),
        (contact + 0.1, VOLLEY_FINISH),
        (contact + 0.4, READY),
    ])
    hold(solver, "Smash_Prep", SMASH_BACK)
    author_keys(solver, "Smash_Swing", [
        (0.0, SMASH_BACK),
        (contact, SMASH_CONTACT),
        (contact + 0.16, SMASH_FINISH),
        (contact + 0.32, SMASH_FINISH),
        (contact + 0.62, READY),
    ])
    # The serve is split like every shot: the toss is held until release, and the swing reaches
    # its contact pose SWING_LATENCY after it starts. The ready pose holds the ball in the left hand.
    hold(solver, "Serve_Ready", SERVE_START, wobble=0.004)
    author_keys(solver, "Serve_Toss", [
        (0.0, SERVE_START),
        (0.45, SERVE_TOSS),
        (1.6, SERVE_TOSS),
    ])
    author_keys(solver, "Serve_Swing", [
        (0.0, SERVE_TOSS),
        (contact * 0.5, SERVE_DROP),
        (contact, SERVE_CONTACT),
        (contact + 0.2, SERVE_LAND),
        (contact + 0.42, SERVE_FINISH),
        (contact + 0.6, SERVE_FINISH),
        (contact + 0.95, READY),
    ])
    author_keys(solver, "Recovery", [
        (0.0, FOREHAND_FINISH),
        (0.35, READY),
    ])


SPLIT_UNWEIGHT = make_pose(
    pelvis_pos=(0.0, 0.0, -0.03),
    foot_l=(-0.22, 0.0, 0.245),
    foot_r=(0.22, 0.0, 0.245),
    foot_l_rot=(-10.0, -12.0),
    foot_r_rot=(-10.0, 12.0),
)
SPLIT_AIR = make_pose(
    pelvis_pos=(0.0, 0.0, 0.0),
    foot_l=(-0.25, 0.02, 0.29),
    foot_r=(0.25, 0.02, 0.29),
    foot_l_rot=(-8.0, -14.0),
    foot_r_rot=(-8.0, 14.0),
)
SPLIT_LAND = make_pose(
    pelvis_pos=(0.0, -0.02, -0.17),
    pelvis_rot=(9.0, 0.0, 0.0),
    spine_rot=(7.0, 0.0, 0.0),
    foot_l=(-0.37, 0.0, 0.225),
    foot_r=(0.37, 0.0, 0.225),
    foot_l_rot=(0.0, -20.0),
    foot_r_rot=(0.0, 20.0),
    grip=(0.13, 0.4, 0.92),
    hand_l=(-0.15, 0.32, 0.97),
)


def author_reactions(solver):
    # The split step: a small hop as the opponent strikes, landing wide and low, ready to push off either way.
    author_keys(solver, "SplitStep", [
        (0.0, READY),
        (0.07, SPLIT_UNWEIGHT),
        (0.14, SPLIT_AIR),
        (0.22, SPLIT_LAND),
        (0.42, READY),
    ])
    author_keys(solver, "Celebrate", [
        (0.0, READY),
        (0.2, CELEBRATE_FIST),
        (0.45, CELEBRATE_PUMP),
        (0.7, CELEBRATE_FIST),
        (0.95, CELEBRATE_PUMP),
        (1.2, CELEBRATE_FIST),
        (1.8, STANDING),
    ])
    author_keys(solver, "PointLost", [
        (0.0, READY),
        (0.35, POINT_LOST_SLUMP),
        (1.5, POINT_LOST_SLUMP),
        (2.0, STANDING),
    ])


def report_contact_heads(solver, strings):
    """
    Prints where the middle of the strings is in each contact pose, in the game's frame (r, f, u from the feet).
    The game's strike zone (ShotTuning.StrikeLateral / StrikeForward / IdealContactHeight) should match these:
    the closer they are, the less the runtime IK has to bend the arm.
    """
    corners = [v.co for v in strings.data.vertices]
    centre = sum(corners, Vector()) / len(corners)
    on_racket = solver.rest["Racket"].inverted() @ centre
    for name, pose in (("forehand", FOREHAND_CONTACT), ("backhand", BACKHAND_CONTACT),
                       ("forehand slice", FOREHAND_SLICE_CONTACT), ("backhand slice", BACKHAND_SLICE_CONTACT),
                       ("volley", VOLLEY_CONTACT), ("smash", SMASH_CONTACT), ("serve", SERVE_CONTACT)):
        world = solver.solve(pose)
        head = world["Racket"] @ on_racket
        log(f"contact head {name}: r {-head.x:.2f} f {-head.y:.2f} u {head.z:.2f}")


# ---------------------------------------------------------------- Contact sheet


def render_contact_sheet(rig, path):
    """Renders each clip at several moments into one image: the way to check the animation by eye."""
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    tile_w, tile_h = 190, 260
    scene.render.resolution_x = tile_w
    scene.render.resolution_y = tile_h
    world = bpy.data.worlds.new("sheet")
    world.color = (0.72, 0.74, 0.76)
    scene.world = world

    data = bpy.data.cameras.new("sheet")
    data.type = "ORTHO"
    data.ortho_scale = 3.0
    camera = bpy.data.objects.new("sheet", data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    camera.location = (-3.0, -5.0, 2.2)
    direction = Vector((0.0, 0.0, 1.0)) - camera.location
    camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()

    actions = sorted(a for a in bpy.data.actions.keys())
    columns = 7
    sheet = np.ones((len(actions) * tile_h, columns * tile_w, 4), dtype=np.float32)
    temp = os.path.join(os.path.dirname(path), "_sheet_tile.png")
    scene.render.filepath = temp

    for row, name in enumerate(actions):
        action = bpy.data.actions[name]
        rig.animation_data.action = action
        start, end = action.frame_range
        for column in range(columns):
            scene.frame_set(int(round(start + (end - start) * column / (columns - 1))))
            bpy.ops.render.render(write_still=True)
            image = bpy.data.images.load(temp, check_existing=False)
            pixels = np.empty(tile_w * tile_h * 4, dtype=np.float32)
            image.pixels.foreach_get(pixels)
            bpy.data.images.remove(image)
            y0 = (len(actions) - 1 - row) * tile_h
            sheet[y0:y0 + tile_h, column * tile_w:(column + 1) * tile_w] = pixels.reshape(tile_h, tile_w, 4)

    output = bpy.data.images.new("sheet", sheet.shape[1], sheet.shape[0], alpha=True)
    output.pixels.foreach_set(sheet.ravel())
    output.filepath_raw = path
    output.file_format = "PNG"
    output.save()
    os.remove(temp)
    log(f"contact sheet rows: {actions}")
    log(f"wrote {path}")


# ---------------------------------------------------------------- Export


def set_material(obj, name, base_path):
    """Named for Unity, which assigns the real materials by these names. The texture is only for previews."""
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    tree = material.node_tree
    bsdf = next(n for n in tree.nodes if n.type == "BSDF_PRINCIPLED")
    node = tree.nodes.new("ShaderNodeTexImage")
    node.image = bpy.data.images.load(base_path, check_existing=True)
    tree.links.new(node.outputs["Color"], bsdf.inputs["Base Color"])
    obj.data.materials.clear()
    obj.data.materials.append(material)


def export_player(path):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in bpy.context.scene.objects:
        if obj.type in ("ARMATURE", "MESH") and obj.name in ("Rig", "Player", "Racket", "Strings"):
            obj.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        add_leaf_bones=False,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        use_tspace=True,
        bake_anim=True,
        bake_anim_use_all_actions=True,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_bones=True,
        bake_anim_force_startend_keying=True,
        bake_anim_simplify_factor=0.0,
        path_mode="STRIP",
    )
    log(f"exported {path}")


def export_ball(work_dir, art_dir):
    bpy.ops.wm.open_mainfile(filepath=os.path.join(work_dir, "racket_low.blend"))
    ball = bpy.data.objects["Ball"]
    bpy.ops.object.select_all(action="DESELECT")
    ball.select_set(True)
    bpy.context.view_layer.objects.active = ball
    bpy.ops.object.make_single_user(object=True, obdata=True)
    set_material(ball, "Ball", os.path.join(art_dir, "Racket_BaseColor.png"))
    bpy.ops.export_scene.fbx(
        filepath=os.path.join(art_dir, "Ball.fbx"),
        use_selection=True,
        object_types={"MESH"},
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        bake_anim=False,
        path_mode="STRIP",
    )
    log("exported ball")


def append_objects(blend_path, names):
    with bpy.data.libraries.load(blend_path) as (source, target):
        target.objects = [n for n in source.objects if n in names]
    objects = {}
    for obj in target.objects:
        bpy.context.scene.collection.objects.link(obj)
        objects[obj.name] = obj
    return objects


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    work_dir, art_dir = argv[0], argv[1]
    sheet_path = argv[2] if len(argv) > 2 else None

    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.fps = FPS

    player = append_objects(os.path.join(work_dir, "player_low.blend"), {"Player"})["Player"]
    racket_parts = append_objects(os.path.join(work_dir, "racket_low.blend"), {"Racket", "Strings"})

    rig = build_armature()
    skin_player(player, rig)
    attach_racket(rig, racket_parts["Racket"], racket_parts["Strings"])
    set_material(player, "Player", os.path.join(art_dir, "Player_BaseColor.png"))
    set_material(racket_parts["Racket"], "Racket", os.path.join(art_dir, "Racket_BaseColor.png"))
    set_material(racket_parts["Strings"], "RacketStrings", os.path.join(art_dir, "Racket_Strings.png"))
    log("skinned")

    rig.animation_data_create()
    solver = Solver(rig)
    author_locomotion(solver)
    author_shots(solver)
    author_reactions(solver)
    report_contact_heads(solver, racket_parts["Strings"])

    if sheet_path:
        render_contact_sheet(rig, sheet_path)

    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work_dir, "player_rigged.blend"))
    export_player(os.path.join(art_dir, "Player.fbx"))
    export_ball(work_dir, art_dir)
    log("done")


# make_crowd_atlas.py imports this module for its pose solver.
if __name__ == "__main__":
    main()
