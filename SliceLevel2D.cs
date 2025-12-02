// File: 2Dportion/SliceLevel2D.cs
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 2D slice world generated dynamically from SliceManager.
/// 
/// Polygons2D are cross-section loops in "slice space" (unscaled),
/// where +Y is down. Segments2D are optional 2-point edges for thin
/// intersections. PlayerStart2D is given in the same slice space.
/// 
/// This script now:
/// - Trusts PlayerStart2D directly (no extra snapping).
/// - Builds 2D geometry & collisions from Polygons2D / Segments2D.
/// - Provides simple platformer movement & an optional debug camera.
/// </summary>
public partial class SliceLevel2D : Node2D
{
	// ---------------- Signals ----------------
	[Signal] public delegate void SliceExitEventHandler(Vector2 delta2D);

	// ---------------- Exports ----------------
	[ExportGroup("Scale & Camera")]
	/// <summary>
	/// Pixels per "slice unit". For a 1:1 mapping with SliceManager's
	/// SliceUnitsPerPixel, a good default is Scale2D = 1.0.
	/// </summary>
	[Export] public float Scale2D = 1.0f;
	[Export] public float CameraZoom = 1f;   // e.g., 0.1 for big zoom-in

	[ExportGroup("Player Movement")]
	[Export] public float MoveSpeed = 220f;
	[Export] public float JumpForce = 380f;  // pixels/sec
	[Export] public float Gravity = 980f;    // pixels/sec^2

	[ExportGroup("Debug")]
	[Export] public bool EnableDebugCam = true;
	[Export] public bool DrawAabbDebug = true;
	[Export] public bool DrawPolyOutlines = true;

	// ---------------- Data from SliceManager ----------------
	/// <summary>Loops in unscaled slice coords. +Y is down.</summary>
	public List<List<Vector2>> Polygons2D = new();
	/// <summary>Optional line segments in unscaled coords.</summary>
	public List<Vector2[]> Segments2D = new();
	/// <summary>Unscaled slice coords (u, vDown) for player spawn.</summary>
	public Vector2 PlayerStart2D = Vector2.Zero;

	// ---------------- Internals ----------------
	private CharacterBody2D _player;
	private Camera2D _cam;
	private Vector2 _vel;
	private Vector2 _startPosPx; // spawn position in pixels
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

		// Visuals + collisions from slice loops
		DrawCrossSectionGeometry(world);

		// Optional debug AABB boxes
		if (DrawAabbDebug)
			DrawAabbDebugRects(world);

		// Ensure we have a player node
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

		// Camera as child of player
		_cam = _player.GetNodeOrNull<Camera2D>("Camera2D");
		if (_cam == null)
		{
			_cam = new Camera2D { Name = "Camera2D" };
			_player.AddChild(_cam);
		}
		_cam.Zoom = new Vector2(CameraZoom, CameraZoom);
		_cam.PositionSmoothingEnabled = false;
		_cam.MakeCurrent();

		// ----------------------------
		// Spawn: trust PlayerStart2D
		// ----------------------------
		Vector2 px = PlayerStart2D * Scale2D;
		GD.Print($"[Slice2D] Raw spawn px={px} (PlayerStart2D={PlayerStart2D}, Scale2D={Scale2D})");

		_player.Position = px;
		_startPosPx = px;
		_vel = Vector2.Zero;
		_player.Velocity = Vector2.Zero;

		// Debug info
		if (Polygons2D != null && Polygons2D.Count > 0)
		{
			float minY = float.MaxValue, maxY = float.MinValue;
			int count = 0;
			foreach (var poly in Polygons2D)
			{
				if (poly == null) continue;
				count += poly.Count;
				foreach (var p in poly)
				{
					if (p.Y < minY) minY = p.Y;
					if (p.Y > maxY) maxY = p.Y;
				}
			}
			GD.Print($"[Slice2D DEBUG] Polygons2D loops={Polygons2D.Count}, pts={count}, Y-range=[{minY}, {maxY}] (unscaled)");
			GD.Print($"[Slice2D DEBUG] PlayerStart2D={PlayerStart2D}, Scale2D={Scale2D}");
		}
		else
		{
			GD.Print("[Slice2D DEBUG] No Polygons2D provided.");
		}

		if (EnableDebugCam)
			GD.Print("[Slice2D] Press F1 to toggle free-cam mode.");

		SetProcessUnhandledInput(true);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		bool wantExit =
			(InputMap.HasAction("slice_exit") && Input.IsActionJustPressed("slice_exit")) ||
			Input.IsActionJustPressed("ui_cancel") ||
			Input.IsActionJustPressed("slice_aim");

		if (wantExit)
		{
			var delta2D = (_player.Position - _startPosPx) / Scale2D; // slice units
			GD.Print($"[Slice2D] Exit requested, delta2D={delta2D}");
			EmitSignal(SignalName.SliceExit, delta2D);
			GetViewport().SetInputAsHandled();
			QueueFree();
			return;
		}

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
			if (dir != Vector2.Zero)
				_cam.Position += dir.Normalized() * (float)(600 * delta);
			return;
		}

		HandlePlatformerMovement((float)delta);
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
	// Geometry & debug drawing
	// ------------------------------------------------------------
	private void DrawCrossSectionGeometry(Node2D world)
	{
		// Polygon fills + collisions
		foreach (var raw in Polygons2D)
		{
			var poly = SanitizeLoop(raw, 0.0005f);
			if (poly.Count < 3 || MathF.Abs(SignedArea(poly)) < 1e-6f) continue;

			// Visible fill
			var fill = new Polygon2D
			{
				Polygon = poly.Select(p => p * Scale2D).ToArray(),
				Color = new Color(0.65f, 0.8f, 1f, 0.42f),
				Antialiased = true
			};
			world.AddChild(fill);

			// Collision solids
			AddConvexCollisionFromPolygon(world, poly, Scale2D);

			// Outline
			if (DrawPolyOutlines)
			{
				var outline = new Line2D
				{
					Width = 2,
					DefaultColor = new Color(0.2f, 0.6f, 1f, 0.9f),
					Closed = true,
					Antialiased = true
				};
				foreach (var p in poly)
					outline.AddPoint(p * Scale2D);
				world.AddChild(outline);
			}
		}

		// Segment lines + collisions (thin intersections)
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

	/// <summary>
	/// Cleans up a polygon loop:
	/// - removes near-duplicate points
	/// - removes colinear points
	/// - enforces CCW (positive area).
	/// </summary>
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
				if (MathF.Abs(cross) > eps)
					clean.Add(b);
			}
			tmp = clean;
		}

		if (tmp.Count >= 3 && SignedArea(tmp) < 0f)
			tmp.Reverse();

		return tmp;
	}

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
