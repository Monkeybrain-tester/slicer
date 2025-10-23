using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SliceLevel2D : Node2D
{
	[Signal] public delegate void SliceExitEventHandler(Vector2 delta2D);

	[Export] public float Scale2D = 1.0f; // keep 1.0 so 1 unit in 3D == 1 unit in 2D

	// Filled by SliceManager before instancing
	public List<List<Vector2>> Polygons2D = new();  // each is a closed loop in 2D slice space (CCW preferred)
	public Vector2 PlayerStart2D = Vector2.Zero;

	private CharacterBody2D _player;
	private Vector2 _startPos;

	public override void _Ready()
	{
		// --- attach geometry under "World" ---
		var world = GetNode<Node2D>("World");

		GD.Print($"[Slice2D] Polygons: {Polygons2D?.Count ?? 0}, Start: {PlayerStart2D}");

		SetProcessInput(true);
		SetProcessUnhandledInput(true);
		foreach (var rawPoly in Polygons2D)
		{
			var poly = SanitizeLoop(rawPoly, 0.0005f);
			if (poly.Count < 3 || MathF.Abs(SignedArea(poly)) < 1e-5f)
				continue;

			// Visual (nice translucent fill)
			var polyVis = new Polygon2D
			{
				Polygon = poly.Select(p => p * Scale2D).ToArray(),
				Color = new Color(0.7f, 0.8f, 1f, 0.45f),
				Antialiased = true
			};
			world.AddChild(polyVis);

			// Robust collisions
			AddConvexCollisionFromPolygon(world, poly, Scale2D);
		}

	// --- Debug cross at player start ---
	var cross = new Line2D { Width = 2 };
	cross.AddPoint(PlayerStart2D * Scale2D + new Vector2(-8, 0));
	cross.AddPoint(PlayerStart2D * Scale2D + new Vector2(8, 0));
	cross.AddPoint(PlayerStart2D * Scale2D + new Vector2(0, -8));
	cross.AddPoint(PlayerStart2D * Scale2D + new Vector2(0, 8));
	world.AddChild(cross);

	// --- Debug print polygon bounds ---
	foreach (var raw in Polygons2D)
	{
		if (raw.Count < 3) continue;
		var min = new Vector2(float.MaxValue, float.MaxValue);
		var max = new Vector2(float.MinValue, float.MinValue);
		foreach (var p in raw)
		{
			min = min.Min(p);
			max = max.Max(p);
		}
		GD.Print($"[Slice2D] loop AABB min={min} max={max}");
	}

	GD.Print($"[Slice2D] PlayerStart2D(px) = {PlayerStart2D * Scale2D}");

Vector2 start2D = PlayerStart2D;       // projected start from 3D (already V-flipped)
if (GetCombinedAabb(out var aabb))
{
	// Clamp X into the level bounds; place Y slightly ABOVE the slice (smaller Y = higher up)
	float clampedX = Mathf.Clamp(start2D.X, aabb.Position.X, aabb.Position.X + aabb.Size.X);
	float aboveY   = aabb.Position.Y - 24f; // 24px above top of slice

	start2D = new Vector2(clampedX, aboveY);
}


// Raycast straight down to find the first platform under start2D
var space = GetWorld2D().DirectSpaceState;
var from  = (start2D * Scale2D) + new Vector2(0, -2000);
var to    = (start2D * Scale2D) + new Vector2(0,  2000);

var query = PhysicsRayQueryParameters2D.Create(from, to);
query.CollideWithAreas = false;
query.CollideWithBodies = true;
var hit = space.IntersectRay(query);

// Half-height of the player collider if you use 12x24 (tweak if different)
float halfHeight = 12f;

Vector2 finalSpawn = start2D * Scale2D;
if (hit.Count > 0 && hit.ContainsKey("position"))
{
	var pos = (Vector2)hit["position"];
	// Place the player so feet sit on the surface (subtract half height)
	finalSpawn = new Vector2(start2D.X * Scale2D, pos.Y - halfHeight);
}


		// Player
		_player = GetNode<CharacterBody2D>("Player2D");
_player.Position = PlayerStart2D * Scale2D;
_startPos = _player.Position;

// --- Decide a safe spawn point ---
FixupPlayerHierarchy();



var cam = _player.GetNode<Camera2D>("Camera2D");
cam.PositionSmoothingEnabled = false;   // turn off while debugging
cam.MakeCurrent();
cam.Offset = Vector2.Zero;
cam.Zoom = new Vector2(0.1f, 0.1f);                 // 1:1 scale

GetNode<Node2D>("World").ZIndex = 0;
_player.ZIndex = 10;

		// UI hint (optional)
		var label = GetNodeOrNull<Label>("UI/Label");
		if (label != null) label.Text = "LMB: Exit slice";
	}

public override void _UnhandledInput(InputEvent e)
{
	bool wantExit =
		(InputMap.HasAction("slice_exit") && Input.IsActionJustPressed("slice_exit")) ||
		Input.IsActionJustPressed("ui_cancel") ||     // ESC
		Input.IsActionJustPressed("slice_aim");       // LMB (optional)

	if (!wantExit) return;

	var delta2D = (_player.Position - _startPos) / Scale2D;
	GD.Print($"[Slice2D] Exit requested, delta2D={delta2D}");
	EmitSignal(SignalName.SliceExit, delta2D);

	GetViewport().SetInputAsHandled();
	QueueFree();
}


	// -----------------------------
	// Helpers (geometry & collision)
	// -----------------------------

	// Shoelace (signed) area; + = CCW, - = CW
	private static float SignedArea(IReadOnlyList<Vector2> pts)
	{
		if (pts == null || pts.Count < 3) return 0f;
		double sum = 0.0;
		for (int i = 0; i < pts.Count; i++)
		{
			var a = pts[i];
			var b = pts[(i + 1) % pts.Count];
			sum += (double)a.X * b.Y - (double)a.Y * b.X;
		}
		return (float)(0.5 * sum);
	}

	// Remove dup/near-dup points, strip colinears, enforce CCW
	private static List<Vector2> SanitizeLoop(List<Vector2> input, float eps = 0.0005f)
	{
		if (input == null || input.Count < 3) return new List<Vector2>(input ?? new());

		// 1) remove near-duplicate consecutive points
		var tmp = new List<Vector2>(input.Count);
		for (int i = 0; i < input.Count; i++)
		{
			var p = input[i];
			if (tmp.Count == 0 || (p - tmp[tmp.Count - 1]).LengthSquared() > eps * eps)
				tmp.Add(p);
		}
		// close-loop cleanup
		if (tmp.Count >= 2 && (tmp[0] - tmp[tmp.Count - 1]).LengthSquared() <= eps * eps)
			tmp.RemoveAt(tmp.Count - 1);

		if (tmp.Count < 3) return tmp;

		// 2) remove colinear points
		var clean = new List<Vector2>(tmp.Count);
		for (int i = 0; i < tmp.Count; i++)
		{
			Vector2 a = tmp[(i - 1 + tmp.Count) % tmp.Count];
			Vector2 b = tmp[i];
			Vector2 c = tmp[(i + 1) % tmp.Count];
			var ab = b - a;
			var bc = c - b;
			float cross = ab.X * bc.Y - ab.Y * bc.X;
			if (MathF.Abs(cross) > eps) clean.Add(b);
		}

		if (clean.Count < 3) return clean;

		// 3) enforce CCW (positive area)
		if (SignedArea(clean) < 0f) clean.Reverse();

		return clean;
	}

	/// <summary>
	/// Adds collision as multiple convex pieces under 'parent'.
	/// Tries convex decomposition first (Geometry2D.DecomposePolygonInConvex).
	/// If it fails/returns empty, falls back to triangulation.
	/// </summary>
	private static void AddConvexCollisionFromPolygon(Node2D parent, List<Vector2> poly, float scale)
	{
		if (poly == null || poly.Count < 3) return;

		// Try convex decomposition
		var convexes = Geometry2D.DecomposePolygonInConvex(poly.ToArray());
		if (convexes != null && convexes.Count > 0)
		{
			foreach (var cv in convexes)
			{
				if (cv == null || cv.Length < 3) continue;
				// area check
				if (MathF.Abs(SignedArea(new List<Vector2>(cv))) < 1e-7f) continue;

				var body = new StaticBody2D();
				var col = new CollisionPolygon2D
				{
					BuildMode = CollisionPolygon2D.BuildModeEnum.Solids,
					Polygon = cv.Select(p => p * scale).ToArray()
				};
				body.AddChild(col);
				parent.AddChild(body);
			}
			return;
		}

		// Fallback: triangulate (returns indices)
		var idx = Geometry2D.TriangulatePolygon(poly.ToArray());
		for (int i = 0; i + 2 < idx.Length; i += 3)
		{
			var a = poly[idx[i]];
			var b = poly[idx[i + 1]];
			var c = poly[idx[i + 2]];

			// area check
			if (MathF.Abs(SignedArea(new List<Vector2> { a, b, c })) < 1e-9f) continue;

			var body = new StaticBody2D();
			var col = new CollisionPolygon2D
			{
				BuildMode = CollisionPolygon2D.BuildModeEnum.Solids,
				Polygon = new Vector2[] { a * scale, b * scale, c * scale }
			};
			body.AddChild(col);
			parent.AddChild(body);
		}
	}
	
	private void EnsureExitAction()
{
	if (!InputMap.HasAction("slice_exit"))
	{
		InputMap.AddAction("slice_exit");

		var rmb = new InputEventMouseButton { ButtonIndex = MouseButton.Right };
		InputMap.ActionAddEvent("slice_exit", rmb);

		var eKey = new InputEventKey { Keycode = Key.E };
		InputMap.ActionAddEvent("slice_exit", eKey);
	}
}

// Combine AABB of all polygons (already in 2D coords, not scaled)
private bool GetCombinedAabb(out Rect2 aabb)
{
	aabb = default;
	if (Polygons2D == null || Polygons2D.Count == 0) return false;

	bool first = true;
	foreach (var poly in Polygons2D)
	{
		if (poly == null || poly.Count == 0) continue;
		Vector2 min = new(float.MaxValue, float.MaxValue);
		Vector2 max = new(float.MinValue, float.MinValue);
		foreach (var p in poly)
		{
			min.X = MathF.Min(min.X, p.X);
			min.Y = MathF.Min(min.Y, p.Y);
			max.X = MathF.Max(max.X, p.X);
			max.Y = MathF.Max(max.Y, p.Y);
		}
		var r = new Rect2(min, max - min);
		aabb = first ? r : aabb.Merge(r);
		first = false;
	}
	return !first;
}

private void FixupPlayerHierarchy()
{
	// 1) Normalize Player2D transform
	_player.Scale = Vector2.One;
	_player.Rotation = 0f;

	// 2) Fix Sprite2D
	var sprite = _player.GetNodeOrNull<Sprite2D>("Sprite2D");
	if (sprite != null)
	{
		sprite.TopLevel = false;            // MUST follow parent
		sprite.Centered = true;             // origin at texture center
		sprite.Offset = Vector2.Zero;
		sprite.Scale = Vector2.One;
		sprite.Rotation = 0f;
		sprite.ZIndex = 10;                 // draw above platforms
	}

	// 3) Fix CollisionShape2D (or CollisionPolygon2D) under Player2D
	var col = _player.GetNodeOrNull<CollisionShape2D>("CollisionShape2D");
	if (col != null)
	{
		col.TopLevel = false;
		col.Position = Vector2.Zero;        // collider aligned with body origin
		col.Rotation = 0f;
		col.Scale = Vector2.One;
		// Optional sanity: use a rectangle 12x24
		if (col.Shape is RectangleShape2D rect)
			rect.Size = new Vector2(12, 24);
	}

	// 4) Camera must be a CHILD of Player2D, not the root
	var cam = _player.GetNodeOrNull<Camera2D>("Camera2D");
	if (cam != null)
	{
		cam.TopLevel = false;
		cam.PositionSmoothingEnabled = false;  // disable while debugging
		cam.Offset = Vector2.Zero;
		cam.Zoom = new Vector2(0.1f, 0.1f);
		cam.Rotation = 0f;
		cam.MakeCurrent();
	}
}



}
