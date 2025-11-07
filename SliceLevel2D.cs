using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 2D slice world generated dynamically from SliceManager.
/// Polygons2D are cross-section loops (in "slice UV" space where +Y is down).
/// Segments2D are 2-point edges (for very thin intersections).
/// Spawns the player analytically at the first floor under the projected X.
/// </summary>
public partial class SliceLevel2D : Node2D
{
	// ---------------- Signals ----------------
	[Signal] public delegate void SliceExitEventHandler(Vector2 delta2D);

	// ---------------- Exports ----------------
	[ExportGroup("Scale & Camera")]
	[Export] public float Scale2D = 1.0f;     // pixels per slice-unit; keep = SliceManager.SliceUnitsPerPixel^-1
	[Export] public float CameraZoom = 1f;  // 0.1 = 10x zoomed-in feel (bigger world)

	[ExportGroup("Player Movement")]
	[Export] public float MoveSpeed = 220f;
	[Export] public float JumpForce = 380f;   // pixels/sec
	[Export] public float Gravity = 980f;     // pixels/sec^2

	[ExportGroup("Debug")]
	[Export] public bool EnableDebugCam = true;
	[Export] public bool DrawAabbDebug = true;
	[Export] public bool DrawPolyOutlines = true;

	// ---------------- Data from SliceManager ----------------
	public List<List<Vector2>> Polygons2D = new();   // loops in unscaled slice coords (CCW preferred). +Y is down
	public List<Vector2[]> Segments2D = new();       // optional line segments in unscaled coords
	public Vector2 PlayerStart2D = Vector2.Zero;     // unscaled slice coords (u, vDown)

	// ---------------- Internals ----------------
	private CharacterBody2D _player;
	private Camera2D _cam;
	private Vector2 _vel;
	private Vector2 _startPosPx;        // store in *pixels* so delta2D = (posPx - startPx)/Scale2D
	private bool _debugMode;

	public override void _Ready()
	{
		// World node to hold geometry
		var world = GetNodeOrNull<Node2D>("World");
		if (world == null)
		{
			world = new Node2D { Name = "World" };
			AddChild(world);
		}

		// Visualize + collisions from slice loops
		DrawCrossSectionGeometry(world);

		// Optional debug boxes/outlines
		if (DrawAabbDebug) DrawAabbDebugRects(world);

		// Ensure we have a player node (or make one)
		_player = GetNodeOrNull<CharacterBody2D>("Player2D");
		if (_player == null)
		{
			_player = new CharacterBody2D { Name = "Player2D" };
			AddChild(_player);

			var sprite = new Sprite2D { Name = "Sprite2D" };
			_player.AddChild(sprite);

			var cshape = new CollisionShape2D
			{
				Name = "CollisionShape2D",
				Shape = new RectangleShape2D { Size = new Vector2(12, 24) }
			};
			_player.AddChild(cshape);
		}
		FixupPlayerHierarchy();

		// Camera child of player
		_cam = _player.GetNodeOrNull<Camera2D>("Camera2D");
		if (_cam == null)
		{
			_cam = new Camera2D { Name = "Camera2D" };
			_player.AddChild(_cam);
		}
		_cam.Zoom = new Vector2(CameraZoom, CameraZoom);
		_cam.PositionSmoothingEnabled = false;
		_cam.MakeCurrent();

		// Analytic spawn using polygon edges (no physics timing)
		AnalyticSpawnAtFloor();

		// Input setup
		if (EnableDebugCam)
			GD.Print("[Slice2D] Press F1 to toggle free-cam mode.");
		SetProcessUnhandledInput(true);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		// Exit actions are created in SliceManager, but accept ESC/LMB here too
		bool wantExit =
			(InputMap.HasAction("slice_exit") && Input.IsActionJustPressed("slice_exit")) ||
			Input.IsActionJustPressed("ui_cancel") ||
			Input.IsActionJustPressed("slice_aim");

		if (wantExit)
		{
			var delta2D = (_player.Position - _startPosPx) / Scale2D; // convert back to slice units
			GD.Print($"[Slice2D] Exit requested, delta2D={delta2D}");
			EmitSignal(SignalName.SliceExit, delta2D);
			GetViewport().SetInputAsHandled();
			QueueFree();
			return;
		}

		// Debug camera toggle
		if (EnableDebugCam && e is InputEventKey key && key.Pressed && key.Keycode == Key.F1)
			ToggleDebugCamera();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_debugMode)
		{
			Vector2 dir = Vector2.Zero;
			if (Input.IsActionPressed("ui_right")) dir.X += 1;
			if (Input.IsActionPressed("ui_left"))  dir.X -= 1;
			if (Input.IsActionPressed("ui_down"))  dir.Y += 1;
			if (Input.IsActionPressed("ui_up"))    dir.Y -= 1;
			_cam.Position += dir.Normalized() * (float)(600 * delta);
			return;
		}

