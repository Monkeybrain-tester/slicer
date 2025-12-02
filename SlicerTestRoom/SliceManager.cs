// File: SlicerTestRoom/SliceManager.cs
using Godot;
using System;
using System.Linq;
using System.Collections.Generic;

/// <summary>
/// Manages taking a 3D cross-section ("slice") of the world,
/// building a 2D level from that slice, and mapping 2D motion
/// back into 3D when you exit the slice.
/// 
/// Key points:
/// - Slice plane is vertical, using camera RIGHT as the normal,
///   with pitch flattened when ForceVertical = true.
/// - 2D space uses:
///     X₂D along _basisU  (horizontal in plane)
///     Y₂D downwards, derived from world Up (basisV = Vector3.Up)
/// - PlayerStart2D is computed using the room floor as a vertical
///   reference, so your height above the floor in 3D is preserved
///   in the 2D slice.
/// </summary>
public partial class SliceManager : Node3D
{
	// ---------- Scene links ----------
	[ExportGroup("Scene Links")]
	[Export] public NodePath PlayerPath;     // CharacterBody3D (your Player1)
	[Export] public NodePath RoomRootPath;   // Parent of sliceable meshes (e.g. LevelTemplate/Room)
	[Export] public NodePath[] TargetMeshes; // Optional explicit mesh list

	[ExportGroup("UI")]
	[Export] public NodePath CrosshairPath;  // Optional crosshair Control

	// ---------- Preview (Decal guide) ----------
	[ExportGroup("Projected Guide")]
	[Export] public bool UseProjectedGuide = true;
	[Export] public Texture2D GuideTexture;   // dashed texture
	[Export] public Color GuideTint = new Color(0.9f, 0.5f, 1, 1);
	[Export] public float GuideHeight = 6.0f; // visual height
	[Export] public float GuideThickness = 0.25f;
	[Export] public float MaxPreviewDistance = 200.0f;

	// ---------- Slice behavior / visuals ----------
	[ExportGroup("Slice Behavior")]
	[Export] public bool KeepBackHalf = false; // keep the sliced-away side as a new MeshInstance3D
	[Export] public bool ForceVertical = true; // zero Y on plane normal (vertical slice)
	[Export] public bool ShowCap = true;       // fill the cut with a cap
	[Export] public Material CapMaterial;      // material for visual cap

	// ---------- 2D Runtime ----------
	[ExportGroup("2D Runtime")]
	[Export] public string Slice2DScenePath = "res://2Dportion/Slice2D.tscn";
	/// <summary>
	/// 3D meters per 2D "slice unit" in projection.
	/// Smaller → more world space per 2D pixel.
	/// </summary>
	[Export] public float SliceUnitsPerPixel = 0.02f;

	// ---------- Input ----------
	[ExportGroup("Input Actions")]
	[Export] public string SliceAimAction = "slice_aim";   // LMB (hold)
	[Export] public string SliceExitAction = "slice_exit"; // RMB / E (in 2D)

	// ---------- Return From 2D ----------
	[ExportGroup("2D → 3D Return")]
	[Export] public float DeadzonePixels = 4f; // ignore tiny 2D jitters on exit

	// ---------- Private state ----------
	private CharacterBody3D _player;
	private Node _roomRoot;
	private Control _crosshair;
	private Camera3D _cam;
	private Decal _guideDecal;

	// Current slice plane (in world)
	private Vector3 _aimOrigin;
	private Vector3 _aimNormal;          // plane normal (camera RIGHT, flattened if ForceVertical)
	private Vector3 _slicePlaneNormal;   // same as _aimNormal, cached for return

	// Basis used to project 3D → 2D (X = horizontal in plane, Y = up)
	private Vector3 _basisU; // horizontal axis in plane
	private Vector3 _basisV; // vertical axis (world Up)

