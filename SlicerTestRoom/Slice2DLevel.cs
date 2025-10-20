// File: Slice2DLevel.cs
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Builds a transient 2D level from sliced intersection loops.
/// Projects 3D points to 2D via plane basis (origin, U, V), creates colliders,
/// spawns a simple 2D player + camera, and reports U-displacement on exit.
/// </summary>
public partial class Slice2DLevel : Node
{
	public class SlicePolygon
	{
		public List<Vector3> WorldLoop = new List<Vector3>(); // world-space loop points
	}

	// Plane basis (world space)
	private Vector3 _origin;   // plane origin (world)
	private Vector3 _u;        // plane tangent (forward in-slice)
	private Vector3 _v;        // plane bitangent (up in-slice)

	// Built scene refs
	private Node2D _root2D;
	private CharacterBody2D _player2D;
	private Camera2D _cam2D;

	private float _uStart;     // starting U of the player
	private float _uCurrent;   // updated as player moves

	private Color _tint = new Color(0.9f, 0.5f, 1.0f, 1.0f); // for optional fill/debug

	/// <summary>
	/// Build and enter the 2D level.
	/// </summary>
	public void BuildAndEnter(
		Node treeParentFor2D,               // where to add the 2D level (e.g., scene root)
		Vector3 origin, Vector3 u, Vector3 v,
		List<SlicePolygon> polygons2DInput,
		Color tintColor = default)
	{
		_origin = origin;
		_u = u.Normalized();
		_v = v.Normalized();
		if (tintColor != default) _tint = tintColor;

		// Root 2D container
		_root2D = new Node2D { Name = "SliceLevel2D" };
		treeParentFor2D.AddChild(_root2D);

		// Optional subtle overlay
		var uiLayer = new CanvasLayer();
		var fade = new ColorRect
		{
			Color = new Color(0, 0, 0, 0.05f),
			AnchorsPreset = (int)Control.LayoutPreset.FullRect
		};
		uiLayer.AddChild(fade);
		_root2D.AddChild(uiLayer);

		// Static geometry from loops → segment colliders (+ optional fill)
		foreach (var poly in polygons2DInput)
			AddLoopAsSegments(poly.WorldLoop);

		// 2D player
		SpawnPlayer2D();

		// Start player at (0,0) slice space (tweak later to snap to ground)
		var startPos2D = new Vector2(0, 0);
		_player2D.GlobalPosition = startPos2D;
		_uStart = startPos2D.X;
		_uCurrent = _uStart;

		// Make the 2D camera active in Godot 4
		_cam2D.Enabled = true;
	}

	/// <summary>
	/// Leaves the 2D level and returns how far the player moved along U.
	/// </summary>
	public float ExitAndGetUDisplacement()
	{
		// Displacement along U axis in 2D
		float du = _uCurrent - _uStart;

		_root2D?.QueueFree();
		_root2D = null;
		_player2D = null;
		_cam2D = null;

		return du;
	}

	// ---- Helpers ----

	private Vector2 ProjectTo2D(Vector3 pWorld)
	{
		Vector3 r = pWorld - _origin;
		float u = r.Dot(_u);
		float v = r.Dot(_v);
		// Godot 2D +Y is down; invert so +V goes up for platformer feel
		return new Vector2(u, -v);
	}

	private void AddLoopAsSegments(List<Vector3> worldLoop)
	{
		if (worldLoop == null || worldLoop.Count < 2) return;

		// Convert to 2D points
		var pts2D = new List<Vector2>(worldLoop.Count);
		foreach (var wp in worldLoop) pts2D.Add(ProjectTo2D(wp));

		// Static body with per-edge SegmentShape2D
		var sb = new StaticBody2D { Name = "SliceStatic" };
		_root2D.AddChild(sb);

		for (int i = 0; i < pts2D.Count; i++)
		{
			var a = pts2D[i];
			var b = pts2D[(i + 1) % pts2D.Count];

			var shape = new SegmentShape2D { A = a, B = b };
			var cs = new CollisionShape2D { Shape = shape };
			sb.AddChild(cs);

			// Optional debug line
			var line = new Line2D
			{
				DefaultColor = new Color(_tint.R, _tint.G, _tint.B, 0.9f),
				Width = 4f,
				Points = new Vector2[] { a, b },
				ZIndex = 10
			};
			_root2D.AddChild(line);
		}

		// Optional translucent fill for readability
		var fill = new Polygon2D
		{
			Polygon = pts2D.ToArray(),
			Color = new Color(_tint.R, _tint.G, _tint.B, 0.15f),
			ZIndex = 1
		};
		_root2D.AddChild(fill);
	}

	private void SpawnPlayer2D()
	{
		_player2D = new CharacterBody2D { Name = "Player2D" };
		_root2D.AddChild(_player2D);

		// Visual
		var vis = new Node2D();
		var bodyRect = new ColorRect
		{
			Color = new Color(0.2f, 0.8f, 1, 1),
			Size = new Vector2(14, 24),
			PivotOffset = new Vector2(7, 12)
		};
		vis.AddChild(bodyRect);
		_player2D.AddChild(vis);

		// Collider
		var shape = new RectangleShape2D { Size = new Vector2(14, 24) };
		var cs = new CollisionShape2D { Shape = shape };
		_player2D.AddChild(cs);

		// Camera2D (Godot 4: use Enabled)
		_cam2D = new Camera2D
		{
			Zoom = new Vector2(0.8f, 0.8f),
			PositionSmoothingEnabled = true,
			PositionSmoothingSpeed = 8f
		};
		_player2D.AddChild(_cam2D);

		// Controls component (nested Godot type must be 'partial')
		var controller = new Player2DController();
		_player2D.AddChild(controller);
		controller.Connect(nameof(Player2DController.OnUChanged), new Callable(this, nameof(OnUChanged)));
	}

	private void OnUChanged(float u)
	{
		_uCurrent = u;
	}

	// Nested controller must be 'partial' for Godot’s analyzer (GD0001)
	public partial class Player2DController : Node
	{
		[Signal] public delegate void OnUChangedEventHandler(float uValue); // Signal name => "OnUChanged"

		private CharacterBody2D _body;
		private float _speed = 180f;
		private float _jumpVel = -260f;
		private float _gravity = 720f;

		public override void _Ready()
		{
			_body = GetParent<CharacterBody2D>();
		}

		public override void _PhysicsProcess(double delta)
		{
			float dt = (float)delta;
			var v = _body.Velocity;

			// Horizontal (U axis)
			int x = (Input.IsActionPressed("ui_right") ? 1 : 0) - (Input.IsActionPressed("ui_left") ? 1 : 0);
			v.X = x * _speed;

			// Gravity + jump (remember we inverted V in projection)
			v.Y += _gravity * dt;
			if (Input.IsActionJustPressed("ui_accept") && _body.IsOnFloor())
				v.Y = _jumpVel;

			_body.Velocity = v;
			_body.MoveAndSlide();

			// Report new U (X) each frame
			EmitSignal(nameof(OnUChanged), _body.GlobalPosition.X);
		}
	}
}