		HandlePlatformerMovement((float)delta);
	}

	// ------------------------------------------------------------
	// Spawn: analytic floor search under the player's X
	// ------------------------------------------------------------
private void AnalyticSpawnAtFloor()
{
	// Player collider half-height in pixels; must match your CollisionShape2D (12x24)
	const float halfHeightPx = 12f;
	const float cushionPx = 2f;

	Vector2 startUV = PlayerStart2D; // unscaled slice coords (u, vDown)

	// Bounds sanity (lets us clamp X)
	if (!GetCombinedAabbUnscaled(out var aabbUV))
	{
		Vector2 pxRaw = startUV * Scale2D;
		_player.Position = pxRaw;
		_startPosPx = pxRaw;
		GD.Print("[Slice2D] No polygons; using raw start position.");
		return;
	}

	float clampedX = Mathf.Clamp(startUV.X, aabbUV.Position.X + 0.001f, aabbUV.End.X - 0.001f);

	// Try to find a valid floor using span-aware query
	if (TrySupportYFromPolys(clampedX, startUV.Y, out float floorY_UV))
	{
		float spawnY_px = floorY_UV * Scale2D - halfHeightPx - cushionPx; // sit **on** that floor
		Vector2 spawnPx = new Vector2(clampedX * Scale2D, spawnY_px);

		_player.Position = spawnPx;
		_startPosPx = spawnPx;
		_player.Velocity = Vector2.Zero;
		_vel = Vector2.Zero;

		GD.Print($"[Slice2D] Analytic spawn px={spawnPx} (floorY_UV={floorY_UV})");
		return;
	}

	// Fallback: shoot from the top of AABB in case start was outside all spans
	float probeY = aabbUV.Position.Y - 1000f;
	if (TrySupportYFromPolys(clampedX, probeY, out float floorFromTop_UV))
	{
		float spawnY_px = floorFromTop_UV * Scale2D - halfHeightPx - cushionPx;
		Vector2 spawnPx = new Vector2(clampedX * Scale2D, spawnY_px);

		_player.Position = spawnPx;
		_startPosPx = spawnPx;
		_player.Velocity = Vector2.Zero;
		_vel = Vector2.Zero;

		GD.Print($"[Slice2D] Fallback spawn px={spawnPx} (from top) floorY_UV={floorFromTop_UV}");
		return;
	}

	// Last resort: raw start
	Vector2 px = startUV * Scale2D;
	_player.Position = px;
	_startPosPx = px;
	_player.Velocity = Vector2.Zero;
	_vel = Vector2.Zero;

	GD.Print("[Slice2D] No support found; using raw start position.");
}


	// Combine AABB of all loops in *unscaled* slice space
	private bool GetCombinedAabbUnscaled(out Rect2 aabbUV)
	{
		aabbUV = default;
		if (Polygons2D == null || Polygons2D.Count == 0) return false;

		bool first = true;
		foreach (var poly in Polygons2D)
		{
			if (poly == null || poly.Count == 0) continue;
			Vector2 min = new(float.MaxValue, float.MaxValue);
			Vector2 max = new(float.MinValue, float.MinValue);
			foreach (var p in poly)
			{
				if (p.X < min.X) min.X = p.X;
				if (p.Y < min.Y) min.Y = p.Y;
				if (p.X > max.X) max.X = p.X;
				if (p.Y > max.Y) max.Y = p.Y;
			}
			var r = new Rect2(min, max - min);
			aabbUV = first ? r : aabbUV.Merge(r);
			first = false;
		}
		return !first;
	}

	// Vertical support query in *unscaled* space.
	// Returns the smallest Y >= y0UV where a vertical ray at xUV intersects any polygon edge.
