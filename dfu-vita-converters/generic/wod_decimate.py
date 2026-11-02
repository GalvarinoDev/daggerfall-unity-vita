# World of Daggerfall mountains: lighter meshes for the Vita, made on the PC in Blender.
#   blender -b --python generic/wod_decimate.py -- <ratio> <in.fbx> <out.fbx> [<preview.png>]
# Collapse-decimates every mesh to <ratio> of its triangles, keeps UVs and material slots, keeps flat shading, and writes an FBX the
# 2018 editor imports with the same transform (object scale/rotation untouched; Unity's default FBX import). rrppack.sh runs it on the
# Mountain_*.fbx copies before the bake when WOD_DECIMATE is set. With <preview.png> it also renders the result (workbench, flat).
import bpy, sys, math
a = sys.argv[sys.argv.index('--') + 1:]
ratio, src, dst = float(a[0]), a[1], a[2]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=src)
for o in bpy.context.scene.objects:
    if o.type != 'MESH':
        continue
    m = o.data
    m.calc_loop_triangles()
    before = len(m.loop_triangles)
    d = o.modifiers.new('dec', 'DECIMATE')
    d.decimate_type = 'COLLAPSE'
    d.ratio = ratio
    d.use_collapse_triangulate = True
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.modifier_apply(modifier='dec')
    for p in m.polygons:
        p.use_smooth = False
    m.calc_loop_triangles()
    print('WODDEC %s %s: %d -> %d triangles' % (src.split('/')[-1], o.name, before, len(m.loop_triangles)))
bpy.ops.export_scene.fbx(filepath=dst, use_selection=False, object_types={'MESH', 'EMPTY'}, mesh_smooth_type='FACE',
                         apply_unit_scale=True, bake_space_transform=False, add_leaf_bones=False, bake_anim=False)
if len(a) > 3:
    s = bpy.context.scene
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    s.collection.objects.link(cam)
    s.camera = cam
    cam.location = (0, -2.2, 0.9)
    cam.rotation_euler = (math.radians(75), 0, 0)
    bpy.ops.object.select_all(action='DESELECT')
    for o in s.objects:
        if o.type == 'MESH':
            o.select_set(True)
    bpy.ops.view3d.camera_to_view_selected()
    s.render.engine = 'BLENDER_WORKBENCH'
    s.display.shading.light = 'STUDIO'
    s.render.resolution_x, s.render.resolution_y = 480, 320
    s.render.filepath = a[3]
    bpy.ops.render.render(write_still=True)
