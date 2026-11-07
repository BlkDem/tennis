"""
Builds the show court's stadium: a bowl of stands around the court, seats, aisles, sponsor boards,
the umpire's chair, players' chairs and a scoreboard, with their textures.

    blender -b --factory-startup --python make_stadium.py -- <stadium_art_dir>

Writes Stadium.fbx (one mesh per material, named for Unity), Stadium_Layout.json (every seat, the
scoreboard screen and the floodlights, for the crowd, the score display and evening lighting) and the textures.

Everything is laid out in the game's coordinates: X across the court, Y up, Z along it with the far end at +Z.
The near end, where the camera looks in from, has a low stand so the view over it stays clear.
The court surface the bowl starts from must match ProjectBuilder (18.29 x 36.58 m).
"""
import json
import math
import os
import sys

import bpy
import numpy as np

# The stands begin at the edge of the court surface.
X0 = 18.29 / 2
Z0 = 36.58 / 2
TREAD = 0.85
RISER = 0.42
FIRST_ROW = 1.1
# The far stand stops lower than the sides so the scoreboard above it stays inside the gameplay camera's view.
ROWS = {"east": 20, "west": 20, "far": 14, "near": 9}
VIP_ROWS = 3
SEAT_PITCH = 0.5
SEAT_OFFSET = 0.5
CORNER_GAP = 0.7
AISLE_HALF_WIDTH = 0.55
SIDE_AISLES = (0.0, 11.0, -11.0, 22.0, -22.0, 32.0, -32.0)
END_AISLES = (5.5, -5.5, 15.5, -15.5)
RAILING = 1.1
WALL_THICKNESS = 0.25

# Sponsor boards: the texture holds one 8:1 panel per row, and a panel spans the parapet's full height.
BOARD_ROWS = 8
BOARD_PANEL_LENGTH = FIRST_ROW * 8.0
BRANDS = [
    ("AURORA WATER", (0.05, 0.25, 0.55), (1.0, 1.0, 1.0)),
    ("BASELINE BANK", (0.08, 0.08, 0.1), (0.95, 0.8, 0.2)),
    ("VOLLEY MOTORS", (0.7, 0.08, 0.1), (1.0, 1.0, 1.0)),
    ("NORTHWIND AIR", (0.9, 0.9, 0.92), (0.05, 0.2, 0.45)),
    ("TOPSPIN ENERGY", (0.1, 0.45, 0.2), (1.0, 0.95, 0.3)),
    ("COURTSIDE COFFEE", (0.35, 0.2, 0.12), (0.98, 0.92, 0.8)),
    ("ACE TELECOM", (0.95, 0.55, 0.1), (0.1, 0.1, 0.12)),
    ("GRAND COURT OPEN", (0.12, 0.3, 0.25), (1.0, 1.0, 1.0)),
]

SCOREBOARD_WIDTH = 8.0
SCOREBOARD_HEIGHT = 3.6
SCOREBOARD_GAP = 0.3

UMPIRE_X = -7.3


def log(message):
    print(f"[stadium] {message}", flush=True)


def v3(x, y, z):
    return np.array([x, y, z], dtype=np.float64)


UP = v3(0, 1, 0)


def to_blender(p):
    """Game to Blender axes, matching the FBX export below: game +X is Blender -X, game +Z is Blender -Y."""
    return np.array([-p[0], -p[2], p[1]], dtype=np.float64)


# ---------------------------------------------------------------- Mesh building