private bool TrySupportYFromPolys(float xUV, float y0UV, out float yHitUV)
{
	const float EPS = 1e-6f;
	yHitUV = float.NaN;

	// Collect all intersections with polygon edges at this x
	var hits = new List<float>(32);

	foreach (var poly in Polygons2D)
	{
		if (poly == null || poly.Count < 2) continue;

		for (int i = 0; i < poly.Count; i++)
		{
			Vector2 a = poly[i];
			Vector2 b = poly[(i + 1) % poly.Count];

			// Skip vertical edges (avoid division noise)
			float minX = MathF.Min(a.X, b.X), maxX = MathF.Max(a.X, b.X);
			if (maxX - minX < EPS) continue;
			if (xUV < minX - EPS || xUV > maxX + EPS) continue;

			float t = (xUV - a.X) / (b.X - a.X);
			if (t < -EPS || t > 1 + EPS) continue;

			float y = a.Y + t * (b.Y - a.Y);
			hits.Add(y);
		}
	}

	if (hits.Count == 0)
		return false;

	hits.Sort(); // ascending: smaller Y = higher on screen (remember: +Y is down)

	// If we have an odd number, drop the last to keep pairs well-formed
	if ((hits.Count & 1) == 1)
		hits.RemoveAt(hits.Count - 1);

	if (hits.Count == 0)
		return false;

	// 1) Inside any span? choose its floor
	for (int i = 0; i + 1 < hits.Count; i += 2)
	{
		float ceilY = hits[i];
		float floorY = hits[i + 1];

		// "Inside" means between ceil and floor in Y-down coordinates
		if (y0UV >= ceilY - EPS && y0UV <= floorY + EPS)
		{
			yHitUV = floorY; // snap to the floor of the span you’re in
			return true;
		}
	}

	// 2) Above the first span → snap to that span's ceiling
	if (y0UV < hits[0] - EPS)
	{
		yHitUV = hits[0];
		return true;
	}

	// 3) Below all spans → snap to the last span's floor
	yHitUV = hits[hits.Count - 1];
	return true;
}

	// ------------------------------------------------------------
	// Movement
	// ------------------------------------------------------------
	private void HandlePlatformerMovement(float dt)
	{
		float dir = 0f;
		if (Input.IsActionPressed("ui_right") || Input.IsActionPressed("move_right")) dir += 1;
		if (Input.IsActionPressed("ui_left")  || Input.IsActionPressed("move_left"))  dir -= 1;

		_vel.X = dir * MoveSpeed;
		_vel.Y += Gravity * dt;

		if ((Input.IsActionJustPressed("ui_accept") || Input.IsActionJustPressed("jump")) && _player.IsOnFloor())
			_vel.Y = -JumpForce;

		_player.Velocity = _vel;
		_player.MoveAndSlide();
		_vel = _player.Velocity;
	}

	// ------------------------------------------------------------
	// Geometry build & debug draw
	// ------------------------------------------------------------
	private void DrawCrossSectionGeometry(Node2D world)
	{
		// Polygon fills + collisions
		foreach (var raw in Polygons2D)
		{
			var poly = SanitizeLoop(raw, 0.0005f);
			if (poly.Count < 3 || MathF.Abs(SignedArea(poly)) < 1e-6f) continue;

			// Visible fill (optional)
			var fill = new Polygon2D
			{
				Polygon = poly.Select(p => p * Scale2D).ToArray(),
				Color = new Color(0.65f, 0.8f, 1f, 0.42f),
				Antialiased = true
			};
			world.AddChild(fill);

			// Collisions via convex decomposition (fallback to triangulation)
			AddConvexCollisionFromPolygon(world, poly, Scale2D);

			// Outline for sanity
			if (DrawPolyOutlines)
			{
				var outline = new Line2D
				{
					Width = 2,
					DefaultColor = new Color(0.2f, 0.6f, 1f, 0.9f),
					Closed = true,
					Antialiased = true
				};
				foreach (var p in poly) outline.AddPoint(p * Scale2D);
				world.AddChild(outline);
			}
		}

		// Segment lines + collisions (for very thin intersections)
		foreach (var seg in Segments2D)
		{
			if (seg == null || seg.Length != 2) continue;

			var line = new Line2D
			{
				Width = 2f,
				DefaultColor = new Color(0.95f, 0.8f, 0.35f, 1f),
				Antialiased = true
			};
			line.AddPoint(seg[0] * Scale2D);
			line.AddPoint(seg[1] * Scale2D);
			world.AddChild(line);

			var body = new StaticBody2D();
			var cshape = new CollisionShape2D
			{
				Shape = new SegmentShape2D
				{
					A = seg[0] * Scale2D,
					B = seg[1] * Scale2D
				}
			};
			body.AddChild(cshape);
			world.AddChild(body);
		}
	}

	private void DrawAabbDebugRects(Node2D world)
	{
		foreach (var raw in Polygons2D)
		{
			if (raw == null || raw.Count == 0) continue;

			Vector2 min = new(float.MaxValue, float.MaxValue);
			Vector2 max = new(float.MinValue, float.MinValue);
			foreach (var p in raw)
			{
				if (p.X < min.X) min.X = p.X;
				if (p.Y < min.Y) min.Y = p.Y;
				if (p.X > max.X) max.X = p.X;
				if (p.Y > max.Y) max.Y = p.Y;
			}

			var p0 = min * Scale2D;
			var p1 = new Vector2(max.X, min.Y) * Scale2D;
			var p2 = max * Scale2D;
			var p3 = new Vector2(min.X, max.Y) * Scale2D;

			var rect = new Polygon2D
			{
				Polygon = new[] { p0, p1, p2, p3 },
				Color = new Color(1f, 0.2f, 0.2f, 0.15f),
				Antialiased = true
			};
			world.AddChild(rect);

			var outline = new Line2D
			{
				Width = 2,
				DefaultColor = new Color(1f, 0.2f, 0.2f, 0.9f),
				Closed = true
			};
			outline.AddPoint(p0); outline.AddPoint(p1); outline.AddPoint(p2); outline.AddPoint(p3);
			world.AddChild(outline);
		}
	}

	// ------------------------------------------------------------
	// Utility
	// ------------------------------------------------------------
	private static float SignedArea(IReadOnlyList<Vector2> pts)
	{
		double s = 0;
		for (int i = 0; i < pts.Count; i++)
		{
			Vector2 a = pts[i];
			Vector2 b = pts[(i + 1) % pts.Count];
			s += (double)a.X * b.Y - (double)a.Y * b.X;
		}
		return (float)(0.5 * s);
	}

	// Remove near-duplicates, colinears; enforce CCW (positive area).
	private static List<Vector2> SanitizeLoop(List<Vector2> input, float eps)
	{
		if (input == null || input.Count < 3) return input ?? new();

		var tmp = new List<Vector2>(input.Count);
		foreach (var p in input)
		{
			if (tmp.Count == 0 || (p - tmp[^1]).LengthSquared() > eps * eps)
				tmp.Add(p);
		}
		if (tmp.Count > 1 && (tmp[0] - tmp[^1]).LengthSquared() <= eps * eps)
			tmp.RemoveAt(tmp.Count - 1);

		// remove colinears
		if (tmp.Count >= 3)
		{
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
			tmp = clean;
		}

		if (tmp.Count >= 3 && SignedArea(tmp) < 0f)
			tmp.Reverse();

		return tmp;
	}

	/// <summary>Add polygon collisions under 'parent' via convex decomposition (fallback to triangle pieces).</summary>
	private static void AddConvexCollisionFromPolygon(Node2D parent, List<Vector2> poly, float scale)
	{
		if (poly == null || poly.Count < 3) return;

		var convexes = Geometry2D.DecomposePolygonInConvex(poly.ToArray());
		if (convexes != null && convexes.Count > 0)
		{
			foreach (var cv in convexes)
			{
				if (cv == null || cv.Length < 3) continue;

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

		// Fallback: triangulate
		var idx = Geometry2D.TriangulatePolygon(poly.ToArray());
		for (int i = 0; i + 2 < idx.Length; i += 3)
		{
			var a = poly[idx[i]];
			var b = poly[idx[i + 1]];
			var c = poly[idx[i + 2]];

			var body = new StaticBody2D();
			var col = new CollisionPolygon2D
			{
				BuildMode = CollisionPolygon2D.BuildModeEnum.Solids,
				Polygon = new[] { a * scale, b * scale, c * scale }
			};
			body.AddChild(col);
			parent.AddChild(body);
		}
	}

	private void FixupPlayerHierarchy()
	{
		_player.Scale = Vector2.One;
		_player.Rotation = 0f;

		var sprite = _player.GetNodeOrNull<Sprite2D>("Sprite2D");
		if (sprite != null)
		{
			sprite.TopLevel = false;
			sprite.Centered = true;
			sprite.Offset = Vector2.Zero;
			sprite.ZIndex = 10;
			// Optional: give a default texture or modulate color if you want
		}

		var col = _player.GetNodeOrNull<CollisionShape2D>("CollisionShape2D");
		if (col != null)
		{
			col.TopLevel = false;
			col.Position = Vector2.Zero;
			col.Rotation = 0f;
			col.Scale = Vector2.One;
			if (col.Shape is RectangleShape2D rect)
				rect.Size = new Vector2(12, 24);
		}
	}

	private void ToggleDebugCamera()
	{
		_debugMode = !_debugMode;
		_cam.Position = _player.Position;
		GD.Print(_debugMode ? "[Slice2D] Free-cam enabled." : "[Slice2D] Free-cam disabled.");
	}
}
