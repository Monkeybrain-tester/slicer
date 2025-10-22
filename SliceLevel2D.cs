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

		// Player
		_player = GetNode<CharacterBody2D>("Player2D");
		_player.Position = PlayerStart2D * Scale2D;
		_startPos = _player.Position;

		// Camera (Godot 4 C#)
		var cam = _player.GetNode<Camera2D>("Camera2D");
		cam.MakeCurrent();

		// UI hint (optional)
		var label = GetNodeOrNull<Label>("UI/Label");
		if (label != null) label.Text = "LMB: Exit slice";
	}

	public override void _UnhandledInput(InputEvent e)
	{
		// Reuse the same action to exit
		if (e.IsActionPressed("slice_aim"))
		{
			var delta2D = (_player.Position - _startPos) / Scale2D;
			EmitSignal(SignalName.SliceExit, delta2D);
			QueueFree();
		}
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
}
