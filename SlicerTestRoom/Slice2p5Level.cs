// File: Slice2p5Level.cs
using Godot;
using System;
using System.Collections.Generic;

public partial class Slice2p5Level : Node3D
{
	// --- Payload passed in by SliceManager ---
	public struct Payload
	{
		public Vector3 Origin;                  // plane origin (world)
		public Vector3 U;                       // plane tangent (right in 2D)
		public Vector3 V;                       // plane bitangent (up in 2D)
		public Vector3 N;                       // plane normal (toward camera)
		public List<List<Vector3>> WorldLoops;  // each loop in world space
		public Color Tint;                      // level tint
		public Node3D WorldRootToHide;          // your 3D room to hide during slice
		public CharacterBody3D ReturnPlayer;    // your real player to hide/restore
	}

	// Exports (set in inspector if you want)
	[Export] public float PlatformDepth = 0.25f;     // thickness along N
	[Export] public float VisualOffset = 0.01f;      // push visuals slightly to avoid Z-fight
	[Export] public float PlayerHeight = 1.8f;       // for fallback player
	[Export] public PackedScene PlayerScene;         // optional: your fancy player scene for the slice

	// Internal
	
	private float _uMin, _uMax, _vMin, _vMax;
	private bool _haveBounds;

	
	private Payload _payload;
	private Node3D _container;               // root for generated platforms/visuals
	private CharacterBody3D _slicePlayer;    // the 3D slice-player instance
	private Camera3D _cam;
	private float _uStart;
	private bool _exitQueued;



	public void BuildFromPayload(Payload p)
	{
		_payload = p;

		// Hide the world + real player while in slice
		if (_payload.WorldRootToHide != null) _payload.WorldRootToHide.Visible = false;
		if (_payload.ReturnPlayer != null)
		{
			_payload.ReturnPlayer.Visible = false;
			_payload.ReturnPlayer.SetProcess(false);
			_payload.ReturnPlayer.SetPhysicsProcess(false);
		}

		// Container for everything in this mini-level
		_container = new Node3D { Name = "Slice2p5_Container" };
		AddChild(_container);
		
		// Build platforms (but first compute bounds so the camera can frame things)
		_haveBounds = false;
		foreach (var loop in _payload.WorldLoops)
			AccumulateBounds(loop);

		foreach (var loop in _payload.WorldLoops)
			BuildPlatformFromLoop(loop);


		// Build platforms
		foreach (var loop in _payload.WorldLoops)
			BuildPlatformFromLoop(loop);

		// Spawn 3D player (locked to U&Y movement)
		_slicePlayer = SpawnSlicePlayer();

		// Camera: 2.5D look (orthographic, slightly yawed for depth)
		_cam = MakeSliceCamera();
		AddChild(_cam);
		_cam.MakeCurrent();

		// Start the slice player at (U=0, on plane)
		var startPos = ToWorldFromUV(0, 0) + _payload.N * (PlatformDepth + VisualOffset + 0.25f);
		_slicePlayer.GlobalPosition = startPos;
		_uStart = 0f;
	}

	public float ExitAndGetUDisplacement()
	{
		// Unhide world & restore real player
		if (_payload.WorldRootToHide != null) _payload.WorldRootToHide.Visible = true;

		// compute 2D displacement along U in meters (slice space)
		float uNow = ProjectToU(_slicePlayer.GlobalPosition);
		float du = uNow - _uStart;

		QueueFree(); // remove this slice scene
		return du;
	}

	public override void _Input(InputEvent e)
	{
		if (_exitQueued) return;

		if (e.IsActionPressed("slice_aim") || (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape))
		{
			_exitQueued = true;
			// let SliceManager handle restoring the real player & translation
			EmitSignal(SignalName.RequestExit);
		}
	}

	// --- Signals ---
	[Signal] public delegate void RequestExitEventHandler();