	// ------------------------------------------------------------
	public override void _Ready()
	{
		_player = GetNodeOrNull<CharacterBody3D>(PlayerPath);
		_roomRoot = GetNodeOrNull<Node>(RoomRootPath);
		_crosshair = GetNodeOrNull<Control>(CrosshairPath);
		_cam = GetViewport()?.GetCamera3D();

		EnsureInputActions();

		// Auto-discover sliceable meshes from a group if no explicit targets
		if (TargetMeshes == null || TargetMeshes.Length == 0)
			AutoDiscoverSliceTargetsFromGroup("sliceable");

		// Build preview decal
		if (UseProjectedGuide)
		{
			_guideDecal = new Decal { Visible = false };
			AddChild(_guideDecal);

			if (GuideTexture != null)
			{
				_guideDecal.TextureAlbedo = GuideTexture;
				_guideDecal.Modulate = GuideTint;
				_guideDecal.EmissionEnergy = 1.15f;
			}
			_guideDecal.UpperFade = 0f;
			_guideDecal.LowerFade = 0f;
			_guideDecal.NormalFade = 0f;
		}

		if (_crosshair != null)
			_crosshair.Visible = false;
	}

	public override void _Input(InputEvent e)
	{
		if (e.IsActionPressed(SliceAimAction))
			BeginAim();
		if (e.IsActionReleased(SliceAimAction))
			EndAimAndSlice();
	}

	public override void _Process(double delta)
	{
		if (_guideDecal != null && _guideDecal.Visible)
			UpdatePreview();
	}

	// ------------------------------------------------------------
	// Aim / Preview
	// ------------------------------------------------------------
	private void BeginAim()
	{
		if (_crosshair != null) _crosshair.Visible = true;
		if (_guideDecal != null) _guideDecal.Visible = true;

		// Hard-lock pitch to horizon while aiming (if Player1 implements these)
		if (_player is Player1 p1)
			p1.LockPitchToHorizon();

		UpdatePreview();
	}

	private void EndAimAndSlice()
	{
		if (_crosshair != null) _crosshair.Visible = false;
		if (_guideDecal != null) _guideDecal.Visible = false;

		// Unlock pitch
		if (_player is Player1 p1)
			p1.UnlockPitch();

		// 1) Capture plane origin + normal from camera or player
		if (_cam != null)
		{
			_aimOrigin = _cam.GlobalPosition;
			_aimNormal = _cam.GlobalTransform.Basis.X; // camera RIGHT
		}
		else if (_player != null)
		{
			_aimOrigin = _player.GlobalPosition;
			_aimNormal = _player.GlobalTransform.Basis.X;
		}
		else
		{
			_aimOrigin = Vector3.Zero;
			_aimNormal = Vector3.Right;
		}

		// 2) Flatten tilt (vertical slice)
		if (ForceVertical)
		{
			_aimNormal.Y = 0f;
			if (_aimNormal.LengthSquared() < 1e-6f)
				_aimNormal = Vector3.Right;
		}
		_aimNormal = _aimNormal.Normalized();

		// 3) Build 2D projection basis
		//    - basisV = world Up (2D Y is up before flipping)
		//    - basisU = basisV × normal (horizontal along slice plane)
		_basisV = Vector3.Up;
		_basisU = _basisV.Cross(_aimNormal).Normalized();
		if (_basisU.LengthSquared() < 1e-6f)
			_basisU = Vector3.Forward;
		_basisV = _basisV.Normalized();

		// 4) Cache slice normal for return mapping
		_slicePlaneNormal = _aimNormal;

		GD.Print($"[SliceManager] Slice Plane -> Normal:{_slicePlaneNormal}  U:{_basisU}  V:{_basisV}");

		// 5) Execute slice
		ExecuteSlice();
	}

	private void UpdatePreview()
	{
		if (_cam == null && _player == null) return;

		var origin = _cam != null ? _cam.GlobalPosition : _player.GlobalPosition;
		var cb = _cam != null ? _cam.GlobalTransform.Basis : _player.GlobalTransform.Basis;

		// plane normal = camera RIGHT
		_aimNormal = cb.X;
		if (ForceVertical) _aimNormal.Y = 0f;
		_aimNormal = _aimNormal.Normalized();

		// width axis = camera FORWARD (so dashes go away from you)
		Vector3 widthAxis = (-cb.Z).Normalized();
		Vector3 heightAxis = _aimNormal.Cross(widthAxis).Normalized();
		if (heightAxis.LengthSquared() < 1e-6f)
			heightAxis = Vector3.Up;

		var space = GetWorld3D().DirectSpaceState;

		// forward ray
		var front = PhysicsRayQueryParameters3D.Create(origin, origin + widthAxis * MaxPreviewDistance);
		front.CollideWithBodies = true;
		front.CollideWithAreas = false;
		var hitF = space.IntersectRay(front);

		// backward ray
		var back = PhysicsRayQueryParameters3D.Create(origin, origin - widthAxis * MaxPreviewDistance);
		back.CollideWithBodies = true;
		back.CollideWithAreas = false;
		var hitB = space.IntersectRay(back);

		float dF = (hitF.Count > 0 && hitF.ContainsKey("position"))
			? ((Vector3)hitF["position"] - origin).Length()
			: MaxPreviewDistance;

		float dB = (hitB.Count > 0 && hitB.ContainsKey("position"))
			? ((Vector3)hitB["position"] - origin).Length()
			: MaxPreviewDistance;

		Vector3 center = origin + widthAxis * ((dF - dB) * 0.5f);

		if (_guideDecal != null)
		{
			_guideDecal.Size = new Vector3(
				Mathf.Max(0.1f, dF + dB),
				Mathf.Max(0.1f, GuideHeight),
				Mathf.Max(0.02f, GuideThickness)
			);
			Basis decalBasis = new Basis(widthAxis, heightAxis, _aimNormal);
			_guideDecal.GlobalTransform = new Transform3D(decalBasis, center);
		}
	}

