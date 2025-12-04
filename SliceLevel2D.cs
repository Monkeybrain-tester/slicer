using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SliceLevel2D : Node2D
{
	// ---------------- Signals ----------------
	// SliceManager uses this; keep the exact name "SliceExit".
	[Signal] public delegate void SliceExitEventHandler(Vector2 delta2D);

	// ---------------- Exports ----------------
	[ExportGroup("Scale & Camera")]
	[Export] public float Scale2D = 1.0f;   // pixels per slice unit
	[Export] public float CameraZoom = 1.0f;

	[ExportGroup("Player")]
	// Assign your 2D player scene here (root should be CharacterBody2D with your Player.cs).
	[Export] public PackedScene Player2DScene;

	[ExportGroup("Debug")]
	[Export] public bool EnableDebugCam = true;
	[Export] public bool DrawAabbDebug = true;
	[Export] public bool DrawPolyOutlines = true;

	// ---------------- Background ----------------
	[ExportGroup("Background")]
	// Drag your ColorRect-with-shader here in the inspector
	[Export] public NodePath BackgroundRectPath;
	[Export] public float BackgroundPaddingPx = 400f;  // extra size around the screen

	private ColorRect _backgroundRect;

	// ---------------- Data from SliceManager ----------------
	// These are filled from SliceManager before the scene is added.
	public List<List<Vector2>> Polygons2D = new();   // loops in unscaled slice coords
	public List<Vector2[]> Segments2D = new();       // optional extra segments
	public Vector2 PlayerStart2D = Vector2.Zero;     // unscaled (slice units)

	// Optional: per-polygon material list that SliceManager can fill.
	// e.g. List<Material> from the 3D meshes – we’ll just pull a color for 2D.
	public List<Material> PolygonMaterials2D = new();

	// ---------------- Internals ----------------
	private CharacterBody2D _player;
	private Camera2D _cam;
	private Vector2 _startPosPx;
	private bool _debugMode;

	private ParallaxBackground _parallaxBg;

	// -------------------------------------------------------------------------
	// Ready
	// -------------------------------------------------------------------------
	public override void _Ready()
	{
		// 1) World container
		var world = GetNodeOrNull<Node2D>("World");
		if (world == null)
		{
			world = new Node2D { Name = "World" };
			AddChild(world);
		}

		// 2) Build geometry + collisions from slice
		DrawCrossSectionGeometry(world);
		if (DrawAabbDebug)
			DrawAabbDebugRects(world);

		// 3) Instantiate animated Player2D
		SetupPlayer();

		// 4) Setup parallax background (if present)
		SetupBackground();

		// 5) Direct ColorRect background (if present)
		_backgroundRect = GetNodeOrNull<ColorRect>(BackgroundRectPath);
		SetupBackgroundToViewport();      // initial sizing/position

		// 6) Debug info
		GD.Print($"[Slice2D] Ready. Polygons={Polygons2D?.Count ?? 0}, PlayerStart2D={PlayerStart2D}, Scale2D={Scale2D}");

		if (EnableDebugCam)
			GD.Print("[Slice2D] Press F1 to toggle free-cam mode.");
	}

	public override void _Process(double delta)
	{
		// Keep background centered on camera every frame
		if (_backgroundRect != null && _cam != null)
		{
			var size = _backgroundRect.Size;
			Vector2 camPos = _cam.GlobalPosition;
			_backgroundRect.Position = camPos - size * 0.5f;
		}
	}

	// -------------------------------------------------------------------------
	// Player & camera
	// -------------------------------------------------------------------------
	private void SetupPlayer()
	{
		if (Player2DScene != null)
		{
			var inst = Player2DScene.Instantiate<Node>();
			if (inst is CharacterBody2D body)
			{
				_player = body;
				_player.Name = "Player2D";
				AddChild(_player);
			}
			else
			{
				GD.PushError("[Slice2D] Player2DScene root is not CharacterBody2D. Using fallback dummy player.");
			}
		}

		// Fallback simple player if Player2DScene not set or wrong root type
		if (_player == null)
		{
			_player = new CharacterBody2D { Name = "Player2D" };
			AddChild(_player);

			var sprite = new Sprite2D { Name = "Sprite2D" };
			_player.AddChild(sprite);

			var shape = new CollisionShape2D
			{
				Name = "CollisionShape2D",
				Shape = new RectangleShape2D { Size = new Vector2(12, 24) }
			};
			_player.AddChild(shape);
		}

		// Camera under player (either existing or new)
		_cam = _player.GetNodeOrNull<Camera2D>("Camera2D");
		if (_cam == null)
		{
			_cam = new Camera2D { Name = "Camera2D" };
			_player.AddChild(_cam);
		}

		_cam.Zoom = new Vector2(CameraZoom, CameraZoom);
		_cam.Enabled = true;
		_cam.MakeCurrent();

		// Place player based on PlayerStart2D (already in slice units from SliceManager).
		_startPosPx = PlayerStart2D * Scale2D;
		_player.Position = _startPosPx;
		GD.Print($"[Slice2D] Raw spawn px={_startPosPx} (PlayerStart2D={PlayerStart2D}, Scale2D={Scale2D})");
	}

	// -------------------------------------------------------------------------
	// Background (parallax)
	// -------------------------------------------------------------------------
	private void SetupBackground()
	{
		_parallaxBg = GetNodeOrNull<ParallaxBackground>("ParallaxBackground");
		if (_parallaxBg == null)
			return; // no background, nothing to do

		var layer = _parallaxBg.GetNodeOrNull<ParallaxLayer>("ParallaxLayer");
		if (layer == null)
			return;

		ColorRect rect = null;
		foreach (Node child in layer.GetChildren())
		{
			if (child is ColorRect cr)
			{
				rect = cr;
				break;
			}
		}

		if (rect == null)
			return;

		// We just want "cover the whole screen (+ padding)".
		const float pad = 200f;

		// Make the ColorRect stretch to the full viewport.
		rect.AnchorLeft   = 0f;
		rect.AnchorTop    = 0f;
		rect.AnchorRight  = 1f;
		rect.AnchorBottom = 1f;

		// Extra padding so the camera never sees an edge.
		rect.OffsetLeft   = -pad;
		rect.OffsetTop    = -pad;
		rect.OffsetRight  =  pad;
		rect.OffsetBottom =  pad;

		// Make sure it's behind everything.
		rect.ZIndex       = -100;
		layer.ZIndex      = -100;

		_parallaxBg.ScrollBaseOffset = Vector2.Zero;
	}

	// -------------------------------------------------------------------------
	// Input: exit slice & debug cam toggle
	// -------------------------------------------------------------------------
	public override void _Input(InputEvent e)
	{
		// --- Mouse buttons ---
		if (e is InputEventMouseButton mb && mb.Pressed)
		{
			// RIGHT CLICK – exit slice
			if (mb.ButtonIndex == MouseButton.Right)
			{
				// Stop this event from reaching anything else (like SliceManager)
				GetViewport().SetInputAsHandled();
				RequestExit();
				return;
			}

			// LEFT CLICK – do nothing in Slice2D, just swallow it
			if (mb.ButtonIndex == MouseButton.Left)
			{
				GetViewport().SetInputAsHandled();
				return;
			}
		}

		// --- Debug free-cam toggle (F1) ---
		if (EnableDebugCam && e is InputEventKey key && key.Pressed && key.Keycode == Key.F1)
		{
			GetViewport().SetInputAsHandled();
			_debugMode = !_debugMode;
			if (_cam != null)
				_cam.Position = _player.Position;
			GD.Print(_debugMode ? "[Slice2D] Free-cam enabled." : "[Slice2D] Free-cam disabled.");
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!_debugMode || _cam == null) return;

		// Simple WASD/arrow free-cam when debug mode is on
		Vector2 dir = Vector2.Zero;
		if (Input.IsActionPressed("ui_right")) dir.X += 1;
		if (Input.IsActionPressed("ui_left"))  dir.X -= 1;
		if (Input.IsActionPressed("ui_down"))  dir.Y += 1;
		if (Input.IsActionPressed("ui_up"))    dir.Y -= 1;

		if (dir != Vector2.Zero)
			_cam.Position += dir.Normalized() * (float)(600.0 * delta);
	}

	// -------------------------------------------------------------------------
	// Geometry & collisions
	// -------------------------------------------------------------------------
	private void DrawCrossSectionGeometry(Node2D world)
	{
		if (Polygons2D == null) return;

		for (int polyIndex = 0; polyIndex < Polygons2D.Count; polyIndex++)
		{
			var raw = Polygons2D[polyIndex];
			if (raw == null || raw.Count < 3) continue;

			var poly = SanitizeLoop(raw, 0.0005f);
			if (poly.Count < 3 || MathF.Abs(SignedArea(poly)) < 1e-6f) continue;

			// Determine color from 3D material if available
			Color fillCol = new Color(0.65f, 0.8f, 1f, 0.42f);
			if (PolygonMaterials2D != null && polyIndex < PolygonMaterials2D.Count && PolygonMaterials2D[polyIndex] is BaseMaterial3D bm)
				fillCol = bm.AlbedoColor;

			// Visible fill
			var fill = new Polygon2D
			{
				Polygon = poly.Select(p => p * Scale2D).ToArray(),
				Color = fillCol,
				Antialiased = true
			};
			world.AddChild(fill);

			// Collisions via convex decomposition
			AddConvexCollisionFromPolygon(world, poly, Scale2D);

			// Outline
			if (DrawPolyOutlines)
			{
				var outline = new Line2D
				{
					Width = 2,
					DefaultColor = fillCol.Lightened(0.2f),
					Closed = true,
					Antialiased = true
				};
				foreach (var p in poly)
					outline.AddPoint(p * Scale2D);
				world.AddChild(outline);
			}
		}

		// Extra thin segments (if any)
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
		if (Polygons2D == null) return;

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
			outline.AddPoint(p0);
			outline.AddPoint(p1);
			outline.AddPoint(p2);
			outline.AddPoint(p3);
			world.AddChild(outline);
		}
	}

	// -------------------------------------------------------------------------
	// Utility helpers
	// -------------------------------------------------------------------------
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

	private void RequestExit()
	{
		if (_player == null)
			return;

		var delta2D = (_player.Position - _startPosPx) / Scale2D;
		GD.Print($"[Slice2D] Exit requested, delta2D={delta2D}");
		EmitSignal(nameof(SliceExit), delta2D);

		// SliceManager will move the 3D player; we just go away.
		QueueFree();
	}

	// Combined AABB in unscaled coords
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

	private void SetupBackgroundToViewport()
	{
		if (_backgroundRect == null)
			return;

		// Make sure it’s drawn behind everything
		_backgroundRect.ZIndex = -1000;
		_backgroundRect.ZAsRelative = false;

		// Base size on viewport + padding in all directions
		Vector2 vpSize = GetViewportRect().Size;
		float pad = BackgroundPaddingPx;
		Vector2 size = vpSize + new Vector2(pad * 2f, pad * 2f);

		_backgroundRect.Size = size;

		// If we already have a camera, center it right away
		if (_cam != null)
		{
			Vector2 camPos = _cam.GlobalPosition;
			_backgroundRect.Position = camPos - size * 0.5f;
		}
		else
		{
			// Fallback: center around world origin
			_backgroundRect.Position = -size * 0.5f;
		}
	}
}