	// =========================================
	// Building
	// =========================================

// Computes signed area of a 2D polygon (positive = CCW, negative = CW)
private float PolygonArea(List<Vector2> pts)
{
	if (pts == null || pts.Count < 3) return 0f;

	float sum = 0f;
	for (int i = 0; i < pts.Count; i++)
	{
		int j = (i + 1) % pts.Count;
		sum += pts[i].X * pts[j].Y - pts[j].X * pts[i].Y;
	}
	return 0.5f * sum;
}

// Computes absolute area of a single triangle given 3 vertices
private float TriangleArea(Vector2 a, Vector2 b, Vector2 c)
{
	return Mathf.Abs((a.X * (b.Y - c.Y) +
					  b.X * (c.Y - a.Y) +
					  c.X * (a.Y - b.Y)) * 0.5f);
}


// Remove duplicates/collinear points and enforce CCW winding
private List<Vector2> CleanPolygon2D(List<Vector2> pts, float eps = 1e-4f)
{
	if (pts == null) return null;

	// 1) Remove consecutive duplicates
	var cleaned = new List<Vector2>(pts.Count);
	Vector2? last = null;
	foreach (var p in pts)
	{
		if (last == null || (p - last.Value).Length() > eps)
		{
			cleaned.Add(p);
			last = p;
		}
	}
	if (cleaned.Count >= 2 && (cleaned[0] - cleaned[^1]).Length() <= eps)
		cleaned.RemoveAt(cleaned.Count - 1);

	// 2) Remove collinear vertices
	if (cleaned.Count >= 3)
	{
		var noCol = new List<Vector2>();
		for (int i = 0; i < cleaned.Count; i++)
		{
			var a = cleaned[(i - 1 + cleaned.Count) % cleaned.Count];
			var b = cleaned[i];
			var c = cleaned[(i + 1) % cleaned.Count];
			var ab = b - a;
			var bc = c - b;
			float cross = ab.X * bc.Y - ab.Y * bc.X;
			if (Mathf.Abs(cross) > eps * eps)
				noCol.Add(b);
		}
		cleaned = noCol;
	}

	// 3) Enforce CCW winding using our custom PolygonArea helper
	if (cleaned.Count >= 3)
	{
		float area = PolygonArea(cleaned); // ✅ use cleaned, not uv
		if (area < 0f) cleaned.Reverse();
	}

	return cleaned;
}


	private void BuildPlatformFromLoop(List<Vector3> worldLoop)
{
	if (worldLoop == null || worldLoop.Count < 3) return;

	// 1) Project to 2D (U,V)
	var uvRaw = new List<Vector2>(worldLoop.Count);
	foreach (var p in worldLoop)
	{
		Vector3 r = p - _payload.Origin;
		float u = r.Dot(_payload.U);
		float v = r.Dot(_payload.V);
		uvRaw.Add(new Vector2(u, v));
	}

	// 2) Clean polygon (dedupe, remove collinear, enforce CCW)
	var uv = CleanPolygon2D(uvRaw);
	if (uv == null || uv.Count < 3) return;

	// Guard against micro-polygons
	if (Mathf.Abs(PolygonArea(uv)) < 1e-4f) return;

	// 3) Triangulate (no convex-decompose needed)
	int[] idx;
	try
	{
		idx = Geometry2D.TriangulatePolygon(uv.ToArray());
	}
	catch
	{
		// Still pathological? bail out gracefully
		return;
	}
	if (idx == null || idx.Length < 3) return;

	// 4) Build collisions using triangles (bullet-proof)
	var staticBody = new StaticBody3D { Name = "PlatformBody" };
	_container.AddChild(staticBody);

	for (int i = 0; i < idx.Length; i += 3)
	{
		var a = uv[idx[i + 0]];
		var b = uv[idx[i + 1]];
		var c = uv[idx[i + 2]];

		// Drop degenerate/very small triangles
		if (TriangleArea(a, b, c) < 1e-5f)
	continue;

		var tri = new CollisionPolygon3D
		{
			Polygon = new Vector2[] { a, b, c },
			Depth = PlatformDepth,
#if DEBUG
			DebugFill = true
#endif
		};
		staticBody.AddChild(tri);
	}

	// 5) Visual top face (same as before): single mesh from all triangles
	var st = new SurfaceTool();
	st.Begin(Mesh.PrimitiveType.Triangles);

	var n = _payload.N; // face toward camera
	var color = new Color(_payload.Tint.R, _payload.Tint.G, _payload.Tint.B, 1f);

	for (int i = 0; i < idx.Length; i += 3)
	{
		var pa = uv[idx[i + 0]];
		var pb = uv[idx[i + 1]];
		var pc = uv[idx[i + 2]];

		var wa = ToWorldFromUV(pa.X, pa.Y) + n * (PlatformDepth + VisualOffset);
		var wb = ToWorldFromUV(pb.X, pb.Y) + n * (PlatformDepth + VisualOffset);
		var wc = ToWorldFromUV(pc.X, pc.Y) + n * (PlatformDepth + VisualOffset);

		st.SetNormal(n); st.SetColor(color); st.AddVertex(wa);
		st.SetNormal(n); st.SetColor(color); st.AddVertex(wb);
		st.SetNormal(n); st.SetColor(color); st.AddVertex(wc);
	}

	var mesh = st.Commit();
	var mi = new MeshInstance3D { Mesh = mesh, Name = "PlatformVisual" };
	_container.AddChild(mi);

	var mat = new StandardMaterial3D
	{
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		AlbedoColor = new Color(color.R * 0.9f, color.G * 0.9f, color.B * 0.9f, 1f)
	};
	if (mesh.GetSurfaceCount() > 0)
		mesh.SurfaceSetMaterial(0, mat);
}