	// ------------------------------------------------------------
	// Slice execution
	// ------------------------------------------------------------
	private void ExecuteSlice()
	{
		var worldPlane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));

		// For 3D cap (optional) and for 2D loops
		var segmentsWorld = new List<(Vector3 A, Vector3 B)>();
		var capPointsWorld = new List<Vector3>();

		// 3 points on the plane to transform into local space
		Vector3 t = BuildPlaneTangent(_aimNormal);
		Vector3 b = _aimNormal.Cross(t);
		Vector3 wp0 = _aimOrigin;
		Vector3 wp1 = _aimOrigin + t;
		Vector3 wp2 = _aimOrigin + b;

		foreach (var path in TargetMeshes ?? Array.Empty<NodePath>())
		{
			var mi = GetNodeOrNull<MeshInstance3D>(path);
			if (mi == null || mi.Mesh == null) continue;

			var am = ConvertToArrayMesh(mi.Mesh);
			if (am == null) continue;

			// world plane -> local plane
			var toLocal = mi.GlobalTransform.AffineInverse();
			Vector3 lp0 = toLocal * wp0;
			Vector3 lp1 = toLocal * wp1;
			Vector3 lp2 = toLocal * wp2;
			Vector3 lN = (lp1 - lp0).Cross(lp2 - lp0).Normalized();
			Plane localPlane = new Plane(lN, lp0.Dot(lN));

			// slice
			var res = SliceMeshUtility.Slice(am, localPlane);

			// keep front mesh in place
			if (res.FrontMesh != null)
				mi.Mesh = res.FrontMesh;

			// optional back half
			if (KeepBackHalf && res.BackMesh != null)
			{
				var back = new MeshInstance3D
				{
					Mesh = res.BackMesh,
					GlobalTransform = mi.GlobalTransform
				};
				mi.GetParent().AddChild(back);
			}

			// collect cross-section segments
			if (res.SectionSegments != null && res.SectionSegments.Count > 0)
			{
				foreach (var seg in res.SectionSegments)
				{
					var aW = mi.GlobalTransform * seg.A;
					var bW = mi.GlobalTransform * seg.B;
					if ((bW - aW).LengthSquared() > 1e-10f)
					{
						segmentsWorld.Add((aW, bW));
						capPointsWorld.Add(aW);
						capPointsWorld.Add(bW);
					}
				}
			}
			else if (res.SectionLoop != null && res.SectionLoop.Count > 0)
			{
				foreach (var p in res.SectionLoop)
					capPointsWorld.Add(mi.GlobalTransform * p);
			}
		}

		// Project segments to 2D slice space
		var segs2D = new List<(Vector2 A, Vector2 B)>(segmentsWorld.Count);
		foreach (var (A, B) in segmentsWorld)
		{
			Vector3 ra = A - _aimOrigin;
			Vector3 rb = B - _aimOrigin;
			float ua = ra.Dot(_basisU);
			float va = ra.Dot(_basisV);
			float ub = rb.Dot(_basisU);
			float vb = rb.Dot(_basisV);

			// y2D = -v / scale  (up becomes negative)
			Vector2 a2 = new Vector2(ua / SliceUnitsPerPixel, -va / SliceUnitsPerPixel);
			Vector2 b2 = new Vector2(ub / SliceUnitsPerPixel, -vb / SliceUnitsPerPixel);
			segs2D.Add((a2, b2));
		}

		// Assemble loops from segments
		var loops = BuildLoopsFromSegments2D(segs2D, eps: 0.5f);

		// Classify outer vs holes and subtract holes using Geometry2D.ClipPolygons
		var finalPolys = BuildSolidPolygonsFromLoops(loops);

		// Debug: show unscaled Y range
		if (GetPolyYRange(finalPolys, out float minY, out float maxY))
		{
			int totalPts = finalPolys.Sum(p => p?.Count ?? 0);
			GD.Print($"[SliceManager DEBUG] Polygons2D loops={finalPolys.Count}, totalPts={totalPts}, Y-range=[{minY}, {maxY}]");
		}
		else
		{
			GD.Print("[SliceManager DEBUG] No polygons generated.");
		}

		// Optional visual cap in 3D
		if (ShowCap && capPointsWorld.Count >= 3)
		{
			var cap = BuildCap(worldPlane, capPointsWorld, CapMaterial);
			if (cap != null)
				AddChild(cap);
		}

		// hand off to 2D
		var segments2D = new List<Vector2[]>();
		foreach (var (A, B) in segs2D)
			segments2D.Add(new[] { A, B });

		LaunchSlice2D(finalPolys, segments2D);
	}

	// ------------------------------------------------------------
	// 2D scene launch / return
	// ------------------------------------------------------------
	private void LaunchSlice2D(List<List<Vector2>> polygons2D, List<Vector2[]> segments2D)
	{
		var scene = GD.Load<PackedScene>(Slice2DScenePath);
		if (scene == null)
		{
			GD.PushError($"[SliceManager] Cannot load 2D scene: {Slice2DScenePath}");
			return;
		}

		var slice = scene.Instantiate<Node2D>() as Node2D;
		if (slice is not SliceLevel2D level)
		{
			GD.PushError("Slice2D.tscn root must have SliceLevel2D.cs attached.");
			return;
		}

		// Turn off 3D camera while in 2D
		if (_cam != null)
			_cam.Current = false;

		// Pack geometry
		level.Polygons2D = polygons2D ?? new();
		level.Segments2D = segments2D ?? new();

		// --- Compute PlayerStart2D using room floor as reference ---

		// 1) Raycast to room floor under player
		Vector3 floorHit3D;
		bool gotFloor = TryRaycastRoomFloor(out floorHit3D);
		float worldDeltaY = 0f;

		if (gotFloor)
			worldDeltaY = _player.GlobalPosition.Y - floorHit3D.Y;
		else
			GD.PushWarning("[SliceManager] TryRaycastRoomFloor failed; using plane projection only.");

		// 2) Pick a 2D "floor" line from polygon Y range (unscaled)
		float polyMinY, polyMaxY;
		float floor2D_Y;
		const float floorMargin = 20f; // in slice units

		if (GetPolyYRange(polygons2D, out polyMinY, out polyMaxY))
		{
			floor2D_Y = polyMaxY + floorMargin;
		}
		else
		{
			floor2D_Y = 0f; // fallback baseline
		}

		// 3) Project player's XZ onto slice plane and into 2D
		var slicePlane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));
		Vector3 projectedOnPlane = ProjectPointToPlane(_player.GlobalPosition, slicePlane);
		Vector2 projected2D = ProjectWorldTo2D(projectedOnPlane);

		// 4) Convert world vertical offset into 2D offset
		//    basisV = world Up, y2D = -v / scale => deltaY2D = -worldDeltaY / scale
		float deltaY2D = -worldDeltaY / SliceUnitsPerPixel;
		Vector2 start2D = new Vector2(projected2D.X, floor2D_Y + deltaY2D);

		level.PlayerStart2D = start2D;

		GD.Print(
			"[SliceManager DEBUG] LaunchSlice2D (room-floor-based):\n" +
			$"  playerPos3D    = {_player.GlobalPosition}\n" +
			$"  gotFloor       = {gotFloor}\n" +
			$"  floorHit3D     = {floorHit3D}\n" +
			$"  worldDeltaY    = {worldDeltaY}\n" +
			$"  polyYRange     = [{polyMinY}, {polyMaxY}]\n" +
			$"  floor2D_Y      = {floor2D_Y}\n" +
			$"  projected2D    = {projected2D}\n" +
			$"  deltaY2D       = {deltaY2D}\n" +
			$"  PlayerStart2D  = {start2D}\n" +
			$"  SliceUnitsPerPixel = {SliceUnitsPerPixel}"
		);

		// Add 2D scene + hide 3D player
		GetTree().CurrentScene.AddChild(level);
		if (_player != null)
			_player.Visible = false;

		level.Connect(SliceLevel2D.SignalName.SliceExit, new Callable(this, nameof(OnSliceExit2D)));
	}

	private void OnSliceExit2D(Vector2 delta2D)
	{
		GD.Print($"[SliceManager DEBUG] OnSliceExit2D: raw delta2D={delta2D}");

		// Deadzone tiny jitter from 2D frame
		if (Mathf.Abs(delta2D.X) < DeadzonePixels) delta2D.X = 0f;
		if (Mathf.Abs(delta2D.Y) < DeadzonePixels) delta2D.Y = 0f;

		// 2D → 3D: same basis as projection, undo V sign flip
		Vector3 delta3D =
			_basisU * (delta2D.X * SliceUnitsPerPixel) +
			_basisV * (-delta2D.Y * SliceUnitsPerPixel);

		var plane = new Plane(_aimNormal, _aimOrigin.Dot(_aimNormal));
		Vector3 startOnPlane = ProjectPointToPlane(_player.GlobalPosition, plane);
		Vector3 newPos = startOnPlane + delta3D;

		_player.Velocity = Vector3.Zero;
		_player.GlobalPosition = newPos;

		if (_cam != null)
			_cam.Current = true;

		_player.Visible = true;

		GD.Print(
			"[SliceManager DEBUG] Return mapping:\n" +
			$"  DeadzonePixels = {DeadzonePixels}\n" +
			$"  cleaned delta2D = {delta2D}\n" +
			$"  delta3D   = {delta3D}\n" +
			$"  startOnPlane = {startOnPlane}\n" +
			$"  newPos    = {newPos}"
		);
		GD.Print($"[SliceManager] Returned to 3D. Δ3D={delta3D}, newPos={newPos}");
	}

	// ------------------------------------------------------------
	// Helpers
	// ------------------------------------------------------------
	private void EnsureInputActions()
	{
		if (!InputMap.HasAction(SliceAimAction))
		{
			InputMap.AddAction(SliceAimAction);
			InputMap.ActionAddEvent(SliceAimAction, new InputEventMouseButton { ButtonIndex = MouseButton.Left });
		}
		if (!InputMap.HasAction(SliceExitAction))
		{
			InputMap.AddAction(SliceExitAction);
			InputMap.ActionAddEvent(SliceExitAction, new InputEventMouseButton { ButtonIndex = MouseButton.Right });
			InputMap.ActionAddEvent(SliceExitAction, new InputEventKey { Keycode = Key.E });
			InputMap.ActionAddEvent(SliceExitAction, new InputEventKey { Keycode = Key.Escape });
		}
	}

	private static Vector3 BuildPlaneTangent(Vector3 n)
	{
		Vector3 up = MathF.Abs(n.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		return up.Cross(n).Normalized();
	}

	private static ArrayMesh ConvertToArrayMesh(Mesh mesh)
	{
		if (mesh is ArrayMesh am) return am;
		var newAM = new ArrayMesh();
		int sc = mesh.GetSurfaceCount();
		for (int s = 0; s < sc; s++)
			newAM.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, mesh.SurfaceGetArrays(s));
		return newAM;
	}

	private MeshInstance3D BuildCap(Plane plane, List<Vector3> pointsWorld, Material mat)
	{
		if (pointsWorld == null || pointsWorld.Count < 3) return null;

		Vector3 n = plane.Normal.Normalized();
		Vector3 t = BuildPlaneTangent(n);
		Vector3 b = n.Cross(t);
		Vector3 c = Vector3.Zero;
		foreach (var p in pointsWorld) c += p;
		c /= pointsWorld.Count;

		var sorted = pointsWorld
			.Select(p =>
			{
				Vector3 r = p - c;
				float u = r.Dot(t);
				float v = r.Dot(b);
				float a = Mathf.Atan2(v, u);
				return (p, a);
			})
			.OrderBy(x => x.a)
			.Select(x => x.p)
			.ToList();

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		for (int i = 0; i < sorted.Count; i++)
		{
			Vector3 a = sorted[i];
			Vector3 c2 = sorted[(i + 1) % sorted.Count];
			st.SetNormal(n); st.AddVertex(c);
			st.SetNormal(n); st.AddVertex(a);
			st.SetNormal(n); st.AddVertex(c2);
		}

		var capMesh = st.Commit();
		if (capMesh == null) return null;
		if (mat != null && capMesh.GetSurfaceCount() > 0)
			capMesh.SurfaceSetMaterial(0, mat);

		return new MeshInstance3D { Mesh = capMesh };
	}

	private static Vector3 ProjectPointToPlane(Vector3 p, Plane plane)
	{
		float d = plane.DistanceTo(p);
		return p - plane.Normal * d;
	}

	private Vector2 ProjectWorldTo2D(Vector3 wp)
	{
		Vector3 r = wp - _aimOrigin;
		float u = r.Dot(_basisU);
		float v = r.Dot(_basisV);
		return new Vector2(u / SliceUnitsPerPixel, -v / SliceUnitsPerPixel);
	}

	private void AutoDiscoverSliceTargetsFromGroup(string group = "sliceable")
	{
		var list = new List<NodePath>();
		foreach (var n in GetTree().GetNodesInGroup(group))
		{
			if (n is MeshInstance3D mi && mi.Mesh != null)
				list.Add(mi.GetPath());
		}
		if (list.Count > 0)
			TargetMeshes = list.ToArray();
	}

	private bool GetPolyYRange(List<List<Vector2>> polys, out float minY, out float maxY)
	{
		minY = float.MaxValue;
		maxY = float.MinValue;
		if (polys == null || polys.Count == 0) return false;

		bool any = false;
		foreach (var poly in polys)
		{
			if (poly == null || poly.Count == 0) continue;
			any = true;
			foreach (var p in poly)
			{
				if (p.Y < minY) minY = p.Y;
				if (p.Y > maxY) maxY = p.Y;
			}
		}
		return any;
	}

	private bool TryRaycastRoomFloor(out Vector3 hitPos)
	{
		hitPos = Vector3.Zero;
		if (_player == null) return false;

		var space = GetWorld3D().DirectSpaceState;
		Vector3 from = _player.GlobalPosition + Vector3.Up * 0.5f;
		Vector3 to = _player.GlobalPosition + Vector3.Down * 30f;

		var q = PhysicsRayQueryParameters3D.Create(from, to);
		q.CollideWithAreas = false;
		q.CollideWithBodies = true;
		// Optionally set q.CollisionMask to limit to floor layer(s)

		var hit = space.IntersectRay(q);
		if (hit.Count == 0 || !hit.ContainsKey("position"))
			return false;

		hitPos = (Vector3)hit["position"];
		return true;
	}

	// ---------- 2D loop & hole helpers ----------

	private static float Area2D(IReadOnlyList<Vector2> pts)
	{
		if (pts == null || pts.Count < 3) return 0f;
		double s = 0;
		for (int i = 0; i < pts.Count; i++)
		{
			var a = pts[i];
			var b = pts[(i + 1) % pts.Count];
			s += (double)a.X * b.Y - (double)a.Y * b.X;
		}
		return (float)(0.5 * s);
	}

	private static Vector2 Quantize(Vector2 p, float eps)
	{
		float k = 1f / eps;
		return new Vector2(Mathf.Round(p.X * k) / k, Mathf.Round(p.Y * k) / k);
	}

	/// <summary>
	/// Greedy assembly of closed loops from 2-point line segments in 2D.
	/// Assumes endpoints are already snapped via Quantize.
	/// </summary>
	private static List<List<Vector2>> BuildLoopsFromSegments2D(List<(Vector2 A, Vector2 B)> segs, float eps = 0.25f)
	{
		var loops = new List<List<Vector2>>();
		if (segs == null || segs.Count == 0) return loops;

		var byPoint = new Dictionary<Vector2, List<int>>();
		var used = new bool[segs.Count];

		for (int i = 0; i < segs.Count; i++)
		{
			var a = Quantize(segs[i].A, eps);
			var b = Quantize(segs[i].B, eps);
			segs[i] = (a, b);

			if (!byPoint.TryGetValue(a, out var la)) { la = new List<int>(); byPoint[a] = la; }
			if (!byPoint.TryGetValue(b, out var lb)) { lb = new List<int>(); byPoint[b] = lb; }
			la.Add(i);
			lb.Add(i);
		}

		for (;;)
		{
			int start = -1;
			for (int i = 0; i < segs.Count; i++)
			{
				if (!used[i]) { start = i; break; }
			}
			if (start == -1) break;

			var a0 = segs[start].A;
			var b0 = segs[start].B;

			var loop = new List<Vector2> { a0, b0 };
			used[start] = true;

			Vector2 curr = b0;
			while (!curr.IsEqualApprox(a0))
			{
				if (!byPoint.TryGetValue(curr, out var cand)) break;

				int pick = -1;
				foreach (var idx in cand)
					if (!used[idx]) { pick = idx; break; }
				if (pick == -1) break;

				var s = segs[pick];
				Vector2 next = s.A.IsEqualApprox(curr) ? s.B : s.A;
				used[pick] = true;
				curr = next;

				if (!curr.IsEqualApprox(loop[^1]))
					loop.Add(curr);

				if (loop.Count > 4096) break;
			}

			if (loop.Count >= 3)
			{
				if (!curr.IsEqualApprox(a0))
					loop.Add(a0);
				loops.Add(loop);
			}
		}

		return loops;
	}

	private static bool PointInPoly(Vector2 p, IReadOnlyList<Vector2> poly)
	{
		bool inside = false;
		for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
		{
			var a = poly[i]; var b = poly[j];
			bool intersect =
				((a.Y > p.Y) != (b.Y > p.Y)) &&
				(p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + 1e-20f) + a.X);
			if (intersect) inside = !inside;
		}
		return inside;
	}

	private static List<Vector2[]> SubtractHoles(Vector2[] outer, List<Vector2[]> holes)
	{
		var solids = new List<Vector2[]> { outer };

		foreach (var h in holes)
		{
			var next = new List<Vector2[]>();
			foreach (var s in solids)
			{
				var arr = Geometry2D.ClipPolygons(s, h);
				if (arr != null && arr.Count > 0)
					next.AddRange(arr);
			}
			solids = next;
			if (solids.Count == 0) break;
		}
		return solids;
	}

	private static List<List<Vector2>> BuildSolidPolygonsFromLoops(List<List<Vector2>> loops)
	{
		if (loops == null || loops.Count == 0)
			return new List<List<Vector2>>();

		// Normalize orientation (CCW) and sort by area
		var normalized = new List<(List<Vector2> poly, int idx, float area)>();
		for (int i = 0; i < loops.Count; i++)
		{
			var poly = loops[i];
			if (poly == null || poly.Count < 3) continue;

			// Copy so we can safely reverse
			var clean = new List<Vector2>(poly);
			float area = Area2D(clean);
			if (area < 0f)
			{
				clean.Reverse();
				area = -area;
			}
			normalized.Add((clean, i, area));
		}

		normalized = normalized
			.OrderByDescending(x => x.area)
			.ToList();

		var outers = normalized.Select(x => x.poly).ToList();
		var holeBuckets = new Dictionary<int, List<Vector2[]>>();

		// For each smaller loop, if centroid lies inside a bigger one, treat as a hole
		for (int i = 0; i < normalized.Count; i++)
		{
			var (polyI, idxI, areaI) = normalized[i];

			// Compute centroid
			Vector2 centroid = Vector2.Zero;
			foreach (var p in polyI) centroid += p;
			centroid /= polyI.Count;

			for (int j = 0; j < normalized.Count; j++)
			{
				if (i == j) continue;
				var (polyJ, idxJ, areaJ) = normalized[j];
				if (areaJ <= areaI) continue; // only consider bigger candidates

				if (PointInPoly(centroid, polyJ))
				{
					if (!holeBuckets.TryGetValue(idxJ, out var list))
					{
						list = new List<Vector2[]>();
						holeBuckets[idxJ] = list;
					}
					list.Add(polyI.ToArray());
					break;
				}
			}
		}

		var finalPolys = new List<List<Vector2>>();

		foreach (var (poly, idx, _) in normalized)
		{
			if (!holeBuckets.TryGetValue(idx, out var holes))
			{
				// No holes → just add as a solid
				finalPolys.Add(poly);
				continue;
			}

			var solids = SubtractHoles(poly.ToArray(), holes);
			foreach (var s in solids)
			{
				if (s != null && s.Length >= 3)
					finalPolys.Add(new List<Vector2>(s));
			}
		}

		if (finalPolys.Count == 0)
			finalPolys = loops; // fallback

		return finalPolys;
	}
}
