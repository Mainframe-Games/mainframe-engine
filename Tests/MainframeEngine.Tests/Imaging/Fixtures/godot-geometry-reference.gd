# Godot 4.7.2 reference for BitMaskTests/Geometry2DTests: run headless and save the JSON after "REF" as
# godot-geometry-reference.json:  Godot --headless --script godot-geometry-reference.gd
extends SceneTree

func disc() -> BitMap:
	var b := BitMap.new()
	b.create(Vector2i(64, 64))
	for y in 64:
		for x in 64:
			b.set_bit(x, y, (x - 32) * (x - 32) + (y - 32) * (y - 32) <= 400)
	return b

func blobs() -> BitMap:
	# two diamonds touching at one corner pixel diagonal, a ring with a hole, and a lone rect
	var b := BitMap.new()
	b.create(Vector2i(64, 40))
	for y in 40:
		for x in 64:
			var d1 = abs(x - 8) + abs(y - 8) <= 6
			var d2 = abs(x - 21) + abs(y - 21) <= 6
			var r2 = (x - 44) * (x - 44) + (y - 14) * (y - 14)
			var ring = r2 <= 100 and r2 >= 25
			var rect = x >= 2 and x < 12 and y >= 28 and y < 36
			b.set_bit(x, y, d1 or d2 or ring or rect)
	return b

func polys(b: BitMap, eps: float):
	var out = []
	for p in b.opaque_to_polygons(Rect2i(Vector2i.ZERO, b.get_size()), eps):
		var pts = []
		for v in p:
			pts.append([v.x, v.y])
		out.append(pts)
	return out

func paths(list):
	var out = []
	for p in list:
		var pts = []
		for v in p:
			pts.append([snappedf(v.x, 0.0001), snappedf(v.y, 0.0001)])
		out.append(pts)
	return out

func _init():
	var r = {}
	r["disc2"] = polys(disc(), 2.0)
	r["disc05"] = polys(disc(), 0.5)
	r["blobs2"] = polys(blobs(), 2.0)
	var g = blobs()
	g.grow_mask(3, Rect2i(0, 0, 64, 40))
	r["grow3"] = g.get_true_bit_count()
	var s = blobs()
	s.grow_mask(-2, Rect2i(0, 0, 64, 40))
	r["shrink2"] = s.get_true_bit_count()
	var gr = blobs()
	gr.grow_mask(2, Rect2i(0, 0, 30, 30))
	r["grow2rect"] = gr.get_true_bit_count()
	var rs = disc()
	rs.resize(Vector2i(40, 24))
	r["resize"] = rs.get_true_bit_count()
	r["count"] = blobs().get_true_bit_count()
	var a = PackedVector2Array([Vector2(0, 0), Vector2(10, 0), Vector2(10, 10), Vector2(0, 10)])
	var b = PackedVector2Array([Vector2(5, 5), Vector2(15, 5), Vector2(15, 15), Vector2(5, 15)])
	r["merge"] = paths(Geometry2D.merge_polygons(a, b))
	r["clip"] = paths(Geometry2D.clip_polygons(a, b))
	r["intersect"] = paths(Geometry2D.intersect_polygons(a, b))
	r["exclude"] = paths(Geometry2D.exclude_polygons(a, b))
	var inner = PackedVector2Array([Vector2(2, 2), Vector2(8, 2), Vector2(8, 8), Vector2(2, 8)])
	r["hole"] = paths(Geometry2D.clip_polygons(a, inner))
	r["offset_sq"] = paths(Geometry2D.offset_polygon(a, 2.5))
	r["offset_round"] = paths(Geometry2D.offset_polygon(a, 2.5, Geometry2D.JOIN_ROUND))
	r["offset_miter"] = paths(Geometry2D.offset_polygon(a, 2.5, Geometry2D.JOIN_MITER))
	r["offset_in"] = paths(Geometry2D.offset_polygon(a, -2.0))
	var line = PackedVector2Array([Vector2(0, 0), Vector2(20, 0), Vector2(20, 10)])
	r["offset_line"] = paths(Geometry2D.offset_polyline(line, 1.5))
	r["clip_line"] = paths(Geometry2D.clip_polyline_with_polygon(PackedVector2Array([Vector2(-5, 5), Vector2(20, 5)]), a))
	r["intersect_line"] = paths(Geometry2D.intersect_polyline_with_polygon(PackedVector2Array([Vector2(-5, 5), Vector2(20, 5)]), a))
	var concave = PackedVector2Array([Vector2(0, 0), Vector2(10, 0), Vector2(10, 10), Vector2(5, 4), Vector2(0, 10)])
	r["triangulate"] = Array(Geometry2D.triangulate_polygon(concave))
	r["clockwise"] = [Geometry2D.is_polygon_clockwise(a), Geometry2D.is_polygon_clockwise(PackedVector2Array([Vector2(0, 0), Vector2(0, 10), Vector2(10, 10), Vector2(10, 0)]))]
	print("REF" + JSON.stringify(r))
	quit()