	// =========================================
	// Player/avatar locked to slice
	// =========================================
	private CharacterBody3D SpawnSlicePlayer()
	{
		CharacterBody3D body;

		if (PlayerScene != null)
			body = PlayerScene.Instantiate<CharacterBody3D>();
		else
		{
			// Fallback capsule avatar
			body = new CharacterBody3D();
			var vis = new MeshInstance3D
			{
				Mesh = new CapsuleMesh { Height = PlayerHeight, Radius = 0.35f, RadialSegments = 16 },
			};
			var mat = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.8f, 1f) };
			if (vis.Mesh.GetSurfaceCount() > 0) vis.Mesh.SurfaceSetMaterial(0, mat);
			body.AddChild(vis);

			var col = new CollisionShape3D { Shape = new CapsuleShape3D { Height = PlayerHeight, Radius = 0.35f } };
			body.AddChild(col);
		}

		// Movement locker script (inner class)
		var ctrl = new SliceMoveController
		{
			U = _payload.U,
			N = _payload.N,
			Gravity = 30f,
			WalkSpeed = 8f,
			JumpVelocity = 8f
		};
		body.AddChild(ctrl);

		AddChild(body);
		return body;
	}

	// =========================================
	// 2.5D Camera
	// =========================================
	private Camera3D MakeSliceCamera()
{
	// Build camera facing the slice:
	// right = U, up = V, forward = -N (Godot looks along -Z)
	var right = _payload.U.Normalized();
	var up    = _payload.V.Normalized();
	var fwd   = (-_payload.N).Normalized();

	var cam = new Camera3D
	{
		Projection = Camera3D.ProjectionType.Orthogonal,
		Current = true
	};
	
	cam.Near = 0.01f;
	cam.Far  = 200f;


	// Compute a nice starting frame based on the slice bounds
	float padU = 1.5f; // padding in meters horizontally
	float padV = 1.5f; // padding vertically

	float uCenter = 0f, vCenter = 0f;
	float height  = 12f; // default if we don't have bounds
	if (_haveBounds)
	{
		uCenter = (_uMin + _uMax) * 0.5f;
		vCenter = (_vMin + _vMax) * 0.5f;
		float width  = (_uMax - _uMin) + padU * 2f;
		float vSpan  = (_vMax - _vMin) + padV * 2f;

		// In orthographic projection, Size is the vertical size in world units.
		// Fit vertical span; you can also choose to fit width if you prefer.
		height = MathF.Max(8f, vSpan);

		// Optional: if you want to ensure width also fits on very wide slices,
		// you can scale height by aspect ratio at runtime (see _Process).
	}

	cam.Size = height;

	// Place the camera a few meters out along +N so it looks at the plane
	float dist = 10f;        // how far out (increase if geometry is thick)
	float lift = 2.0f;       // slight upward offset for nicer angle
	Vector3 centerWorld = ToWorldFromUV(uCenter, vCenter);
	Vector3 camPos = centerWorld + _payload.N * dist + up * lift;

	// Slight yaw for 2.5D flavor (optional)
	float yawDeg = 12f;
	Basis baseBasis = new Basis(right, up, fwd);
	Basis yaw = Basis.FromEuler(new Vector3(0, Mathf.DegToRad(yawDeg), 0));
	cam.GlobalTransform = new Transform3D(baseBasis * yaw, camPos);

	return cam;
}


	// =========================================
	// Math helpers
	// =========================================
	private Vector3 ToWorldFromUV(float u, float v)
	{
		return _payload.Origin + _payload.U * u + _payload.V * v;
	}

	private float ProjectToU(Vector3 worldPos)
	{
		var r = worldPos - _payload.Origin;
		return r.Dot(_payload.U);
	}
	
	private void AccumulateBounds(List<Vector3> worldLoop)
{
	foreach (var p in worldLoop)
	{
		Vector3 r = p - _payload.Origin;
		float u = r.Dot(_payload.U);
		float v = r.Dot(_payload.V);
		if (!_haveBounds)
		{
			_uMin = _uMax = u;
			_vMin = _vMax = v;
			_haveBounds = true;
		}
		else
		{
			if (u < _uMin) _uMin = u;
			if (u > _uMax) _uMax = u;
			if (v < _vMin) _vMin = v;
			if (v > _vMax) _vMax = v;
		}
	}
}