class Part:
    """One mesh with one material. Faces are given in game coordinates with the side they should face."""

    def __init__(self, name, tile=2.0):
        self.name = name
        self.tile = tile
        self.verts = []
        self.faces = []
        self.uvs = []

    def quad(self, a, b, c, d, outward, uv=None):
        points = [np.asarray(p, dtype=np.float64) for p in (a, b, c, d)]
        if uv is None:
            uv = [self.planar(p, outward) for p in points]
        blender = [to_blender(p) for p in points]
        normal = np.cross(blender[1] - blender[0], blender[2] - blender[0])
        if np.dot(normal, to_blender(outward)) < 0.0:
            blender.reverse()
            uv = list(reversed(uv))
        start = len(self.verts)
        self.verts.extend(blender)
        self.faces.append((start, start + 1, start + 2, start + 3))
        self.uvs.extend(uv)

    def planar(self, p, outward):
        n = np.abs(outward)
        if n[1] >= n[0] and n[1] >= n[2]:
            return (p[0] / self.tile, p[2] / self.tile)
        if n[0] >= n[2]:
            return (p[2] / self.tile, p[1] / self.tile)
        return (p[0] / self.tile, p[1] / self.tile)

    def box(self, centre, size, forward=(0.0, 0.0, 1.0), faces="tfblrd"):
        """A box turned to face forward (horizontal). size = (across, up, along forward). faces: t, f, b, l, r, d (bottom)."""
        f = np.asarray(forward, dtype=np.float64)
        f = f / np.linalg.norm(f)
        r = v3(f[2], 0.0, -f[0])
        c = np.asarray(centre, dtype=np.float64)
        hx, hy, hz = size[0] / 2, size[1] / 2, size[2] / 2

        def corner(sx, sy, sz):
            return c + r * (sx * hx) + UP * (sy * hy) + f * (sz * hz)

        if "t" in faces:
            self.quad(corner(-1, 1, -1), corner(1, 1, -1), corner(1, 1, 1), corner(-1, 1, 1), UP)
        if "d" in faces:
            self.quad(corner(-1, -1, -1), corner(1, -1, -1), corner(1, -1, 1), corner(-1, -1, 1), -UP)
        if "f" in faces:
            self.quad(corner(-1, -1, 1), corner(1, -1, 1), corner(1, 1, 1), corner(-1, 1, 1), f)
        if "b" in faces:
            self.quad(corner(-1, -1, -1), corner(1, -1, -1), corner(1, 1, -1), corner(-1, 1, -1), -f)
        if "r" in faces:
            self.quad(corner(1, -1, -1), corner(1, -1, 1), corner(1, 1, 1), corner(1, 1, -1), r)
        if "l" in faces:
            self.quad(corner(-1, -1, -1), corner(-1, -1, 1), corner(-1, 1, 1), corner(-1, 1, -1), -r)

    def build(self):
        mesh = bpy.data.meshes.new(self.name)
        mesh.from_pydata([tuple(v) for v in self.verts], [], self.faces)
        layer = mesh.uv_layers.new(name="UVMap")
        layer.data.foreach_set("uv", np.asarray(self.uvs, dtype=np.float32).ravel())
        mesh.validate()
        mesh.update()
        obj = bpy.data.objects.new(self.name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        material = bpy.data.materials.new(self.name)
        obj.data.materials.append(material)
        log(f"{self.name}: {len(self.faces)} quads")
        return obj


# ---------------------------------------------------------------- The bowl


class Segment:
    """One side of the bowl. Points are normal * (base + depth) + along * s, with |s| <= half + depth: corners are diagonal."""

    def __init__(self, name, normal, along, base, half, aisles):
        self.name = name
        self.n = np.asarray(normal, dtype=np.float64)
        self.a = np.asarray(along, dtype=np.float64)
        self.base = base
        self.half = half
        self.aisles = aisles
        self.rows = ROWS[name]

    def point(self, depth, s, y):
        p = self.n * (self.base + depth) + self.a * s
        p[1] = y
        return p

    def reach(self, depth):
        return self.half + depth

    def viewer_right(self):
        """Direction of reading for someone on court facing this stand."""
        return v3(self.n[2], 0.0, -self.n[0])


def row_height(row):
    return FIRST_ROW + row * RISER


SEGMENTS = [
    Segment("east", (1, 0, 0), (0, 0, 1), X0, Z0, SIDE_AISLES),
    Segment("west", (-1, 0, 0), (0, 0, 1), X0, Z0, SIDE_AISLES),
    Segment("far", (0, 0, 1), (1, 0, 0), Z0, X0, END_AISLES),
    Segment("near", (0, 0, -1), (1, 0, 0), Z0, X0, END_AISLES),
]


def neighbour(segment, sign):
    """The segment met at the s = sign * reach end of this one."""
    target = segment.a * sign
    return next(s for s in SEGMENTS if np.allclose(s.n, target))


def build_bowl(concrete, railing, facade, boards):
    for seg in SEGMENTS:
        inward = -seg.n
        for row in range(seg.rows):
            d1, d2 = row * TREAD, (row + 1) * TREAD
            y = row_height(row)
            concrete.quad(seg.point(d1, -seg.reach(d1), y), seg.point(d1, seg.reach(d1), y),
                          seg.point(d2, seg.reach(d2), y), seg.point(d2, -seg.reach(d2), y), UP)
            if row > 0:
                y_below = row_height(row - 1)
                concrete.quad(seg.point(d1, -seg.reach(d1), y_below), seg.point(d1, seg.reach(d1), y_below),
                              seg.point(d1, seg.reach(d1), y), seg.point(d1, -seg.reach(d1), y), inward)

        build_parapet(seg, boards)

        # The wall behind the top row: a railing towards the court and a plain facade outside.
        d_end = seg.rows * TREAD
        top = row_height(seg.rows - 1)
        wall_top = top + RAILING
        r_in, r_out = seg.reach(d_end), seg.reach(d_end + WALL_THICKNESS)
        railing.quad(seg.point(d_end, -r_in, top), seg.point(d_end, r_in, top),
                     seg.point(d_end, r_in, wall_top), seg.point(d_end, -r_in, wall_top), inward)
        railing.quad(seg.point(d_end, -r_in, wall_top), seg.point(d_end, r_in, wall_top),
                     seg.point(d_end + WALL_THICKNESS, r_out, wall_top), seg.point(d_end + WALL_THICKNESS, -r_out, wall_top), UP)
        facade.quad(seg.point(d_end + WALL_THICKNESS, -r_out, 0.0), seg.point(d_end + WALL_THICKNESS, r_out, 0.0),
                    seg.point(d_end + WALL_THICKNESS, r_out, wall_top), seg.point(d_end + WALL_THICKNESS, -r_out, wall_top), seg.n)

        # Where a taller neighbour's rows run past this stand's top, close the corner with a wall.
        for sign in (-1.0, 1.0):
            other = neighbour(seg, sign)
            outward = (seg.a * sign - seg.n) / math.sqrt(2.0)
            for row in range(other.rows, seg.rows):
                d1, d2 = row * TREAD, (row + 1) * TREAD
                y = row_height(row)
                facade.quad(seg.point(d1, sign * seg.reach(d1), 0.0), seg.point(d2, sign * seg.reach(d2), 0.0),
                            seg.point(d2, sign * seg.reach(d2), y), seg.point(d1, sign * seg.reach(d1), y), outward)


MAST_HEIGHT = 14.0
LAMP_SIZE = (4.2, 2.2, 0.5)


def corner_squares():
    """
    Where a tall side stand meets a lower end stand, a tower fills the corner in plan from the end stand's
    back wall out to the side stand's. Returns (sign_x, sign_z, inner, outer, height) per corner.
    """
    squares = []
    side_rows = ROWS["east"]
    for sign_z, end in ((1.0, "far"), (-1.0, "near")):
        inner = ROWS[end] * TREAD
        outer = side_rows * TREAD + WALL_THICKNESS
        height = row_height(side_rows - 1) + RAILING
        for sign_x in (1.0, -1.0):
            squares.append((sign_x, sign_z, inner, outer, height))
    return squares


def in_tower(p):
    for sign_x, sign_z, inner, outer, _ in corner_squares():
        dx = p[0] * sign_x - X0
        dz = p[2] * sign_z - Z0
        if inner - 0.3 <= dx <= outer and inner - 0.3 <= dz <= outer:
            return True
    return False


def build_corner_towers(facade, railing, metal, lamp):
    """Stair towers close the bowl's corners, and each carries a floodlight mast for evening matches."""
    floodlights = []
    for sign_x, sign_z, inner, outer, height in corner_squares():
        size = outer - inner
        centre = v3(sign_x * (X0 + (inner + outer) / 2), height / 2, sign_z * (Z0 + (inner + outer) / 2))
        facade.box(centre, (size, height, size), faces="fblr")
        top = centre + UP * (height / 2)
        railing.quad(top + v3(-size / 2, 0, -size / 2), top + v3(size / 2, 0, -size / 2),
                     top + v3(size / 2, 0, size / 2), top + v3(-size / 2, 0, size / 2), UP)

        to_court = -np.array([centre[0], 0.0, centre[2]])
        to_court /= np.linalg.norm(to_court)
        metal.box(top + UP * (MAST_HEIGHT / 2), (0.6, MAST_HEIGHT, 0.6), to_court, faces="fblr")
        head = top + UP * MAST_HEIGHT
        metal.box(head, LAMP_SIZE, to_court)
        face = head + to_court * (LAMP_SIZE[2] / 2 + 0.02)
        right = v3(to_court[2], 0.0, -to_court[0])
        hw, hh = LAMP_SIZE[0] / 2 - 0.15, LAMP_SIZE[1] / 2 - 0.15
        lamp.quad(face - right * hw - UP * hh, face + right * hw - UP * hh, face + right * hw + UP * hh, face - right * hw + UP * hh, to_court)
        floodlights.append([round(float(c), 3) for c in face])
    return floodlights


def build_parapet(seg, boards):
    """The front of the first row, faced with sponsor boards that read left to right from the court."""
    right = seg.viewer_right()
    sign = 1.0 if np.dot(seg.a, right) > 0 else -1.0
    length = seg.reach(0.0) * 2.0
    panels = int(math.ceil(length / BOARD_PANEL_LENGTH))
    start = -panels * BOARD_PANEL_LENGTH / 2.0
    brand_offset = SEGMENTS.index(seg) * 3

    for i in range(panels):
        u0 = start + i * BOARD_PANEL_LENGTH
        u1 = u0 + BOARD_PANEL_LENGTH
        lo, hi = max(u0, -length / 2), min(u1, length / 2)
        if hi <= lo:
            continue
        brand = (i + brand_offset) % BOARD_ROWS
        v0 = (brand + 0.02) / BOARD_ROWS
        v1 = (brand + 0.98) / BOARD_ROWS
        ua, ub = (lo - u0) / BOARD_PANEL_LENGTH, (hi - u0) / BOARD_PANEL_LENGTH
        # Reading coordinate u runs along the viewer's right; map it to s on this segment.
        p0 = seg.point(0.0, sign * lo, 0.0)
        p1 = seg.point(0.0, sign * hi, 0.0)
        p2 = seg.point(0.0, sign * hi, FIRST_ROW)
        p3 = seg.point(0.0, sign * lo, FIRST_ROW)
        boards.quad(p0, p1, p2, p3, -seg.n, uv=[(ua, v0), (ub, v0), (ub, v1), (ua, v1)])


def in_aisle(seg, s):
    return any(abs(s - a) < AISLE_HALF_WIDTH for a in seg.aisles)


def build_seats(blue, white, steps, accent):
    seats = []
    for seg in SEGMENTS:
        facing = -seg.n
        for row in range(seg.rows):
            depth = row * TREAD + SEAT_OFFSET
            y = row_height(row)
            limit = seg.reach(depth) - CORNER_GAP
            count = int(limit / SEAT_PITCH)
            part = white if row < VIP_ROWS and seg.name in ("east", "west") else blue
            for k in range(-count, count):
                s = (k + 0.5) * SEAT_PITCH
                if abs(s) > limit or in_aisle(seg, s):
                    continue
                origin = seg.point(depth, s, y)
                if in_tower(origin):
                    continue
                part.box(origin + UP * 0.43, (0.46, 0.06, 0.40), facing, faces="tf")
                part.box(origin + UP * 0.68 - facing * 0.22, (0.46, 0.44, 0.05), facing, faces="tfb")
                seats.append((origin, facing))

        # Aisles: a half step on the back of each tread, both edges striped.
        for s in seg.aisles:
            for row in range(1, seg.rows):
                if abs(s) > seg.reach(row * TREAD) - CORNER_GAP:
                    continue
                y = row_height(row - 1)
                centre = seg.point(row * TREAD - TREAD / 4.0, s, y + RISER / 4.0)
                steps.box(centre, (1.0, RISER / 2.0, TREAD / 2.0), facing, faces="tflr")
                accent.box(seg.point(row * TREAD - TREAD / 2.0 + 0.04, s, y + RISER / 2.0 + 0.004), (1.0, 0.012, 0.08), facing, faces="t")
                accent.box(seg.point(row * TREAD + 0.04, s, row_height(row) + 0.004), (1.0, 0.012, 0.08), facing, faces="t")
    return seats


# ---------------------------------------------------------------- Court furniture and scoreboard


def build_umpire_chair(metal, seat):
    facing = (1.0, 0.0, 0.0)
    base = v3(UMPIRE_X, 0.0, 0.0)
    for dx in (-0.3, 0.3):
        for dz in (-0.3, 0.3):
            metal.box(base + v3(dx, 0.9, dz), (0.06, 1.8, 0.06), facing, faces="fblr")
    metal.box(base + v3(0.0, 1.83, 0.0), (0.8, 0.06, 0.8), facing)
    metal.box(base + v3(0.35, 2.15, 0.0), (0.8, 0.6, 0.06), facing, faces="tfblr")
    seat.box(base + v3(-0.05, 2.2, 0.0), (0.5, 0.08, 0.45), facing)
    seat.box(base + v3(-0.3, 2.5, 0.0), (0.5, 0.55, 0.06), facing)
    for i in range(6):
        metal.box(base + v3(-0.42, 0.3 * (i + 1), 0.0), (0.5, 0.04, 0.05), facing, faces="tfbd")


def build_player_chairs(metal, seat):
    facing = (1.0, 0.0, 0.0)
    for z in (-1.9, 1.9):
        base = v3(UMPIRE_X - 0.6, 0.0, z)
        for dx in (-0.2, 0.2):
            for dz in (-0.2, 0.2):
                metal.box(base + v3(dx, 0.23, dz), (0.04, 0.46, 0.04), facing, faces="fblr")
        seat.box(base + v3(0.0, 0.48, 0.0), (0.48, 0.06, 0.46), facing)
        seat.box(base + v3(-0.23, 0.78, 0.0), (0.48, 0.55, 0.05), facing)
        # A cooler box beside each chair.
        seat.box(base + v3(0.1, 0.22, z * 0.25), (0.4, 0.44, 0.32), facing, faces="tfblr")


def build_scoreboard(frame, screen):
    far = next(s for s in SEGMENTS if s.name == "far")
    wall_top = row_height(far.rows - 1) + RAILING
    back = Z0 + far.rows * TREAD
    bottom = wall_top + SCOREBOARD_GAP
    centre = v3(0.0, bottom + SCOREBOARD_HEIGHT / 2.0, back + 0.3)
    facing = (0.0, 0.0, -1.0)
    frame.box(centre, (SCOREBOARD_WIDTH + 0.4, SCOREBOARD_HEIGHT + 0.4, 0.5), facing)
    for x in (-SCOREBOARD_WIDTH / 3.0, SCOREBOARD_WIDTH / 3.0):
        frame.box(v3(x, wall_top + SCOREBOARD_GAP / 2.0, back + 0.3), (0.3, SCOREBOARD_GAP, 0.3), facing, faces="fblr")
    face_z = back + 0.3 - 0.26
    screen.quad(v3(-SCOREBOARD_WIDTH / 2, bottom, face_z), v3(SCOREBOARD_WIDTH / 2, bottom, face_z),
                v3(SCOREBOARD_WIDTH / 2, bottom + SCOREBOARD_HEIGHT, face_z), v3(-SCOREBOARD_WIDTH / 2, bottom + SCOREBOARD_HEIGHT, face_z),
                v3(0, 0, -1), uv=[(0, 0), (1, 0), (1, 1), (0, 1)])
    return {"center": [0.0, float(centre[1]), float(face_z - 0.01)], "width": SCOREBOARD_WIDTH, "height": SCOREBOARD_HEIGHT}


# ---------------------------------------------------------------- Textures


def save_image(pixels, path, colorspace):
    height, width = pixels.shape[:2]
    image = bpy.data.images.new(os.path.basename(path), width, height, alpha=True)
    image.colorspace_settings.name = colorspace
    image.pixels.foreach_set(pixels.astype(np.float32).ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)
    log(f"wrote {path}")


def tileable_noise(size, feature_px, seed):
    """Smooth noise that wraps at the edges: white noise filtered in frequency space is periodic by construction."""
    rng = np.random.default_rng(seed)
    white = rng.standard_normal((size, size))
    fx = np.fft.fftfreq(size)[None, :]
    fy = np.fft.fftfreq(size)[:, None]
    falloff = np.exp(-(fx * fx + fy * fy) * (feature_px ** 2) * 8.0)
    noise = np.real(np.fft.ifft2(np.fft.fft2(white) * falloff))
    return (noise - noise.mean()) / (noise.std() + 1e-9)


def make_concrete(art_dir):
    """A light, tileable grey that the stadium's materials tint: concrete, steps and painted walls share it."""
    size = 512
    value = 0.8 + 0.05 * tileable_noise(size, 60, 1) + 0.035 * tileable_noise(size, 12, 2) + 0.025 * tileable_noise(size, 2, 3)
    grey = np.clip(value, 0.0, 1.0)
    pixels = np.stack([grey, grey * 0.99, grey * 0.97, np.ones_like(grey)], axis=-1)
    save_image(pixels, os.path.join(art_dir, "Concrete_BaseColor.png"), "sRGB")


def make_boards(art_dir):
    """Renders each brand's panel with Blender's own text, then stacks them into one texture."""
    width, height = 2048, 256
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.film_transparent = False
    scene.display.render_aa = "16"
    scene.display.shading.light = "FLAT"
    scene.display.shading.color_type = "OBJECT"
    scene.view_settings.view_transform = "Standard"

    camera_data = bpy.data.cameras.new("boards")
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = 8.0
    camera = bpy.data.objects.new("boards", camera_data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    camera.location = (0.0, 0.0, 10.0)

    bpy.ops.mesh.primitive_plane_add(size=1.0)
    background = bpy.context.active_object
    background.scale = (8.2, 1.2, 1.0)

    curve = bpy.data.curves.new("brand", type="FONT")
    curve.align_x = "CENTER"
    curve.align_y = "CENTER"
    curve.size = 0.62
    curve.space_character = 1.05
    text = bpy.data.objects.new("brand", curve)
    scene.collection.objects.link(text)
    text.location = (0.0, 0.0, 0.1)

    atlas = np.zeros((height * BOARD_ROWS, width, 4), dtype=np.float32)
    temp = os.path.join(art_dir, "_board.png")
    for row, (name, back, ink) in enumerate(BRANDS):
        curve.body = name
        background.color = (*back, 1.0)
        text.color = (*ink, 1.0)
        scene.render.filepath = temp
        bpy.ops.render.render(write_still=True)
        image = bpy.data.images.load(temp, check_existing=False)
        pixels = np.empty(width * height * 4, dtype=np.float32)
        image.pixels.foreach_get(pixels)
        bpy.data.images.remove(image)
        atlas[row * height:(row + 1) * height] = pixels.reshape(height, width, 4)
    os.remove(temp)
    atlas[..., 3] = 1.0
    save_image(atlas, os.path.join(art_dir, "Stadium_Boards.png"), "sRGB")

    for obj in (background, text, camera):
        bpy.data.objects.remove(obj, do_unlink=True)


# ---------------------------------------------------------------- Export


def export(path, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"MESH"},
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        bake_anim=False,
        path_mode="STRIP",
    )
    log(f"exported {path}")


def main():
    art_dir = sys.argv[sys.argv.index("--") + 1]
    os.makedirs(art_dir, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)

    make_boards(art_dir)
    make_concrete(art_dir)

    parts = {name: Part(name) for name in (
        "Stadium_Concrete", "Stadium_Steps", "Stadium_Railing", "Stadium_Facade", "Stadium_Accent",
        "Seats_Blue", "Seats_White", "Furniture_Metal", "Furniture_Seat", "Scoreboard_Frame", "Scoreboard_Screen",
        "Floodlight_Metal", "Floodlight_Lamp")}
    parts["Stadium_Boards"] = Part("Stadium_Boards")

    build_bowl(parts["Stadium_Concrete"], parts["Stadium_Railing"], parts["Stadium_Facade"], parts["Stadium_Boards"])
    seats = build_seats(parts["Seats_Blue"], parts["Seats_White"], parts["Stadium_Steps"], parts["Stadium_Accent"])
    build_umpire_chair(parts["Furniture_Metal"], parts["Furniture_Seat"])
    build_player_chairs(parts["Furniture_Metal"], parts["Furniture_Seat"])
    scoreboard = build_scoreboard(parts["Scoreboard_Frame"], parts["Scoreboard_Screen"])
    floodlights = build_corner_towers(parts["Stadium_Facade"], parts["Stadium_Railing"], parts["Floodlight_Metal"], parts["Floodlight_Lamp"])

    objects = [part.build() for part in parts.values() if part.faces]
    export(os.path.join(art_dir, "Stadium.fbx"), objects)

    flat = []
    for origin, facing in seats:
        flat.extend([round(float(origin[0]), 3), round(float(origin[1]), 3), round(float(origin[2]), 3),
                     round(float(facing[0]), 3), round(float(facing[2]), 3)])
    layout = {"seatStride": 5, "seats": flat, "scoreboardCenter": scoreboard["center"],
              "scoreboardWidth": scoreboard["width"], "scoreboardHeight": scoreboard["height"],
              "floodlights": [c for light in floodlights for c in light]}
    with open(os.path.join(art_dir, "Stadium_Layout.json"), "w") as handle:
        json.dump(layout, handle, separators=(",", ":"))
    log(f"{len(seats)} seats")
    log("done")


main()