private void UpdateCameraFollow(double delta)
{
	if (_cam == null || _slicePlayer == null) return;

	// Keep the camera’s orientation fixed; just move its position
	Vector3 r = _slicePlayer.GlobalPosition - _payload.Origin;
	float u = r.Dot(_payload.U);
	float vCenter = _haveBounds ? (_vMin + _vMax) * 0.5f : 0f;

	Vector3 desiredCenter = ToWorldFromUV(u, vCenter);

	// Must match MakeSliceCamera() offsets
	float dist = 10f;
	float lift = 2.0f;

	Vector3 targetPos = desiredCenter + _payload.N * dist + _payload.V.Normalized() * lift;

	// Smooth follow
	float lerp = Mathf.Clamp((float)(delta * 6.0), 0f, 1f);
	Vector3 newPos = _cam.GlobalPosition.Lerp(targetPos, lerp);

	// Keep rotation, update position
	var t = _cam.GlobalTransform;
	_cam.GlobalTransform = new Transform3D(t.Basis, newPos);
}




	// =========================================
	// Inner movement controller
	// =========================================
	public partial class SliceMoveController : Node
	{
		[Export] public Vector3 U;   // movement axis (horizontal in slice)
		[Export] public Vector3 N;   // plane normal (depth axis)
		[Export] public float Gravity = 30f;
		[Export] public float WalkSpeed = 8f;
		[Export] public float JumpVelocity = 8f;

		private CharacterBody3D _body;
		private Vector3 _up = Vector3.Up;

		public override void _Ready()
		{
			_body = GetParent<CharacterBody3D>();
		}
		



		public override void _PhysicsProcess(double delta)
		{
			float dt = (float)delta;
			var vel = _body.Velocity;

			// Lock depth to the plane (kill velocity along N)
			vel -= N * vel.Dot(N);

			// Inputs along U only
			float x = (Input.IsActionPressed("ui_right") ? 1 : 0) - (Input.IsActionPressed("ui_left") ? 1 : 0);
			Vector3 targetHoriz = U.Normalized() * (x * WalkSpeed);

			// accelerate horizontally
			Vector3 horiz = vel - _up * vel.Dot(_up);
			horiz = horiz.MoveToward(targetHoriz, 20f * dt);
			vel = horiz + _up * vel.Dot(_up);

			// gravity & jump
			vel += _up * (-Gravity * dt);
			if (Input.IsActionJustPressed("ui_accept") && _body.IsOnFloor())
				vel = new Vector3(vel.X, JumpVelocity, vel.Z);

			_body.Velocity = vel;
			_body.MoveAndSlide();
		}
		

	}
					public override void _Process(double delta)
		{
			UpdateCameraFollow(delta);
		}
}
